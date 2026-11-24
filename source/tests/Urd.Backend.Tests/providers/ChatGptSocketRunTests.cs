using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Urd.Core.Providers;
using Xunit;

namespace Urd.Backend.Tests.Providers;

public sealed class ChatGptSocketRunTests {
  private const string Request = "{\"model\":\"model\",\"input\":[{\"role\":\"user\",\"content\":\"hello\"}]}";
  private const string Created = "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\"}}";
  private const string Completion = "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"output\":[{\"type\":\"message\"}]}}";
  private const string Followup = "{\"model\":\"model\",\"input\":[{\"role\":\"user\",\"content\":\"hello\"},{\"type\":\"message\"},{\"role\":\"user\",\"content\":\"next\"}]}";

  [Fact]
  public async Task HandshakeTimeoutLatchesHttpAndDisposesLateSocket() {
    using var run = new ChatGptSocketRun();
    var pending = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
    int attempts = 0;
    Task<WebSocket> Connect(string token, Guid id, CancellationToken cancellationToken) { attempts++; return pending.Task; }
    ProviderState state = CreateState();
    Guid session = Guid.NewGuid();

    Assert.Null(await run.OpenTurnAsync(state, session, Connect, default).WaitAsync(TimeSpan.FromSeconds(15)));
    Assert.Null(await run.OpenTurnAsync(state, session, Connect, default));
    Assert.Equal(1, attempts);
    var late = new FakeSocket();
    pending.SetResult(late);
    await late.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
  }

  [Fact]
  public async Task CallerCancellationDuringHandshakePropagatesWithoutLatchingHttp() {
    using var run = new ChatGptSocketRun();
    using var cancellation = new CancellationTokenSource();
    var pending = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<WebSocket> Connect(string token, Guid id, CancellationToken cancellationToken) { started.SetResult(); return pending.Task; }
    ProviderState state = CreateState();
    Guid session = Guid.NewGuid();
    Task<HttpClient?> opening = run.OpenTurnAsync(state, session, Connect, cancellation.Token);
    await started.Task;
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
    var late = new FakeSocket();
    pending.SetResult(late);
    await late.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var fresh = new FakeSocket();
    using HttpClient? turn = await run.OpenTurnAsync(state, session, (_, _, _) => Task.FromResult<WebSocket>(fresh), default);
    Assert.NotNull(turn);
    run.EndTurn(false);
  }

  [Theory]
  [InlineData("session")]
  [InlineData("connection")]
  [InlineData("credential")]
  [InlineData("generation")]
  [InlineData("closed")]
  public async Task StaleScopeOrSocketDisposesAndResetsContinuation(string change) {
    using var run = new ChatGptSocketRun();
    ProviderState state = CreateState();
    Guid session = Guid.NewGuid();
    var first = new FakeSocket(Created, Completion);
    var second = new FakeSocket(Created, Completion);
    int attempts = 0;
    Task<WebSocket> Connect(string token, Guid id, CancellationToken cancellationToken) => Task.FromResult<WebSocket>(++attempts == 1 ? first : second);
    using (HttpClient turn = (await run.OpenTurnAsync(state, session, Connect, default))!) {
      using var response = await turn.PostAsync("http://local/", new StringContent(Request));
      await response.Content.ReadAsStringAsync();
    }
    run.EndTurn(true);
    if (change == "session") session = Guid.NewGuid();
    if (change == "connection") state = state with { Connection = state.Connection with { Id = Guid.NewGuid() } };
    if (change == "credential") state = state with { CredentialVersion = state.CredentialVersion + 1 };
    if (change == "generation") state = state with { Generation = state.Generation + 1 };
    if (change == "closed") first.Close();

    using HttpClient next = (await run.OpenTurnAsync(state, session, Connect, default))!;
    Assert.True(first.Disposed.Task.IsCompleted);
    using var nextResponse = await next.PostAsync("http://local/", new StringContent(Followup));
    using var frame = JsonDocument.Parse(Assert.Single(second.Frames));
    Assert.False(frame.RootElement.TryGetProperty("previous_response_id", out _));
    Assert.Equal(3, frame.RootElement.GetProperty("input").GetArrayLength());
    Assert.Equal(2, attempts);
    run.EndTurn(false);
    Assert.True(second.Disposed.Task.IsCompleted);
  }

