using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Urd.Core.Concurrency;
using Urd.Core.Storage;
using Urd.Core.Usage;

namespace Urd.Core.Providers;

// Tracks provider requests and drains cancellation before releasing borrowed transport and storage.
internal sealed class ProviderRequests(
    AsyncLock gate, ProviderStorage storage, ModelSettingsStorage modelSettings, OpenAIInferenceClient inference,
    AnthropicInferenceClient anthropic, Func<Guid, long, CancellationToken, Task<ProviderState>> getCredentials,
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
      ProviderConnection expectedBinding, LlmApi? modelApi, IEnumerable<ChatMessage> messages, ChatOptions options,
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
      bool canRetryPrepass = prepared.AdditionalProperties?.Remove(PrepassRetry.PropertyName) == true;
      string? imageModel = options.ModelId is { Length: > 0 and <= 256 } modelId && !modelId.Any(char.IsControl)
          && input.Any(message => message.Contents.Any(ContainsImage)) ? modelId : null;
      RateLimitRetry? rateLimitRetry = null;
      while (true) {
        ProviderState state = await getCredentials(expectedBinding.Id, generation, token);
        token.ThrowIfCancellationRequested();
        rateLimitRetry?.CheckDeadline();
        RequireImagesAllowed(state, imageModel);
        ProviderOperationException? rejected = null;
        bool observed = false;
        var (transport, wireOptions) = SelectInference(state, prepared, modelApi);
        await using (var stream = ObserveInference(transport, input, wireOptions, context).GetAsyncEnumerator(token)) {
          while (true) {
            var (hasUpdate, failure) = await ReadUpdateAsync(stream, state, imageModel, observed, canRetryPrepass, token);
            if (!hasUpdate) {
              rejected = failure;
              if (rejected?.IsRetryableRateLimit == true)
                rateLimitRetry ??= new(retryClock);
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
        if (rejected is null) {
          if (completion is null)
            throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
          completedState = state;
          break;
        }
        // Keep the original provider reservation through backoff; Stop and provider changes cancel it.
        if (rejected.IsRetryableRateLimit) {
          await rateLimitRetry!.WaitAsync(rejected.RetryAfter, publishRetry, token);
        } else {
          canRetryPrepass = false;
          await PrepassRetry.WaitAsync(rejected.RetryAfter, retryClock, token);
        }
      }
    } finally {
      await FinishRequestAsync(request, completedState, cancellationToken);
    }
    // No executable calls or native-context completion escape before the manager accepts the result.
    yield return completion;
  }

  internal async Task<ProviderModelTestResult> TestCopilotModelAsync(ProviderState state, string modelId, CancellationToken token) {
    const int previewLimit = 4096;
    ChatMessage[] input = [new(ChatRole.User, "Reply with one short greeting.")];
    var (transport, options) = SelectInference(state, new() { ModelId = modelId }, null);
    var preview = new StringBuilder();
    UsageDetails? counts = null;
    bool truncated = false, completed = false;
    await foreach (ChatResponseUpdate update in ObserveInference(transport, input, options, new(null, null, "provider test"))
        .WithCancellation(token).ConfigureAwait(false)) {
      if (completed)
        throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);
      if (update.FinishReason is { } finish) {
        if (finish != ChatFinishReason.Stop)
          throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
        completed = true;
      }
      foreach (string text in update.Contents.OfType<TextContent>().Select(part => part.Text)) {
        int length = Math.Min(previewLimit - preview.Length, text.Length);
        preview.Append(text.AsSpan(0, length));
        truncated |= length < text.Length;
      }
      foreach (UsageContent reported in update.Contents.OfType<UsageContent>())
        (counts ??= new()).Add(reported.Details);
    }
    if (!completed)
      throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
    return new(state.Connection.Id, modelId, preview.ToString(), truncated,
        counts is null ? null : ProviderTokenUsage.ToProviderUsage(counts));
  }

  private UsageStream ObserveInference(ProviderState state, IReadOnlyList<ChatMessage> input, ChatOptions options,
      UsageContext context) {
    PromptCacheCapture? cache = context.PromptCache is { } scope
        ? new(scope.Monitor, scope.Stamp, state, options.ModelId!) : null;
    return UsageStream.Observe(ct => (state.Connection.Api == LlmApi.AnthropicMessages
        ? anthropic.GetStreamingResponseAsync(state, input, options, ct)
        : inference.GetStreamingResponseAsync(state, input, options, ct, cache)).GetAsyncEnumerator(ct),
        usage, state.Connection, input, options, context, prices, cache);
  }

  private static (ProviderState State, ChatOptions Options) SelectInference(ProviderState state, ChatOptions options,
      LlmApi? expectedModelApi) {
    if (state.Connection.Route != ProviderRoute.GitHubCopilot)
      return (state, options);

    ProviderModel? model = state.Catalog?.Models.FirstOrDefault(candidate => candidate.Id == options.ModelId);
    if (model?.Api is not { } api || expectedModelApi is { } expected && expected != api)
      throw new ProviderOperationException(ProviderFailure.ConnectionChanged,
          "This Copilot model or its protocol changed. Refresh the catalog and reselect the model.");
    ProviderConnection connection = state.Connection with {
      Api = api, Endpoint = GitHubCopilotAuthentication.GetEndpoint(state.Credentials!)
    };
#pragma warning disable MEAI001 // Match the selected effort to the account catalog before serialization.
    if (options.Reasoning?.Effort is { } effort && !SupportsEffort(model, effort)) {
      options = options.Clone();
      options.Reasoning = null;
    }
#pragma warning restore MEAI001
    return (state with { Connection = connection }, options);
  }

  private static bool SupportsEffort(ProviderModel model, ReasoningEffort effort) =>
      model.ReasoningEfforts?.Contains(effort == ReasoningEffort.ExtraHigh ? "xhigh" : effort.ToString().ToLowerInvariant(),
          StringComparer.Ordinal) == true;

  private async ValueTask<(bool HasUpdate, ProviderOperationException? Rejected)> ReadUpdateAsync(
      IAsyncEnumerator<ChatResponseUpdate> stream, ProviderState state, string? imageModel, bool observed,
      bool canRetryPrepass, CancellationToken cancellationToken) {
    try {
      return (await stream.MoveNextAsync(), null);
    } catch (ProviderOperationException failure) when (!observed &&
        (failure.IsRetryableRateLimit || canRetryPrepass && PrepassRetry.CanRetry(failure))) {
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
