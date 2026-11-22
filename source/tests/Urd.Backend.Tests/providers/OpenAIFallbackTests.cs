using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Xunit;
using static Urd.Backend.Tests.Support.ProviderModelStreams;

#pragma warning disable MEAI001

namespace Urd.Backend.Tests.Providers;

public sealed class OpenAIFallbackTests {
  [Theory]
  [InlineData("absent")]
  [InlineData("null")]
  [InlineData("server_error")]
  public async Task EarlyCorrelatedFailureFallsBackOnceAndLatchesHttp(string error) {
    using var fixture = new Fixture(Created(), Failed(error));
    List<ChatResponseUpdate> updates = await fixture.Read();
    Assert.Single(fixture.Handler.Requests);
    Assert.Contains(updates.SelectMany(update => update.Contents), content => content is TextContent text && text.Text == "hello");
    Assert.Contains(fixture.Diagnostics, diagnostic => diagnostic.Transport == "websocket" && diagnostic.Stage == "stream-fallback");

    await fixture.Read();
    Assert.Equal(2, fixture.Handler.Requests.Count);
    Assert.Single(fixture.Socket.Frames);
    Assert.Equal(1, fixture.Connections);
  }

  [Theory]
  [InlineData("other-code")]
  [InlineData("standalone")]
  [InlineData("incomplete")]
  [InlineData("text")]
  [InlineData("item")]
  [InlineData("argument")]
  [InlineData("transport")]
  [InlineData("terminal-output")]
  [InlineData("created-output")]
  [InlineData("progress-output")]
  [InlineData("error-object-without-code")]
  public async Task UnsafeFailureNeverFallsBack(string kind) {
    const string output = """[{"type":"message","id":"msg_fixture","status":"completed","role":"assistant","content":[{"type":"output_text","text":"final","annotations":[]}]}]""";
    string[] messages = kind switch {
      "other-code" => [Created(), Failed("invalid_request_error")],
      "standalone" => [Created(), "{\"type\":\"error\",\"sequence_number\":1,\"code\":\"server_error\",\"message\":\"failed\"}"],
      "incomplete" => [Created(), Terminal("response.incomplete", "incomplete", "null")],
      "text" => [Created(), "{\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"item_id\":\"msg_fixture\",\"output_index\":0,\"content_index\":0,\"delta\":\"partial\"}", Failed("server_error")],
      "item" => [Created(), "{\"type\":\"response.output_item.added\",\"sequence_number\":1,\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_fixture\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}", Failed("server_error")],
      "argument" => [Created(), "{\"type\":\"response.function_call_arguments.delta\",\"sequence_number\":1,\"item_id\":\"fc_fixture\",\"output_index\":0,\"delta\":\"{\"}", Failed("server_error")],
      "terminal-output" => [Created(), Failed("server_error", output)],
      "created-output" => [Terminal("response.created", "in_progress", "null", output), Failed("server_error")],
      "progress-output" => [Created(), Terminal("response.in_progress", "in_progress", "null", output), Failed("server_error")],
      "error-object-without-code" => [Created(), Terminal("response.failed", "failed", "{}")],
      _ => [Created()]
    };
    using var fixture = new Fixture(messages);
    fixture.Socket.FailAtEnd = kind == "transport";
    await Assert.ThrowsAsync<ProviderOperationException>(() => fixture.Read());
    Assert.Empty(fixture.Handler.Requests);
    Assert.DoesNotContain(fixture.Diagnostics, diagnostic => diagnostic.Stage == "stream-fallback");
  }

  [Fact]
  public async Task FallbackResendsFullHistoryAfterCompactedSocketContinuation() {
    string[] first = Responses([Message]).Split('\n')
        .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
        .Select(line => line[6..]).ToArray();
    string[] second = [Created().Replace("resp_fixture", "resp_second", StringComparison.Ordinal),
        Failed("server_error").Replace("resp_fixture", "resp_second", StringComparison.Ordinal)];
    using var fixture = new Fixture([.. first, .. second]);
    List<ChatResponseUpdate> initial = await fixture.Read();
    LlmProviderResponse response = Assert.Single(initial.Select(LlmProviderContext.GetResponse).OfType<LlmProviderResponse>());
    ChatMessage[] history = [new(ChatRole.User, "hello"), LlmProviderContext.Restore(response), new(ChatRole.User, "next")];

    await fixture.Read(history);

    using var socketRequest = JsonDocument.Parse(fixture.Socket.Frames[1]);
    Assert.Equal("resp_fixture", socketRequest.RootElement.GetProperty("previous_response_id").GetString());
    Assert.Single(socketRequest.RootElement.GetProperty("input").EnumerateArray());
    using var httpRequest = JsonDocument.Parse(Assert.Single(fixture.Handler.Requests).Body);
    Assert.False(httpRequest.RootElement.TryGetProperty("previous_response_id", out _));
    Assert.Equal(3, httpRequest.RootElement.GetProperty("input").GetArrayLength());
  }