  [Theory]
  [InlineData(119, false)]
  [InlineData(120, true)]
  [InlineData(121, true)]
  public async Task IdleSocketExpiresBeforeNextTurn(int seconds, bool expired) {
    var clock = new FakeClock();
    using var run = new ChatGptSocketRun(clock);
    ProviderState state = CreateState();
    Guid session = Guid.NewGuid();
    var first = new FakeSocket(Created, Completion);
    var second = new FakeSocket();
    int attempts = 0;
    Task<WebSocket> Connect(string token, Guid id, CancellationToken cancellationToken) => Task.FromResult<WebSocket>(++attempts == 1 ? first : second);
    using (HttpClient turn = (await run.OpenTurnAsync(state, session, Connect, default))!) {
      using var response = await turn.PostAsync("http://local/", new StringContent(Request));
      await response.Content.ReadAsStringAsync();
    }
    clock.Advance(TimeSpan.FromSeconds(seconds));
    run.EndTurn(true);

    using HttpClient next = (await run.OpenTurnAsync(state, session, Connect, default))!;
    Assert.Equal(expired, first.Disposed.Task.IsCompleted);
    Assert.Equal(expired ? 2 : 1, attempts);
    using var nextRequest = new HttpRequestMessage(HttpMethod.Post, "http://local/") { Content = new StringContent(Followup) };
    using var nextResponse = await next.SendAsync(nextRequest, HttpCompletionOption.ResponseHeadersRead);
    using var frame = JsonDocument.Parse(expired ? Assert.Single(second.Frames) : first.Frames[1]);
    Assert.Equal(!expired, frame.RootElement.TryGetProperty("previous_response_id", out _));
    Assert.Equal(expired ? 3 : 1, frame.RootElement.GetProperty("input").GetArrayLength());
    run.EndTurn(false);
  }

  [Fact]
  public async Task ExpiredSocketHandshakeFailureLatchesHttpFallback() {
    var clock = new FakeClock();
    using var run = new ChatGptSocketRun(clock);
    ProviderState state = CreateState();
    Guid session = Guid.NewGuid();
    var socket = new FakeSocket(Created, Completion);
    int attempts = 0;
    Task<WebSocket> Connect(string token, Guid id, CancellationToken ct) => ++attempts == 1 ?
        Task.FromResult<WebSocket>(socket) : Task.FromException<WebSocket>(new WebSocketException());
    using (HttpClient turn = (await run.OpenTurnAsync(state, session, Connect, default))!) {
      using var response = await turn.PostAsync("http://local/", new StringContent(Request));
      await response.Content.ReadAsStringAsync();
    }
    run.EndTurn(true);
    clock.Advance(TimeSpan.FromMinutes(2));
    Assert.Null(await run.OpenTurnAsync(state, session, Connect, default));
    Assert.True(socket.Disposed.Task.IsCompleted);
    Assert.Null(await run.OpenTurnAsync(state, session, Connect, default));
    Assert.Equal(2, attempts);
    Assert.Single(socket.Frames);
  }

