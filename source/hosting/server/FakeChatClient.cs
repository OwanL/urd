using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Urd.Server;

// Deterministic streaming client for local integration; never calls a provider or network.
internal sealed class FakeChatClient : IChatClient {
  public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
      ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    foreach (string text in new[] { "Hello ", "from ", "Urd." }) {
      cancellationToken.ThrowIfCancellationRequested();
      yield return new(ChatRole.Assistant, text);
      await Task.Yield();
    }

    cancellationToken.ThrowIfCancellationRequested();
    yield return new() { FinishReason = ChatFinishReason.Stop };
  }

  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
      CancellationToken cancellationToken = default) => throw new NotSupportedException("Use streaming responses.");

  public object? GetService(Type serviceType, object? serviceKey = null) => null;

  public void Dispose() {
    // No owned resources.
  }
}
