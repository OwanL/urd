using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Core.Usage;

namespace Urd.Backend.Tests.Support;

// Adds real usage capture to scripted clients without owning them.
internal sealed class UsageChatClient(IChatClient inner, UsageStorage storage, ProviderConnection connection) : IChatClient {
  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
      CancellationToken cancellationToken = default) =>
      GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

  public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
      ChatOptions? options = null, CancellationToken cancellationToken = default) {
    IReadOnlyList<ChatMessage> input = messages.ToArray();
    ChatOptions prepared = UsageStream.PrepareOptions(options, out UsageContext context);
    return UsageStream.Observe(token => inner.GetStreamingResponseAsync(input, prepared, cancellationToken)
        .GetAsyncEnumerator(token), storage, connection, input, prepared, context);
  }

  public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

  public void Dispose() { /* The caller owns the wrapped client. */ }
}
