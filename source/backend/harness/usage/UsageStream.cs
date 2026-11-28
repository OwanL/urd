using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Storage;

namespace Urd.Core.Usage;

// Preserve EOF separately from disposal: callers may flush output before joining provider cleanup.
internal sealed class UsageStream(
    Func<CancellationToken, (IAsyncEnumerator<ChatResponseUpdate> Updates, UsageObservation Observation)> start)
    : IAsyncEnumerable<ChatResponseUpdate> {
  internal static UsageStream Observe(Func<CancellationToken, IAsyncEnumerator<ChatResponseUpdate>> stream,
      UsageStorage storage, ProviderConnection connection, IReadOnlyList<ChatMessage> input, ChatOptions options,
      UsageContext context, ModelPriceCatalog? prices = null, PromptCacheCapture? promptCache = null) {
    string model = options.ModelId ?? throw new ArgumentException("A model ID is required.", nameof(options));
    return new(token => {
      token.ThrowIfCancellationRequested();
      var observation = new UsageObservation(storage, connection, model, context, input, options, prices, promptCache);
      try {
        return (stream(token), observation);
      } catch {
        observation.Finish();
        throw;
      }
    });
  }

  // Keep internal accounting metadata out of model options without mutating the caller's settings.
  internal static ChatOptions PrepareOptions(ChatOptions? options, out UsageContext context) {
    ChatOptions prepared = options?.Clone() ?? new();
    if (options?.AdditionalProperties is { } properties)
      prepared.AdditionalProperties = new(properties);
    if (prepared.AdditionalProperties is { } copied && copied.TryGetValue(UsageContext.PropertyName, out object? value)) {
      copied.Remove(UsageContext.PropertyName);
      context = value as UsageContext ?? throw new ArgumentException("A usage context is required.", nameof(options));
    } else {
      context = new(null, null, UsageContext.DefaultPurpose);
    }
    return prepared;
  }

  public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
    var (updates, observation) = start(cancellationToken);
    return new Enumerator(updates, observation);
  }

  private sealed class Enumerator(IAsyncEnumerator<ChatResponseUpdate> updates, UsageObservation observation)
      : IAsyncEnumerator<ChatResponseUpdate> {
    public ChatResponseUpdate Current => updates.Current;

    public async ValueTask<bool> MoveNextAsync() {
      bool next = await updates.MoveNextAsync();
      if (next)
        observation.Append(updates.Current);
      else
        observation.Finish(streamEnded: true);
      return next;
    }

    public async ValueTask DisposeAsync() {
      try {
        await updates.DisposeAsync();
      } finally {
        // Caller cancellation cannot discard consumption already observed before failure or Stop.
        observation.Finish();
      }
    }
  }
}
