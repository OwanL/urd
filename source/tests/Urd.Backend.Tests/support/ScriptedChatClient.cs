using Microsoft.Extensions.AI;

namespace Urd.Backend.Tests.Support;

// Streaming-only fake model client that replays a scripted response and counts calls.
internal sealed class ScriptedChatClient(Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> script)
    : IChatClient {
  private int calls, disposeCalls;
  internal int Calls => Volatile.Read(ref calls);
  internal int DisposeCalls => Volatile.Read(ref disposeCalls);
  internal IReadOnlyList<ChatMessage> LastRequest { get; private set; } = [];

  public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
      ChatOptions? options = null, CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
    LastRequest = messages.ToArray();
    Interlocked.Increment(ref calls);
    return script(cancellationToken);
  }

  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
      CancellationToken cancellationToken = default) => throw new NotSupportedException("Streaming fake only.");

  public object? GetService(Type serviceType, object? serviceKey = null) => null;
  public void Dispose() => Interlocked.Increment(ref disposeCalls);
}
