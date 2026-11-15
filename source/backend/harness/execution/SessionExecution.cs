using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Compaction;
using Urd.Core.Sessions;
using Urd.Core.Providers;
using Urd.Core.Preferences;
using Urd.Core.Tools;
using Urd.Core.Usage;

namespace Urd.Core.Execution;

// Runs each LLM request: prepare context, stream and save the response, then run its tool calls.
// Owns request cancellation and stream cleanup; SessionToolExecution owns tool batches.
// SessionExecutionDriver chooses the next request and waits for cleanup before settling the execution.
internal sealed class SessionExecution : IDisposable {
  private readonly SessionBase session;
  private readonly SessionExecutionRecorder recorder;
  private readonly Func<ProviderConnection, IChatClient> createChatClient; // Returns borrowed clients.
  private readonly LlmSelection selection;
  private readonly StartingContextManager startingContextManager;
  private StartingContext? startingContext;
  private IChatClient? chatClient;
  private readonly SessionToolExecution toolExecution;
  private readonly CancellationTokenSource stop = new();
  private Task? stopCallbacks;
  private readonly ManualCompactionRequest? manualCompactionRequest;

  internal SessionExecution(SessionExecutionRecorder recorder, SessionBase session,
      Func<ProviderConnection, IChatClient> createChatClient, LlmSelection selection,
      StartingContextManager startingContextManager, IReadOnlyList<AgentTool>? tools = null,
      PendingToolCalls? pendingToolCalls = null, ManualCompactionRequest? compaction = null) {
    this.recorder = recorder;
    this.session = session;
    this.createChatClient = createChatClient;
    this.selection = selection;
    this.startingContextManager = startingContextManager;
    toolExecution = new SessionToolExecution(session, recorder, tools ?? [],
        pendingToolCalls ?? new PendingToolCalls(), selection.SupportsImages, stop.Token);
    manualCompactionRequest = compaction;
    IsCompacting = compaction is not null;
  }

  internal ExecutionIdentity Identity => recorder.Identity;
  internal SessionExecutionRecorder Recorder => recorder;
  internal Guid ExecutionId => Identity.ExecutionId;
  internal bool IsStopping => stop.IsCancellationRequested;
  internal bool IsManualCompaction => manualCompactionRequest is not null;
  internal bool IsCompacting { get; set; }
  internal bool HasPendingTools => toolExecution.HasRunningBackgroundTools;
  internal Task JoinPendingToolsAsync() => toolExecution.JoinBackgroundToolsAsync();

  // Completes once every callback registered on the stop token has finished.
  internal Task StopCallbacks => stopCallbacks ?? Task.CompletedTask;

  // Called by the session while it holds its lock. Repeated calls share one task.
  internal Task StopAsync() {
    if (stopCallbacks is not null)
      return stopCallbacks;
    stopCallbacks = stop.CancelAsync();
    toolExecution.CancelBackgroundTools();
    return stopCallbacks;
  }

  // Called by the session only after every request and StopCallbacks have finished
  // and the execution's result has been saved.
  public void Dispose() => stop.Dispose();

  // Called sequentially outside the session lock. Foreground calls join here;
  // registered background calls remain owned until the session drains them.
  internal async Task<ExecutionOutcome> RunRequestAsync(LlmRequest request) {
    LlmRequestResult result = await SendLlmRequestAsync(request);
    // Retry only the rejected model exchange, never a tool or any observed partial output.
    bool canCompactAndRetry = result.Failure == ProviderFailure.ContextWindowExceeded
        && !result.OutputObserved && !IsStopping;
    if (canCompactAndRetry && await session.CanRetryOverflowAsync(ExecutionId, request.RequestId)) {
      try {
        LlmRequest? reducedRequest = await CompactHistoryAsync(request.RequestId, CompactionReason.Overflow);
        if (reducedRequest is not null)
          result = await SendLlmRequestAsync(reducedRequest, allowAutomaticCompaction: false);
      } catch (Exception failure) {
        result = CreateRequestResult(request.RequestId, failure);
      }
    }

    bool requestSaved = await recorder.CompleteRequestAsync(result);
    if (!requestSaved)
      return ExecutionOutcome.Cancelled;
    if (result.Outcome != ExecutionOutcome.Completed)
      return result.Outcome;

    return await toolExecution.RunBatchAsync(request.RequestId, result.ToolCalls ?? [], startingContext?.Tools ?? []);
  }

  internal async Task<ExecutionOutcome> RunCompactionAsync() {
    try {
      if (await CompactHistoryAsync(null, CompactionReason.Manual, manualCompactionRequest!.Focus) is null)
        await session.RecordCompactionErrorAsync(ExecutionId, "No older history can be compacted without dropping recent context.");
      return IsStopping ? ExecutionOutcome.Cancelled : ExecutionOutcome.Completed;
    } catch (OperationCanceledException) when (IsStopping) {
      return ExecutionOutcome.Cancelled;
    } catch (CompactionSaveException) {
      throw;
    } catch (Exception failure) {
      await session.RecordCompactionErrorAsync(ExecutionId, failure is ProviderOperationException provider
          ? provider.Failure.ToString() : "History compaction failed; the previous context is unchanged.");
      return ExecutionOutcome.Failed;
    }
  }