  [Fact]
  public async Task CancellationWhileWaitingForSocketDoesNotFallBackToHttp() {
    using var fixture = new Fixture(Created());
    using var cancellation = new CancellationTokenSource();
    Task<List<ChatResponseUpdate>> read = fixture.Read(cancellationToken: cancellation.Token);
    await fixture.Socket.ReceiveBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    Assert.Empty(fixture.Handler.Requests);
    Assert.DoesNotContain(fixture.Diagnostics, diagnostic => diagnostic.Stage == "stream-fallback");
  }

  [Fact]
  public async Task HttpFallbackFailureIsNotRetried() {
    using var fixture = new Fixture([Created(), Failed("server_error")], HttpStatusCode.ServiceUnavailable);
    await Assert.ThrowsAsync<ProviderOperationException>(() => fixture.Read());
    Assert.Single(fixture.Handler.Requests);
    Assert.Single(fixture.Socket.Frames);
  }

  private static string Created() => Terminal("response.created", "in_progress", "null");

  private static string Failed(string error, string output = "[]") => Terminal("response.failed", "failed", error switch {
    "absent" => "absent",
    "null" => "null",
    _ => JsonSerializer.Serialize(new { code = error, message = "fixture failure" })
  }, output);

  private static string Terminal(string type, string status, string error, string output = "[]") {
    string errorProperty = error == "absent" ? "" : $",\"error\":{error}";
    return $$$"""{"type":"{{{type}}}","sequence_number":1,"response":{"id":"resp_fixture","object":"response","created_at":1700000000,"model":"fixture-model","status":"{{{status}}}","store":false,"output":{{{output}}}{{{errorProperty}}},"incomplete_details":null}}""";
  }

  private sealed class Fixture : IDisposable {
    private readonly ChatGptSocketRun run = new();
    private readonly HttpClient http;
    private readonly OpenAIInferenceClient inference;
    private readonly Guid session = Guid.NewGuid();
    private readonly ProviderState state = new(new(Guid.NewGuid(), "ChatGPT", ProviderRoute.ChatGpt, LlmApi.OpenAIResponses,
        new Uri("https://api.openai.com/v1/"), AuthenticationMethod.OAuth),
        1, 1, null, new(null, "fixture-token", null, null, null, []), false, null);
    internal ScriptedOpenAIHandler Handler { get; }
    internal ScriptedSocket Socket { get; }
    internal List<ProviderDiagnostic> Diagnostics { get; } = [];
    internal int Connections { get; private set; }

    internal Fixture(params string[] messages) : this(messages, HttpStatusCode.OK) { }
    internal Fixture(string[] messages, HttpStatusCode status) {
      Socket = new(messages);
      Handler = new(status == HttpStatusCode.OK ? Responses([Message]) : "{}",
          status == HttpStatusCode.OK ? "text/event-stream" : "application/json", status);
      http = new(Handler);
      inference = new(http, connectSocket: (_, _, _) => {
        Connections++;
        return Task.FromResult<WebSocket>(Socket);
      }, diagnose: Diagnostics.Add);
    }

    internal async Task<List<ChatResponseUpdate>> Read(ChatMessage[]? history = null, CancellationToken cancellationToken = default) {
      var options = new ChatOptions { ModelId = "fixture-model", AdditionalProperties = new() {
        [OpenAIInferenceClient.SessionIdProperty] = session, [ChatGptSocketRun.PropertyName] = run
      } };
      List<ChatResponseUpdate> updates = [];
      await foreach (var update in inference.GetStreamingResponseAsync(state, history ?? [new(ChatRole.User, "hello")], options, cancellationToken))
        updates.Add(update);
      return updates;
    }

    public void Dispose() { run.Dispose(); http.Dispose(); }
  }

  private sealed class ScriptedSocket(string[] messages) : WebSocket {
    private int index;
    private WebSocketState state = WebSocketState.Open;
    internal List<string> Frames { get; } = [];
    internal TaskCompletionSource ReceiveBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool FailAtEnd { get; set; }
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;
    public override void Abort() => state = WebSocketState.Aborted;
    public override void Dispose() => state = WebSocketState.Closed;
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
      Frames.Add(Encoding.UTF8.GetString(buffer.AsSpan()));
      return Task.CompletedTask;
    }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
      if (index >= messages.Length) {
        if (FailAtEnd)
          throw new WebSocketException();
        ReceiveBlocked.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException();
      }
      byte[] bytes = Encoding.UTF8.GetBytes(messages[index++]);
      bytes.CopyTo(buffer.AsSpan());
      return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
    }
  }
}