  [Theory]
  [InlineData("send")]
  [InlineData("receive")]
  [InlineData("interrupted")]
  public async Task UncertainTurnFailsWithoutHttpRetryAndResets(string failure) {
    using var run = new ChatGptSocketRun();
    var first = new FakeSocket { FailSend = failure == "send", FailReceive = failure == "receive" };
    var second = new FakeSocket();
    int attempts = 0;
    Task<WebSocket> Connect(string token, Guid id, CancellationToken cancellationToken) => Task.FromResult<WebSocket>(++attempts == 1 ? first : second);
    ProviderState state = CreateState();
    Guid session = Guid.NewGuid();
    using HttpClient turn = (await run.OpenTurnAsync(state, session, Connect, default))!;
    if (failure == "send") {
      await Assert.ThrowsAsync<HttpRequestException>(() => turn.PostAsync("http://local/", new StringContent(Request)));
    } else {
      using var request = new HttpRequestMessage(HttpMethod.Post, "http://local/") { Content = new StringContent(Request) };
      using var response = await turn.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
      if (failure == "receive") {
        using Stream stream = await response.Content.ReadAsStreamAsync();
        await Assert.ThrowsAnyAsync<IOException>(async () => await stream.ReadExactlyAsync(new byte[128]));
      }
    }
    run.EndTurn(false);
    Assert.True(first.Disposed.Task.IsCompleted);
    await Assert.ThrowsAsync<HttpRequestException>(() => turn.PostAsync("http://local/", new StringContent(Request)));
    using HttpClient? next = await run.OpenTurnAsync(state, session, Connect, default);
    Assert.NotNull(next);
    Assert.Equal(2, attempts);
    run.EndTurn(false);
  }

  [Theory]
  [InlineData("completion")]
  [InlineData("created")]
  [InlineData("completion-after-created")]
  public async Task ReusedSocketRejectsDelayedCompletionOrRepeatedCreated(string late) {
    using var run = new ChatGptSocketRun();
    string oldCompletion = "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"output\":[{\"type\":\"function_call\",\"name\":\"stale\",\"call_id\":\"call_old\",\"arguments\":\"{}\"}]}}";
    string lateMessage = late == "created" ? Created : oldCompletion;
    string[] messages = late == "completion-after-created" ?
        [Created, Completion, "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_2\"}}", oldCompletion] :
        [Created, Completion, lateMessage];
    var socket = new FakeSocket(messages);
    ProviderState state = CreateState();
    Guid session = Guid.NewGuid();
    Task<WebSocket> Connect(string token, Guid id, CancellationToken ct) => Task.FromResult<WebSocket>(socket);
    using (HttpClient turn = (await run.OpenTurnAsync(state, session, Connect, default))!) {
      using var response = await turn.PostAsync("http://local/", new StringContent(Request));
      await response.Content.ReadAsStringAsync();
    }
    run.EndTurn(true);
    using (HttpClient turn = (await run.OpenTurnAsync(state, session, Connect, default))!) {
      using var request = new HttpRequestMessage(HttpMethod.Post, "http://local/") { Content = new StringContent(Request) };
      using var response = await turn.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
      using var frame = JsonDocument.Parse(socket.Frames[1]);
      Assert.False(frame.RootElement.TryGetProperty("previous_response_id", out _));
      using Stream stream = await response.Content.ReadAsStreamAsync();
      await Assert.ThrowsAnyAsync<IOException>(() => stream.CopyToAsync(Stream.Null));
    }
    run.EndTurn(false);
    Assert.True(socket.Disposed.Task.IsCompleted);
  }

  private static ProviderState CreateState() => new(
      new(Guid.NewGuid(), "ChatGPT", ProviderRoute.ChatGpt, LlmApi.OpenAIResponses,
          new Uri("https://api.openai.com/v1/"), AuthenticationMethod.OAuth),
      1, 1, null, new(null, "token", null, null, null, []), false, null);

  private sealed class FakeClock : TimeProvider {
    private long timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
  }

  private sealed class FakeSocket(params string[] messages) : WebSocket {
    private WebSocketState state = WebSocketState.Open;
    private int messageIndex;
    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Frames { get; } = [];
    public bool FailSend { get; init; }
    public bool FailReceive { get; init; }
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;
    public void Close() => state = WebSocketState.Closed;
    public override void Abort() => Close();
    public override void Dispose() { Close(); Disposed.TrySetResult(); }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
      if (FailSend) throw new WebSocketException();
      Frames.Add(Encoding.UTF8.GetString(buffer.AsSpan()));
      return Task.CompletedTask;
    }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
      if (FailReceive || messageIndex >= messages.Length) throw new WebSocketException();
      byte[] bytes = Encoding.UTF8.GetBytes(messages[messageIndex++]);
      bytes.CopyTo(buffer.AsSpan());
      return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
    }
  }
}
