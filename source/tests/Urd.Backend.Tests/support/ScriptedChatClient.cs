using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Urd.Core.Usage;

namespace Urd.Backend.Tests.Support;

// One captured request with its options; routing scripts key on the recorded usage purpose.
internal sealed record ScriptedRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) {
  internal string? Purpose => Options?.AdditionalProperties?
      .TryGetValue(UsageContext.PropertyName, out object? value) == true && value is UsageContext usage
      ? usage.Purpose : null;

  internal string LastUserText => Messages[^1].Text;
}

// Fake LLM client that replays scripted responses and counts both request forms. The routing
// constructor hands each request to its script capture-sequentially, so concurrent
// conversations can never observe a foreign request.
internal sealed class ScriptedChatClient : IChatClient {
  private readonly ConcurrentQueue<IReadOnlyList<ChatMessage>> requests = new();
  private readonly ConcurrentQueue<ChatOptions?> requestOptions = new();
  private readonly Func<ScriptedRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> routeStream;
  private readonly Func<ScriptedRequest, CancellationToken, Task<ChatResponse>>? routeResponse;
  private int calls, disposeCalls;

  internal ScriptedChatClient(Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> script,
      Func<CancellationToken, Task<ChatResponse>>? response = null)
      : this((_, token) => script(token), response is null ? null : (_, token) => response(token), purposeAware: false) { }

  internal ScriptedChatClient(Func<ScriptedRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> stream,
      Func<ScriptedRequest, CancellationToken, Task<ChatResponse>>? response = null)
      : this(stream, response, purposeAware: true) { }

  private ScriptedChatClient(Func<ScriptedRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> stream,
      Func<ScriptedRequest, CancellationToken, Task<ChatResponse>>? response, bool purposeAware) {
    routeStream = stream;
    routeResponse = response;
    IsPurposeAware = purposeAware;
  }

  internal bool IsPurposeAware { get; }
  internal int Calls => Volatile.Read(ref calls);
  internal int DisposeCalls => Volatile.Read(ref disposeCalls);
  internal IReadOnlyList<ChatMessage> LastRequest { get; private set; } = [];
  internal IEnumerable<ChatMessage> LastRequestSource { get; private set; } = [];
  internal IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => requests.ToArray();
  internal IReadOnlyList<ChatOptions?> Options => requestOptions.ToArray();

  public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
      ChatOptions? options = null, CancellationToken cancellationToken = default) =>
      routeStream(Capture(messages, options), cancellationToken);

  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
      CancellationToken cancellationToken = default) {
    if (routeResponse is null)
      return GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);
    return routeResponse(Capture(messages, options), cancellationToken);
  }

  private ScriptedRequest Capture(IEnumerable<ChatMessage> messages, ChatOptions? options) {
    ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
    ChatMessage[] captured = messages.ToArray();
    ChatOptions? capturedOptions = options?.Clone();
    LastRequestSource = messages;
    LastRequest = captured;
    requests.Enqueue(captured);
    requestOptions.Enqueue(capturedOptions);
    Interlocked.Increment(ref calls);
    return new(captured, capturedOptions);
  }

  public object? GetService(Type serviceType, object? serviceKey = null) => null;
  public void Dispose() => Interlocked.Increment(ref disposeCalls);
}