  // Summary work runs outside the session lock. The commit rechecks Stop, revision and prior boundary.
  private async Task<LlmRequest?> CompactHistoryAsync(Guid? requestId, CompactionReason reason, string? focus = null,
      ModelHistory? currentHistory = null) {
    HistoryCompactionPlan? plan = await CreateCompactionPlanAsync(requestId, reason, focus, currentHistory);
    if (plan is null)
      return null;
    await session.SetCompactingAsync(ExecutionId, true);
    try {
      GeneratedCompactionSummary result = await GenerateHistorySummaryAsync(plan);
      stop.Token.ThrowIfCancellationRequested();
      bool saved;
      try {
        saved = await session.SaveCompactionAsync(ExecutionId, plan, result);
      } catch (Exception failure) {
        throw new CompactionSaveException(failure);
      }
      if (!saved)
        throw new OperationCanceledException("The compaction boundary was rejected.");
      ModelHistory compactedHistory = await session.ReadModelHistoryAsync(ExecutionId, requestId);
      return new(requestId ?? Guid.Empty, compactedHistory.ReadMessages(), compactedHistory);
    } finally {
      await session.SetCompactingAsync(ExecutionId, IsManualCompaction);
    }
  }

  private async Task<HistoryCompactionPlan?> CreateCompactionPlanAsync(Guid? requestId, CompactionReason reason,
      string? focus, ModelHistory? currentHistory) {
    stop.Token.ThrowIfCancellationRequested();
    UserPreferences preferences = session.ReadPreferences();
    if (reason == CompactionReason.Threshold && selection.ContextWindow is null
        && preferences.CompactionTriggerTokenCount is null)
      return null;
    ModelHistory history = currentHistory ?? await session.ReadModelHistoryAsync(ExecutionId, requestId);
    EnsureStartingContext();
    return HistoryCompactor.CreatePlan(history, selection, startingContext!, reason, focus,
        preferences.CompactionTriggerTokenCount);
  }

  private async Task<GeneratedCompactionSummary> GenerateHistorySummaryAsync(HistoryCompactionPlan plan) {
    chatClient ??= createChatClient(selection.Connection);
    try {
      return await HistoryCompactor.GenerateSummaryAsync(chatClient, selection, plan, stop.Token,
          new UsageContext(session.Id, ExecutionId, "compaction"));
    } catch (OperationCanceledException) when (IsStopping) {
      throw;
    } catch (Exception failure) {
      throw new InvalidOperationException("History compaction failed; the previous context is unchanged.", failure);
    }
  }

  private void EnsureStartingContext() {
    startingContext ??= session.ReadStartingContext() ?? startingContextManager.Create(new(
        session.Kind, selection.Family, session.WorkingDirectory, null), stop.Token);
    stop.Token.ThrowIfCancellationRequested();
  }

  private async Task<LlmRequestResult> SendLlmRequestAsync(LlmRequest request, bool allowAutomaticCompaction = true) {
    using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
    var writer = new LlmResponseWriter(request.RequestId, SaveChunkAsync,
        (callId, name) => RecordToolCallAsync(request.RequestId, callId, name));
    var response = new LlmResponseAccumulator(writer);
    (Exception? failure, bool outputObserved) = await StreamLlmResponseAsync(
        request, requestStop, allowAutomaticCompaction, writer, response);
    if (failure is ProviderOperationException && !IsStopping) {
      try { await writer.FlushAsync(); }
      catch (Exception saveFailure) { failure = saveFailure; }
    }
    LlmRequestResult result = CreateRequestResult(request.RequestId, failure);
    return result with {
      ToolCalls = result.Outcome == ExecutionOutcome.Completed ? response.ToolCalls : null,
      ProviderResponse = result.Outcome == ExecutionOutcome.Completed ? response.ProviderResponse : null,
      Usage = response.Usage,
      OutputObserved = outputObserved
    };
  }

