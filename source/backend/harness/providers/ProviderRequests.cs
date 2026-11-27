using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Core.Concurrency;
using Urd.Core.Storage;
using Urd.Core.Usage;

namespace Urd.Core.Providers;

// Tracks provider requests and drains cancellation before releasing borrowed transport and storage.
internal sealed class ProviderRequests(
    AsyncLock gate, ProviderStorage storage, ModelSettingsStorage modelSettings, OpenAIInferenceClient inference,
    Func<Guid, long, CancellationToken, Task<ProviderState>> getCredentials,
    Action throwIfStopping, Action<ProviderState> ensureAvailable, Action<ProviderState> requireCurrentGrant,
    UsageStorage usage, ModelPriceCatalog? prices, TimeProvider retryClock) {
  private readonly List<ProviderRequest> requests = [];

  internal async Task<T> RunRequestAsync<T>(Guid connectionId,
      Func<ProviderState, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) {
    var (request, generation) = await BeginRequestAsync(connectionId, cancellationToken);
    try {
      ProviderState state = await getCredentials(connectionId, generation, request.Cancellation.Token);
      request.Cancellation.Token.ThrowIfCancellationRequested();
      T result = await operation(state, request.Cancellation.Token);
      using var held = await gate.AcquireAsync(request.Cancellation.Token);
      request.Cancellation.Token.ThrowIfCancellationRequested();
      requireCurrentGrant(state);
      return result;
    } finally {
      await FinishRequestAsync(request, cancellationToken: CancellationToken.None);
    }
  }

  internal async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
      ProviderConnection expectedBinding, IEnumerable<ChatMessage> messages, ChatOptions options,
      [EnumeratorCancellation] CancellationToken cancellationToken) {
    var (request, generation) = await BeginRequestAsync(expectedBinding.Id, cancellationToken,
        Timeout.InfiniteTimeSpan, expectedBinding);
    ChatResponseUpdate? completion = null;
    ProviderState? completedState = null;
    try {
      CancellationToken token = request.Cancellation.Token;
      IReadOnlyList<ChatMessage> input = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
      ChatOptions prepared = UsageStream.PrepareOptions(options, out UsageContext context);
      Func<DateTimeOffset?, Task>? publishRetry = TakeRetryStatus(prepared);
      string? imageModel = options.ModelId is { Length: > 0 and <= 256 } modelId && !modelId.Any(char.IsControl)
          && input.Any(message => message.Contents.Any(ContainsImage)) ? modelId : null;
      RateLimitRetry? retry = null;
      while (true) {
        ProviderState state = await getCredentials(expectedBinding.Id, generation, token);
        token.ThrowIfCancellationRequested();
        retry?.CheckDeadline();
        RequireImagesAllowed(state, imageModel);
        ProviderOperationException? throttled = null;
        bool observed = false;
        await using (var stream = UsageStream.Observe(ct => inference.GetStreamingResponseAsync(state, input, prepared, ct)
            .GetAsyncEnumerator(ct), usage, expectedBinding, input, prepared, context, prices).GetAsyncEnumerator(token)) {
          while (true) {
            var (hasUpdate, failure) = await ReadUpdateAsync(stream, state, imageModel, observed, token);
            if (!hasUpdate) {
              throttled = failure;
              if (throttled is not null)
                retry ??= new(retryClock);
              break;
            }
            ChatResponseUpdate update = stream.Current;
            observed |= HasOutput(update);
            if (completion is not null)
              throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);
            if (update.FinishReason is not null)
              completion = update;
            else
              yield return update;
          }
        }
        token.ThrowIfCancellationRequested();
        if (throttled is null) {
          if (completion is null)
            throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
          completedState = state;
          break;
        }
        // Keep the original provider reservation through backoff; Stop and provider changes cancel it.
        await retry!.WaitAsync(throttled.RetryAfter, publishRetry, token);
      }
    } finally {
      await FinishRequestAsync(request, completedState, cancellationToken);
    }
    // No executable calls or native-context completion escape before the manager accepts the result.
    yield return completion;
  }

  private async ValueTask<(bool HasUpdate, ProviderOperationException? Throttled)> ReadUpdateAsync(
      IAsyncEnumerator<ChatResponseUpdate> stream, ProviderState state, string? imageModel, bool observed,
      CancellationToken cancellationToken) {
    try {
      return (await stream.MoveNextAsync(), null);
    } catch (ProviderOperationException failure) when (failure.IsRetryableRateLimit && !observed) {
      return (false, failure);
    } catch (ProviderOperationException failure) when (imageModel is not null && failure.Failure == ProviderFailure.ImageInputUnsupported) {
      using var held = await gate.AcquireAsync(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      throwIfStopping();
      requireCurrentGrant(state);
      modelSettings.RecordImageRejection(state.Connection.Id, imageModel);
      throw;
    }
  }

  private void RequireImagesAllowed(ProviderState state, string? imageModel) {
    if (imageModel is not null && modelSettings.ImagesDisabled(state.Connection.Id, imageModel))
      throw new ProviderOperationException(ProviderFailure.ImageInputUnsupported,
          "Image input is disabled for this model. Reload Urd and reselect the model to update this chat, or enable Images in model settings.");
  }

  private static Func<DateTimeOffset?, Task>? TakeRetryStatus(ChatOptions options) {
    if (options.AdditionalProperties is not { } properties || !properties.TryGetValue(RateLimitRetry.StatusProperty, out object? value))
      return null;
    properties.Remove(RateLimitRetry.StatusProperty);
    return (Func<DateTimeOffset?, Task>)value!;
  }

  // Some tool argument progress arrives without AIContent. Only dedicated usage updates are safe to replay past.
  private static bool HasOutput(ChatResponseUpdate update) => update.FinishReason is not null
      || update.Contents.Count == 0 || update.Contents.Any(content => content is not UsageContent);

  private static bool ContainsImage(AIContent content) => content switch {
    DataContent data => data.HasTopLevelMediaType("image"),
    FunctionResultContent { Result: IReadOnlyList<AIContent> parts } => parts.OfType<DataContent>()
        .Any(part => part.HasTopLevelMediaType("image")),
    _ => false
  };

  private async Task<(ProviderRequest Request, long Generation)> BeginRequestAsync(Guid connectionId,
      CancellationToken cancellationToken, TimeSpan? timeout = null, ProviderConnection? expectedBinding = null) {
    using var held = await gate.AcquireAsync(cancellationToken);
    throwIfStopping();
    ProviderState state = storage.Get(connectionId);
    ensureAvailable(state);
    if (expectedBinding is not null && !ProviderManager.SameBinding(state.Connection, expectedBinding))
      throw new ProviderOperationException(ProviderFailure.ConnectionChanged);
    var request = new ProviderRequest(connectionId, cancellationToken, timeout);
    requests.Add(request);
    return (request, state.Generation);
  }

  private async Task FinishRequestAsync(ProviderRequest request, ProviderState? completedState = null,
      CancellationToken cancellationToken = default) {
    bool drained = false;
    try {
      await request.Cancellation.CancelAsync();
      drained = true;
    } finally {
      using var held = await gate.AcquireAsync(CancellationToken.None);
      try {
        if (drained && completedState is not null) {
          cancellationToken.ThrowIfCancellationRequested();
          throwIfStopping();
          requireCurrentGrant(completedState);
        }
      } finally {
        requests.Remove(request);
        request.Cancellation.Dispose();
        request.Finished.TrySetResult();
      }
    }
  }

  // Caller holds the shared guard; callbacks and joins do not execute synchronously under it.
  internal Task Cancel(Guid? connectionId = null) => Task.WhenAll(requests
      .Where(request => connectionId is null || request.ConnectionId == connectionId)
      .Select(request => Task.WhenAll(request.Cancellation.CancelAsync(), request.Finished.Task)));
}
