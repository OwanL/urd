using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Urd.Core.Providers;
using Xunit;

namespace Urd.Backend.Tests.Providers;

public sealed class ResponsesSocketStreamTests {
  [Fact]
  public async Task FragmentsBecomeCompactEventsWithoutReadingAhead() {
    byte[] first = Encoding.UTF8.GetBytes("{\n\"type\":\"response.delta\",\"text\":\"é\\nnext\"}");
    int split = Array.IndexOf(first, (byte)0xc3) + 1;
    var socket = new ScriptedSocket(
        Frame.Text("{\"type\":\"codex.response.metadata\"}"),
        Frame.Text("{\"type\":\"response.created\",\"response\":{\"id\":\"ok\"}}"),
        new(first[..split], false), new(first[split..], true),
        Frame.Text("{\"type\":\"response.completed\",\"response\":{\"id\":\"ok\"}}"),
        Frame.Text("{\"type\":\"next.turn\"}"));
    JsonElement response = default;
    using var stream = new ResponsesSocketStream(socket, value => response = value);
    byte[] buffer = new byte[3];
    using var output = new MemoryStream();
    int count = await stream.ReadAsync(buffer);
    Assert.Equal(2, socket.Receives);
    do {
      await output.WriteAsync(buffer.AsMemory(0, count));
      count = await stream.ReadAsync(buffer);
    } while (count != 0);
    Assert.Equal(5, socket.Receives);
    Assert.Equal("ok", response.GetProperty("id").GetString());
    string text = Encoding.UTF8.GetString(output.ToArray());
    string[] events = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    Assert.Equal(3, events.Length);
    Assert.StartsWith("event: response.created\ndata: ", events[0]);
    Assert.StartsWith("event: response.delta\ndata: ", events[1]);
    using var json = JsonDocument.Parse(events[1][(events[1].IndexOf("data: ", StringComparison.Ordinal) + 6)..]);
    Assert.Equal("é\nnext", json.RootElement.GetProperty("text").GetString());
    Assert.StartsWith("event: response.completed\ndata: ", events[2]);
    Assert.Equal(WebSocketState.Open, socket.State);
  }

  [Theory]
  [InlineData("response.incomplete")]
  [InlineData("response.failed")]
  [InlineData("error")]
  public async Task OtherTerminalsEndWithoutCallback(string type) {
    Frame terminal = Frame.Text(type == "error" ? "{\"type\":\"error\"}" : $"{{\"type\":\"{type}\",\"response\":{{\"id\":\"ok\"}}}}");
    var socket = type == "error" ? new ScriptedSocket(terminal, Frame.Text("{}")) :
        new ScriptedSocket(Frame.Text("{\"type\":\"response.created\",\"response\":{\"id\":\"ok\"}}"), terminal, Frame.Text("{}"));
    bool called = false;
    using var stream = new ResponsesSocketStream(socket, _ => called = true);
    using var output = new MemoryStream();
    await stream.CopyToAsync(output);
    Assert.Equal(type == "error" ? 1 : 2, socket.Receives);
    Assert.False(called);
    Assert.Contains($"event: {type}\n", Encoding.UTF8.GetString(output.ToArray()));
  }

  [Theory]
  [InlineData("not json secret")]
  [InlineData("{}")]
  [InlineData("{\"type\":\"bad\\nsecret\"}")]
  [InlineData("{\"type\":\"response.completed\"}")]
  [InlineData("{\"type\":\"response.completed\",\"response\":null}")]
  public async Task InvalidMessagesAreSanitized(string message) {
    using var stream = new ResponsesSocketStream(new ScriptedSocket(Frame.Text(message)), _ => { });
    IOException error = await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(new byte[10]));
    Assert.DoesNotContain("secret", error.ToString());
    Assert.Null(error.InnerException);
  }

  [Theory]
  [InlineData(true, "{\"type\":\"response.completed\",\"response\":{\"id\":\"old\"}}")]
  [InlineData(false, "{\"type\":\"response.created\",\"response\":{\"id\":\"ok\"}}")]
  [InlineData(false, "{\"type\":\"response.completed\",\"response\":{\"id\":\"old\"}}")]
  [InlineData(false, "{\"type\":\"response.output_text.delta\",\"response_id\":\"old\"}")]
  public async Task RejectsUnexpectedOrConflictingEvents(bool first, string message) {
    var socket = first ? new ScriptedSocket(Frame.Text(message)) :
        new ScriptedSocket(Frame.Text("{\"type\":\"response.created\",\"response\":{\"id\":\"ok\"}}"), Frame.Text(message));
    bool completed = false;
    using var stream = new ResponsesSocketStream(socket, _ => completed = true);
    await Assert.ThrowsAsync<IOException>(() => stream.CopyToAsync(Stream.Null));
    Assert.False(completed);
  }

  [Fact]
  public async Task CloseInFragmentIsFailure() {
    var socket = new ScriptedSocket(new(Encoding.UTF8.GetBytes("{\"type\":"), false),
        new([], true, WebSocketMessageType.Close));
    using var stream = new ResponsesSocketStream(socket, _ => { });
    await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(new byte[10]));
  }

  [Fact]
  public async Task OversizedMessageIsRejected() {
    var socket = new ScriptedSocket(new Frame(new byte[ResponsesSocketStream.MaximumMessageBytes + 1], true));
    using var stream = new ResponsesSocketStream(socket, _ => { });
    await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(new byte[10]));
  }

  [Fact]
  public async Task CancellationReachesReceive() {
    var socket = new ScriptedSocket(Frame.Text("{}"));
    using var stream = new ResponsesSocketStream(socket, _ => { });
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadExactlyAsync(new byte[10], cancellation.Token));
  }

  private sealed record Frame(byte[] Bytes, bool End, WebSocketMessageType Type = WebSocketMessageType.Text) {
    public static Frame Text(string text) => new(Encoding.UTF8.GetBytes(text), true);
  }

  private sealed class ScriptedSocket(params Frame[] frames) : WebSocket {
    private int index;
    private int offset;
    public int Receives { get; private set; }
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override void Dispose() { }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
      cancellationToken.ThrowIfCancellationRequested();
      Receives++;
      Frame frame = frames[index];
      int count = Math.Min(buffer.Count, frame.Bytes.Length - offset);
      frame.Bytes.AsSpan(offset, count).CopyTo(buffer.AsSpan());
      offset += count;
      bool consumed = offset == frame.Bytes.Length;
      if (consumed) { index++; offset = 0; }
      return Task.FromResult(new WebSocketReceiveResult(count, frame.Type, consumed && frame.End));
    }
  }
}