  private async Task<(Exception? Failure, bool OutputObserved)> StreamLlmResponseAsync(LlmRequest request,
      CancellationTokenSource requestStop, bool allowAutomaticCompaction, LlmResponseWriter writer, LlmResponseAccumulator response) {
    IAsyncEnumerator<ChatResponseUpdate>? updates = null;
    Task<bool>? pendingRead = null;
    Exception? failure = null;
    bool outputObserved = false;
    try {
      CancellationToken stopToken = stop.Token;
      stopToken.ThrowIfCancellationRequested();
      EnsureStartingContext();
      if (allowAutomaticCompaction)
        request = await CompactHistoryAsync(request.RequestId, CompactionReason.Threshold, currentHistory: request.Context) ?? request;
      chatClient ??= createChatClient(selection.Connection);
      var options = new ChatOptions {
        ModelId = selection.ModelId,
        Reasoning = selection.ReadReasoningEffort() is { } effort
            ? new ReasoningOptions { Effort = effort } : null,
        Tools = [.. startingContext!.Tools],
        AdditionalProperties = new() {
          [UsageContext.PropertyName] = new UsageContext(session.Id, ExecutionId, "conversation")
        }
      };
      await StartRequestAsync(request.RequestId, startingContext);
      updates = chatClient.GetStreamingResponseAsync(LlmMessageProjection.AdaptForModel(
          [.. startingContext.Instructions, .. request.Messages], selection.SupportsImages), options, requestStop.Token)
          .GetAsyncEnumerator(requestStop.Token);

      while (await WaitForUpdateAndFlushAsync(writer, pendingRead ??= updates.MoveNextAsync().AsTask())) {
        pendingRead = null;
        stopToken.ThrowIfCancellationRequested();
        outputObserved |= updates.Current.Contents.Any(content => content is not UsageContent);
        await response.AddUpdateAsync(updates.Current);
      }

      pendingRead = null;
      stopToken.ThrowIfCancellationRequested();
      await response.FinishAsync();
    } catch (Exception exception) {
      failure = exception;
    }

    await CancelAndCloseResponseAsync(requestStop, pendingRead, updates, failure);
    return (failure, outputObserved);
  }

  private async Task<bool> WaitForUpdateAndFlushAsync(LlmResponseWriter writer, Task<bool> pendingRead) {
    Task? flushDelay = writer.FlushDelay;
    if (flushDelay is not null &&
        (flushDelay.IsCompleted || await Task.WhenAny(pendingRead, flushDelay) == flushDelay)) {
      stop.Token.ThrowIfCancellationRequested();
      // Flushing clears the deadline; only another update can start a new one.
      await writer.FlushAsync();
    }

    return await pendingRead;
  }

  private LlmRequestResult CreateRequestResult(Guid requestId, Exception? failure) {
    // A failed save is a session failure, not a failed LLM request.
    if (failure is OutputSaveException or RequestStartSaveException or CompactionSaveException)
      ExceptionDispatchInfo.Throw(failure);

    bool stopped = stop.IsCancellationRequested;
    ExecutionOutcome outcome = failure switch {
      null when stopped => ExecutionOutcome.Cancelled,
      null => ExecutionOutcome.Completed,
      OutputRejectedException => ExecutionOutcome.Cancelled,
      OperationCanceledException when stopped => ExecutionOutcome.Cancelled,
      _ => ExecutionOutcome.Failed
    };
    string? error = outcome == ExecutionOutcome.Failed ? failure!.Message : null;
    return new(requestId, outcome, error, Failure: (failure as ProviderOperationException)?.Failure);
  }

  private Task StartRequestAsync(Guid requestId, StartingContext startingContext) =>
    RequireSavedAsync(recorder.StartRequestAsync(requestId, startingContext), static failure => new RequestStartSaveException(failure));

  private Task SaveChunkAsync(LlmResponseChunk chunk) =>
    RequireSavedAsync(recorder.RecordOutputAsync(chunk), static failure => new OutputSaveException(failure));

  private Task RecordToolCallAsync(Guid requestId, string callId, string? name) =>
    RequireSavedAsync(recorder.RecordToolCallAsync(requestId, callId, name), static failure => new OutputSaveException(failure));

  private static async Task RequireSavedAsync(Task<bool> save, Func<Exception, Exception> wrapFailure) {
    bool saved;
    try {
      saved = await save;
    } catch (Exception failure) {
      throw wrapFailure(failure);
    }
    if (!saved)
      throw new OutputRejectedException();
  }

  // Attempts every step even when an earlier one fails. A cleanup failure leaves the
  // execution unsettled, so it is thrown to the session instead of becoming a failed request.
  private static async Task CancelAndCloseResponseAsync(CancellationTokenSource requestStop, Task<bool>? pendingRead,
      IAsyncEnumerator<ChatResponseUpdate>? updates, Exception? requestFailure) {
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

#pragma warning disable S3871 // Private driver signals, not public exception contracts.
  // The session no longer accepts output from this execution.
  private sealed class OutputRejectedException : Exception;

  private sealed class CompactionSaveException(Exception inner) : Exception("Saving history compaction failed.", inner);

  private sealed class OutputSaveException(Exception inner) : Exception("Saving response output failed.", inner);

  private sealed class RequestStartSaveException(Exception inner) : Exception("Saving the request attempt failed.", inner);
#pragma warning restore S3871
}
