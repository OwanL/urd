using Microsoft.Extensions.AI;

namespace Urd.Core.Providers;

// A borrowed connection binding, not an agent/session or a credential-bearing SDK client.
internal sealed class ProviderChatClient(ProviderManager manager, ProviderConnection connection) : IChatClient {
  private bool disposed;

  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
      CancellationToken cancellationToken = default) =>
      GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

  // Nonstreaming callers still need observations before a terminal failure rejects the response.
  internal Task<ChatResponse> GetObservedResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options,
      Action<ChatResponseUpdate> observe, CancellationToken cancellationToken) {
    async IAsyncEnumerable<ChatResponseUpdate> Observed() {
      await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken)) {
        observe(update);
        yield return update;
      }
    }
    return Observed().ToChatResponseAsync(cancellationToken);
  }

  public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
      ChatOptions? options = null, CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(disposed, this);
    ArgumentNullException.ThrowIfNull(messages);
    ArgumentNullException.ThrowIfNull(options);
    return manager.GetStreamingResponseAsync(connection, messages, options, cancellationToken);
  }

  public object? GetService(Type serviceType, object? serviceKey = null) {
    ArgumentNullException.ThrowIfNull(serviceType);
    ObjectDisposedException.ThrowIf(disposed, this);
    if (serviceKey is not null) return null;
    if (serviceType == typeof(ChatClientMetadata))
      return new ChatClientMetadata(connection.Route.ToString(), connection.Endpoint);
    return serviceType.IsInstanceOfType(this) ? this : null;
  }

  // The manager owns requests and transport; disposing this view does not cancel another consumer.
  public void Dispose() => disposed = true;
}
