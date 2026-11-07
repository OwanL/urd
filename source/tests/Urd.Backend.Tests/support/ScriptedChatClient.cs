using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace Urd.Backend.Tests.Support;

// Streaming-only fake model client that replays a scripted response and counts calls.
internal sealed class ScriptedChatClient(Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> script)
    : IChatClient {
  private readonly ConcurrentQueue<IReadOnlyList<ChatMessage>> requests = new();
  private readonly ConcurrentQueue<ChatOptions?> requestOptions = new();
  private int calls, disposeCalls;
  internal int Calls => Volatile.Read(ref calls);
  internal int DisposeCalls => Volatile.Read(ref disposeCalls);
  internal IReadOnlyList<ChatMessage> LastRequest { get; private set; } = [];
  internal IEnumerable<ChatMessage> LastRequestSource { get; private set; } = [];
  internal IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => requests.ToArray();
  internal IReadOnlyList<ChatOptions?> Options => requestOptions.ToArray();

  public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
      ChatOptions? options = null, CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
    LastRequestSource = messages;
    ChatMessage[] captured = messages.ToArray();
    LastRequest = captured;
    requests.Enqueue(captured);
    requestOptions.Enqueue(options?.Clone());
    Interlocked.Increment(ref calls);
    return script(cancellationToken);
  }

  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
      CancellationToken cancellationToken = default) => throw new NotSupportedException("Streaming fake only.");

  public object? GetService(Type serviceType, object? serviceKey = null) => null;
  public void Dispose() => Interlocked.Increment(ref disposeCalls);
}
