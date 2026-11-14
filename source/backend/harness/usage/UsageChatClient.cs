using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Storage;

namespace Urd.Core.Usage;

// A borrowed view: usage belongs to each invocation, not to this client or its disposal.
internal sealed class UsageChatClient(IChatClient inner, UsageStorage storage, ProviderConnection connection,
    bool disposeInner = false) : IChatClient {
  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
      CancellationToken cancellationToken = default) =>
      GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

  public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
      ChatOptions? options = null, CancellationToken cancellationToken = default) => new UsageStream(token => {
    cancellationToken.ThrowIfCancellationRequested();
    token.ThrowIfCancellationRequested();
    ChatMessage[] history = messages.ToArray();
    ChatOptions requestOptions = options?.Clone() ?? new();
    if (options?.AdditionalProperties is { } additionalProperties) {
      requestOptions.AdditionalProperties = [];
      foreach ((string key, object? value) in additionalProperties)
        requestOptions.AdditionalProperties[key] = value;
    }
    UsageContext context = ReadContext(requestOptions);
    string model = requestOptions.ModelId ?? throw new ArgumentException("A model ID is required.");
    var observation = new UsageObservation(storage, connection, model, context, history, requestOptions);
    try {
      return (inner.GetStreamingResponseAsync(history, requestOptions, cancellationToken).GetAsyncEnumerator(token), observation);
    } catch {
      observation.Finish();
      throw;
    }
  });

  public object? GetService(Type serviceType, object? serviceKey = null) {
    ArgumentNullException.ThrowIfNull(serviceType);
    return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);
  }

  public void Dispose() {
    if (disposeInner)
      inner.Dispose();
  }

  private static UsageContext ReadContext(ChatOptions options) {
    if (options.AdditionalProperties is { } properties && properties.TryGetValue(UsageContext.PropertyName, out object? value)) {
      properties.Remove(UsageContext.PropertyName);
      return value as UsageContext
          ?? throw new ArgumentException($"'{UsageContext.PropertyName}' must contain a UsageContext.", nameof(options));
    }
    return new(null, null, UsageContext.DefaultPurpose);
  }
}
