using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Tools;
using Xunit;

#pragma warning disable MEAI001

namespace Urd.Backend.Tests.Providers;

public sealed class ResponsesTerminalDiagnosticsTests {
  private const string Created = "{\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"sentinel-id\",\"object\":\"response\",\"created_at\":1700000000,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}";
  private const string Failed = "{\"type\":\"response.failed\",\"sequence_number\":1,\"response\":{\"id\":\"sentinel-id\",\"object\":\"response\",\"created_at\":1700000000,\"model\":\"fixture-model\",\"status\":\"failed\",\"output\":[]}}";
  private const string Error = "{\"type\":\"error\",\"sequence_number\":1}";

  [Theory]
  [InlineData(Failed, ProviderStreamTerminalKind.ResponseFailed)]
  [InlineData(Error, ProviderStreamTerminalKind.StandaloneError)]
  public async Task WireFailureWithoutDetailsIsNotPeerDisconnect(string terminal, ProviderStreamTerminalKind kind) {
    var (error, diagnostic, socket) = await SocketFailure(Created, terminal);

    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.Equal("ProviderUnavailable", error.Message);
    Assert.NotNull(diagnostic.WireTerminal);
    Assert.Equal(kind, diagnostic.WireTerminal.Kind);
    Assert.Equal(kind, diagnostic.SdkTerminal);
    Assert.Equal(ProviderStreamTerminalErrorShape.Missing, diagnostic.WireTerminal.ErrorShape);
    Assert.Equal(ProviderStreamTerminalErrorCode.Missing, diagnostic.WireTerminal.ErrorCode);
    Assert.False(diagnostic.WireTerminal.MessagePresent);
    Assert.Equal("websocket", diagnostic.Transport);
    Assert.Equal("stream", diagnostic.Stage);
    Assert.Null(diagnostic.HttpStatus);
    Assert.Null(diagnostic.ExceptionType);
    Assert.Null(diagnostic.WebSocketReceiveFailure);
    Assert.Equal(2, socket.Receives);
    Assert.DoesNotContain("sentinel-id", JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
  }

  [Fact]
  public async Task NestedSocketErrorCanLoseItsCodeAndMessageDuringSdkProjection() {
    const string terminal = """{"type":"error","status":429,"error":{"type":"rate_limit_error","code":"rate_limit_exceeded","message":"fixture-rate-limit"}}""";
    var (error, diagnostic, _) = await SocketFailure(Created, terminal);

    Assert.Equal("ProviderUnavailable", error.Message);
    Assert.Equal(ProviderStreamTerminalKind.StandaloneError, diagnostic.SdkTerminal);
    Assert.Equal(ProviderStreamTerminalErrorShape.ErrorObject, diagnostic.WireTerminal?.ErrorShape);
    Assert.Equal(ProviderStreamTerminalErrorCode.RateLimitExceeded, diagnostic.WireTerminal?.ErrorCode);
    Assert.Equal(429, diagnostic.WireTerminal?.EventStatusCode);
    Assert.Null(diagnostic.HttpStatus); // A socket error's status is not an HTTP response.
    Assert.True(diagnostic.WireTerminal?.MessagePresent);
    Assert.DoesNotContain("fixture-rate-limit", JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
  }

  [Fact]
  public async Task PeerCloseHasNoTerminalAndRetainsReceiveFailure() {
    var (error, diagnostic, socket) = await SocketFailure(Created);

    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.Contains("peer closed", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Null(diagnostic.WireTerminal);
    Assert.Null(diagnostic.SdkTerminal);
    Assert.Equal("PeerClosed", diagnostic.WebSocketReceiveFailure);
    Assert.Equal(2, socket.Receives);
  }

  [Fact]
  public async Task MismatchedTerminalIsRecordedButNotPassedToSdk() {
    string wrong = Failed.Replace("sentinel-id", "other-id", StringComparison.Ordinal);
    var (error, diagnostic, _) = await SocketFailure(Created, wrong);

    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.Equal(ProviderStreamTerminalKind.ResponseFailed, diagnostic.WireTerminal?.Kind);
    Assert.Equal(ProviderStreamTerminalCorrelation.Mismatch, diagnostic.WireTerminal?.Correlation);
    Assert.Null(diagnostic.SdkTerminal);
    Assert.Equal("InvalidEvent", diagnostic.WebSocketReceiveFailure);
    Assert.DoesNotContain("other-id", JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
  }

  [Fact]
  public async Task DiagnosticDoesNotPersistTerminalDetails() {
    string terminal = Failed.Replace("\"status\":\"failed\"", "\"status\":\"failed\",\"error\":{\"code\":\"sentinel-code\",\"message\":\"sentinel-message\"}", StringComparison.Ordinal);
    var (_, diagnostic, _) = await SocketFailure(Created, terminal);

    Assert.Equal(ProviderStreamTerminalKind.ResponseFailed, diagnostic.WireTerminal?.Kind);
    string persisted = JsonSerializer.Serialize(diagnostic);
    Assert.DoesNotContain("sentinel-code", persisted, StringComparison.Ordinal);
    Assert.DoesNotContain("sentinel-message", persisted, StringComparison.Ordinal);
    Assert.DoesNotContain("sentinel-id", persisted, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ChatGptSocketSendsAskUserWithItsEffectiveSchema() {
    var socket = new ScriptedSocket([Created, Failed]);
    using var run = new ChatGptSocketRun();
    using var handler = new ScriptedOpenAIHandler("unexpected");
    using var http = new HttpClient(handler);
    var inference = new OpenAIInferenceClient(http, connectSocket: (_, _, _) => Task.FromResult<WebSocket>(socket));
    var options = Options();
    options.Tools = [AgentToolDeclaration.Create(new AskUserTool(null!))];
    options.AdditionalProperties = new() {
      [OpenAIInferenceClient.SessionIdProperty] = Guid.NewGuid(),
      [ChatGptSocketRun.PropertyName] = run
    };

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var _ in inference.GetStreamingResponseAsync(CreateState(ProviderRoute.ChatGpt), History(), options, default)) { /* Drain to the terminal. */ }
    });

    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.Empty(handler.Requests);
    using var request = JsonDocument.Parse(Assert.Single(socket.Sends));
    JsonElement namespaceTool = Assert.Single(request.RootElement.GetProperty("tools").EnumerateArray());
    Assert.Equal("namespace", namespaceTool.GetProperty("type").GetString());
    Assert.Equal("urd", namespaceTool.GetProperty("name").GetString());
    JsonElement function = Assert.Single(namespaceTool.GetProperty("tools").EnumerateArray());
    Assert.Equal("ask_user", function.GetProperty("name").GetString());
    JsonElement schema = function.GetProperty("parameters");
    JsonElement properties = schema.GetProperty("properties");
    Assert.Equal(new[] { "choices", "context", "question" },
        properties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    Assert.Equal("string", properties.GetProperty("question").GetProperty("type").GetString());
    Assert.Equal("string", properties.GetProperty("context").GetProperty("type").GetString());
    Assert.Equal("array", properties.GetProperty("choices").GetProperty("type").GetString());
    Assert.Equal("string", properties.GetProperty("choices").GetProperty("items").GetProperty("type").GetString());
    Assert.Equal(new[] { "choices", "question" },
        schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
    string serializedSchema = schema.GetRawText();
    Assert.DoesNotContain("\"background\"", serializedSchema, StringComparison.Ordinal);
    Assert.DoesNotContain("\"uniqueItems\"", serializedSchema, StringComparison.Ordinal);
    Assert.DoesNotContain("\"minLength\"", serializedSchema, StringComparison.Ordinal);
    Assert.DoesNotContain("\"maxLength\"", serializedSchema, StringComparison.Ordinal);
  }

  [Fact]
  public async Task HttpFailureHasNativeTerminalButNoWireObservation() {
    string wire = OpenAIStreams.Event("response.created", JsonDocument.Parse(Created).RootElement) +
        OpenAIStreams.Event("response.failed", JsonDocument.Parse(Failed).RootElement);
    using var handler = new ScriptedOpenAIHandler(wire);
    using var http = new HttpClient(handler);
    List<ProviderDiagnostic> diagnostics = [];
    var inference = new OpenAIInferenceClient(http, diagnose: diagnostics.Add);

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var _ in inference.GetStreamingResponseAsync(CreateState(ProviderRoute.Custom), History(), Options(), default)) { /* Drain to the terminal. */ }
    });

    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.Equal("ProviderUnavailable", error.Message);
    var diagnostic = Assert.Single(diagnostics);
    Assert.Null(diagnostic.WireTerminal);
    Assert.Equal(ProviderStreamTerminalKind.ResponseFailed, diagnostic.SdkTerminal);
    Assert.Single(handler.Requests);
  }

  private static async Task<(ProviderOperationException, ProviderDiagnostic, ScriptedSocket)> SocketFailure(params string[] messages) {
    var socket = new ScriptedSocket(messages);
    using var run = new ChatGptSocketRun();
    using var handler = new ScriptedOpenAIHandler("unexpected");
    using var http = new HttpClient(handler);
    List<ProviderDiagnostic> diagnostics = [];
    var inference = new OpenAIInferenceClient(http, connectSocket: (_, _, _) => Task.FromResult<WebSocket>(socket), diagnose: diagnostics.Add);
    var options = Options();
    options.AdditionalProperties = new() {
      [OpenAIInferenceClient.SessionIdProperty] = Guid.NewGuid(),
      [ChatGptSocketRun.PropertyName] = run
    };
    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var _ in inference.GetStreamingResponseAsync(CreateState(ProviderRoute.ChatGpt), History(), options, default)) { /* Drain to the terminal. */ }
    });
    Assert.Empty(handler.Requests);
    return (error, Assert.Single(diagnostics), socket);
  }

  private static ProviderState CreateState(ProviderRoute route) => new(
      new(Guid.NewGuid(), "Fixture", route, LlmApi.OpenAIResponses,
          new Uri(route == ProviderRoute.ChatGpt ? "https://api.openai.com/v1/" : "https://fixture.invalid/v1"),
          route == ProviderRoute.ChatGpt ? AuthenticationMethod.OAuth : AuthenticationMethod.None),
      1, 1, null, new(null, "fixture-token", null, null, null, []), false, null);

  private static ChatMessage[] History() => [new(ChatRole.User, "hello")];
  private static ChatOptions Options() => new() { ModelId = "fixture-model" };

  private sealed class ScriptedSocket(string[] messages) : WebSocket {
    private int index;
    public int Receives { get; private set; }
    public List<string> Sends { get; } = [];
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override void Dispose() { }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
      Sends.Add(Encoding.UTF8.GetString(buffer.AsSpan()));
      return Task.CompletedTask;
    }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
      cancellationToken.ThrowIfCancellationRequested();
      Receives++;
      if (index == messages.Length)
        return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
      byte[] bytes = Encoding.UTF8.GetBytes(messages[index++]);
      bytes.CopyTo(buffer.AsSpan());
      return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
    }
  }
}

#pragma warning restore MEAI001
