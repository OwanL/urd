using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Usage;

namespace Urd.Core.Execution;

// Runs and closes one borrowed client's streamed response; request preparation stays with its owner.
internal static class LlmResponseRunner {
  internal sealed record Result(
      Exception? Failure,
      bool OutputObserved,
      IReadOnlyList<LlmToolCall> ToolCalls,
      LlmProviderResponse? ProviderResponse,
      UsageDetails? Usage);

  internal static async Task<Result> RunAsync(IChatClient borrowedClient,
      IReadOnlyList<ChatMessage> messages, ChatOptions options, Guid requestId,
      Func<LlmResponseChunk, Task> saveChunkAsync,
      Func<string, string?, Task> recordToolCallAsync,
      CancellationToken cancellation) {
    using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    var writer = new LlmResponseWriter(requestId, saveChunkAsync, recordToolCallAsync);
    var response = new LlmResponseAccumulator(writer);
    (Exception? failure, bool outputObserved) = await StreamAsync(
        borrowedClient, messages, options, requestStop, writer, response, cancellation);
    if (failure is ProviderOperationException && !cancellation.IsCancellationRequested) {
      try { await writer.FlushAsync(); }
      catch (Exception saveFailure) { failure = saveFailure; }
    }

    return new(failure, outputObserved, response.ToolCalls, response.ProviderResponse, response.Usage);
  }

  private static async Task<(Exception? Failure, bool OutputObserved)> StreamAsync(IChatClient borrowedClient,
      IReadOnlyList<ChatMessage> messages, ChatOptions options, CancellationTokenSource requestStop,
      LlmResponseWriter writer, LlmResponseAccumulator response, CancellationToken cancellation) {
    IAsyncEnumerator<ChatResponseUpdate>? updates = null;
    Task<bool>? pendingRead = null;
    Exception? failure = null;
    bool outputObserved = false;
    try {
      cancellation.ThrowIfCancellationRequested();
      updates = borrowedClient.GetStreamingResponseAsync(messages, options, requestStop.Token)
          .GetAsyncEnumerator(requestStop.Token);

      while (await WaitForUpdateAndFlushAsync(writer, pendingRead ??= updates.MoveNextAsync().AsTask(), cancellation)) {
        pendingRead = null;
        cancellation.ThrowIfCancellationRequested();
        outputObserved |= updates.Current.Contents.Any(content => content is not UsageContent);
        await response.AddUpdateAsync(updates.Current);
      }

      pendingRead = null;
      cancellation.ThrowIfCancellationRequested();
      await response.FinishAsync();
    } catch (Exception exception) {
      failure = exception;
    }

    await CancelAndCloseResponseAsync(requestStop, pendingRead, updates, failure);
    return (failure, outputObserved);
  }

  private static async Task<bool> WaitForUpdateAndFlushAsync(LlmResponseWriter writer,
      Task<bool> pendingRead, CancellationToken cancellation) {
    Task? flushDelay = writer.FlushDelay;
    if (flushDelay is not null &&
        (flushDelay.IsCompleted || await Task.WhenAny(pendingRead, flushDelay) == flushDelay)) {
      cancellation.ThrowIfCancellationRequested();
      // Flushing clears the deadline; only another update can start a new one.
      await writer.FlushAsync();
    }

    return await pendingRead;
  }

  // Attempts every step even when an earlier one fails. A cleanup failure leaves the
  // execution unsettled, so it is thrown to the owner instead of becoming a failed request.
  private static async Task CancelAndCloseResponseAsync(CancellationTokenSource requestStop,
      Task<bool>? pendingRead, IAsyncEnumerator<ChatResponseUpdate>? updates, Exception? requestFailure) {
    Exception? cancelFailure = await CaptureFailureAsync(() => new ValueTask(requestStop.CancelAsync()));
    if (pendingRead is not null) {
      // A read abandoned by cancellation is expected to fail. Wait for it anyway,
      // because the enumerator must be idle before it is disposed.
      _ = await CaptureFailureAsync(() => new ValueTask(pendingRead));
    }

    Exception? updatesFailure = updates is null ? null : await CaptureFailureAsync(updates.DisposeAsync);
    if (cancelFailure is null && updatesFailure is null)
      return;

    Exception?[] failures = [requestFailure, cancelFailure, updatesFailure];
    throw new AggregateException("The request could not be cleaned up.", failures.OfType<Exception>());
  }

  private static async Task<Exception?> CaptureFailureAsync(Func<ValueTask> action) {
    try {
      await action();
      return null;
    } catch (Exception exception) {
      return exception;
    }
  }
}
