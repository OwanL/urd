using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Xunit;
using static Urd.Backend.Tests.Support.OpenAIStreams;
using static Urd.Backend.Tests.Support.ProviderModelStreams;

#pragma warning disable MEAI001

namespace Urd.Backend.Tests.Providers;

public sealed class OpenAIInferenceClientTests {
  [Theory]
  [InlineData(ProviderRoute.ChatGpt, LlmApi.OpenAIResponses, "developer")]
  [InlineData(ProviderRoute.OpenAI, LlmApi.OpenAIResponses, "system")]
  [InlineData(ProviderRoute.Custom, LlmApi.OpenAIChatCompletions, "system")]
  public async Task InstructionsUseRouteAppropriateRolesWithoutMutatingSavedHistory(
      ProviderRoute route, LlmApi api, string expectedRole) {
    ChatMessage[] history = [new(ChatRole.System, "first instruction"),
        new(ChatRole.System, "second instruction"), new(ChatRole.User, "hello")];
    using var handler = new ScriptedOpenAIHandler(api == LlmApi.OpenAIResponses
        ? Responses([Message]) : ChatStream());
    using var http = new HttpClient(handler);

    await foreach (var _ in new OpenAIInferenceClient(http).GetStreamingResponseAsync(
        State(api, route), history, Options(), default)) { /* Drain before reading the wire. */ }

    using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
    JsonElement input = document.RootElement.GetProperty(api == LlmApi.OpenAIResponses ? "input" : "messages");
    Assert.Equal([expectedRole, expectedRole, "user"],
        input.EnumerateArray().Select(item => item.GetProperty("role").GetString()));
    Assert.Equal([ChatRole.System, ChatRole.System, ChatRole.User], history.Select(message => message.Role));
  }

  [Fact]
  public async Task OpenAIApiKeyRouteDoesNotSendSessionIdentity() {
    Guid sessionId = Guid.NewGuid();
    var options = Options();
    options.AdditionalProperties = new() { [OpenAIInferenceClient.SessionIdProperty] = sessionId };
    var state = new ProviderState(
        new(Guid.NewGuid(), "OpenAI", ProviderRoute.OpenAI, LlmApi.OpenAIResponses,
            new Uri("https://api.openai.com/v1/"), AuthenticationMethod.ApiKey),
        7, 2, null, new("synthetic-key", null, null, null, null, []), false, null);
    using var handler = new ScriptedOpenAIHandler(Responses([Message]));
    using var http = new HttpClient(handler);

    await foreach (var _ in new OpenAIInferenceClient(http).GetStreamingResponseAsync(
        state, History(), options, default)) { /* Drain before reading the wire. */ }

    ScriptedOpenAIRequest request = Assert.Single(handler.Requests);
    Assert.Equal("Bearer synthetic-key", request.Authorization);
    Assert.Empty(request.SessionIds!);
    using var document = JsonDocument.Parse(request.Body);
    Assert.DoesNotContain(OpenAIInferenceClient.SessionIdProperty, document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("session-id", request.Body, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(sessionId.ToString(), request.Body, StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData("response.failed", ProviderFailure.ProviderUnavailable)]
  [InlineData("response.incomplete", ProviderFailure.IncompleteResponse)]
  [InlineData(null, ProviderFailure.IncompleteResponse)]
  public async Task ResponsesFailureOrMissingTerminalNeverReleasesCalls(string? terminal, ProviderFailure failure) =>
      await Rejected(Responses([Message, Call], terminal), failure, options: Options(tools: true));

  [Theory]
  [InlineData("\"reasoning\":42")]
  [InlineData("\"reasoning_content\":42")]
  [InlineData("\"reasoning\":\"other\",\"reasoning_content\":\"thought\"")]
  public async Task InvalidOrAmbiguousReasoningCannotAuthorizeCalls(string replacement) =>
      await Rejected(ChatCalls(reasoning: true).Replace(
          "\"reasoning_content\":\"thought\"", replacement, StringComparison.Ordinal),
          ProviderFailure.InvalidProviderResponse, State(LlmApi.OpenAIChatCompletions), Options(tools: true));

  [Theory]
  [InlineData(
      "{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"private-token\"}}",
      ProviderFailure.ContextWindowExceeded)]
  [InlineData(
      "{\"error\":{\"code\":\"invalid_request_error\",\"message\":\"context_length_exceeded private-token\"}}",
      ProviderFailure.ProviderUnavailable)]
  [InlineData(
      "{\"error\":{\"message\":\"context_length_exceeded private-token\"}}",
      ProviderFailure.ProviderUnavailable)]
  public async Task HttpFailuresClassifyOnlyAnExplicitContextWindowCode(string body, ProviderFailure failure) {
    using var handler = new ScriptedOpenAIHandler(body, "application/json", HttpStatusCode.BadRequest);
    using var http = new HttpClient(handler);

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var _ in new OpenAIInferenceClient(http).GetStreamingResponseAsync(
          State(), History(), Options(), default)) { /* Exhaust the stream to observe its terminal failure. */ }
    });

    Assert.Equal(failure, error.Failure);
    Assert.StartsWith($"{failure} (HTTP 400)", error.Message);
    Assert.Null(error.InnerException);
  }

  [Theory]
  [InlineData(HttpStatusCode.Unauthorized, ProviderFailure.AuthenticationRequired)]
  [InlineData(HttpStatusCode.Forbidden, ProviderFailure.PlanAccessDenied)]
  [InlineData(HttpStatusCode.InternalServerError, ProviderFailure.ProviderUnavailable)]
  [InlineData(HttpStatusCode.BadRequest, ProviderFailure.ProviderUnavailable)]
  public async Task ErrorsAreSecretSafeAndNeverRetried(HttpStatusCode status, ProviderFailure failure) {
    using var handler = new ScriptedOpenAIHandler("{\"error\":{\"message\":\"private-token\"}}", "application/json", status);
    using var http = new HttpClient(handler);

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var update in new OpenAIInferenceClient(http).GetStreamingResponseAsync(
          State(), History(), Options(), default))
        Assert.Fail($"HTTP failure yielded content: {update}");
    });

    Assert.Equal(failure, error.Failure);
    Assert.StartsWith($"{failure} (HTTP {(int)status})", error.Message);
    Assert.Contains("private-token", error.Message); // Provider text is retained; credential redaction has separate coverage.
    Assert.Null(error.InnerException);
    Assert.Single(handler.Requests);
  }

  [Fact]
  public async Task ConnectionFailureReportsNoHttpResponseAndSafeCause() {
    using var http = new HttpClient(new DroppedConnection());
    List<ProviderDiagnostic> diagnostics = [];
    var inference = new OpenAIInferenceClient(http, diagnose: diagnostics.Add);
    ProviderState state = State();
    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var _ in inference.GetStreamingResponseAsync(state, History(), Options(), default))
        Assert.Fail("Unexpected model response.");
    });

    Assert.Equal("ProviderUnavailable: No HTTP response received from the provider.", error.Message);
    Assert.Null(error.InnerException);
    var diagnostic = Assert.Single(diagnostics);
    Assert.Equal("https", diagnostic.Transport);
    Assert.Equal("response-headers", diagnostic.Stage);
    Assert.Null(diagnostic.HttpStatus);
    Assert.Equal("ConnectionError", diagnostic.HttpRequestError);
    Assert.Equal("ConnectionReset", diagnostic.SocketError);
    Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
  }

  [Fact]
  public async Task WebSocketHandshakeFailureRecordsFallbackAndHttpRejectionSeparately() {
    var state = new ProviderState(new(Guid.NewGuid(), "ChatGPT", ProviderRoute.ChatGpt, LlmApi.OpenAIResponses,
        new Uri("https://api.openai.com/v1/"), AuthenticationMethod.OAuth),
        1, 1, null, new(null, "fixture-token", null, null, null, []), false, null);
    using var run = new ChatGptSocketRun();
    var options = Options();
    Guid session = Guid.NewGuid();
    options.AdditionalProperties = new() {
      [OpenAIInferenceClient.SessionIdProperty] = session,
      [ChatGptSocketRun.PropertyName] = run
    };
    using var handler = new ScriptedOpenAIHandler("{}", "application/json", HttpStatusCode.ServiceUnavailable);
    using var http = new HttpClient(handler);
    List<ProviderDiagnostic> diagnostics = [];
    var inference = new OpenAIInferenceClient(http, connectSocket: (_, _, _) =>
        Task.FromException<WebSocket>(new WebSocketException(WebSocketError.NotAWebSocket)), diagnose: diagnostics.Add);
    await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var _ in inference.GetStreamingResponseAsync(state, History(), options, default))
        Assert.Fail("Unexpected model response.");
    });

    Assert.Collection(diagnostics,
        fallback => {
          Assert.Equal(session, fallback.SessionId);
          Assert.Equal("websocket", fallback.Transport);
          Assert.Equal("handshake-fallback", fallback.Stage);
          Assert.Equal("NotAWebSocket", fallback.WebSocketError);
        },
        rejection => {
          Assert.Equal(session, rejection.SessionId);
          Assert.Equal("https", rejection.Transport);
          Assert.Equal(503, rejection.HttpStatus);
        });
    Assert.Single(handler.Requests);
  }

  private sealed class DroppedConnection : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ConnectionError, "fixture-secret",
            new SocketException((int)SocketError.ConnectionReset)));
  }

  private static async Task Rejected(string wire, ProviderFailure failure, ProviderState? state = null,
      ChatOptions? options = null, string? reason = null) {
    using var handler = new ScriptedOpenAIHandler(wire);
    using var http = new HttpClient(handler);
    List<ChatResponseUpdate> observed = [];

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var update in new OpenAIInferenceClient(http).GetStreamingResponseAsync(
          state ?? State(), History(), options ?? Options(), default))
        observed.Add(update);
    });

    Assert.Equal(failure, error.Failure);
    if (reason is not null) {
      Assert.Equal(reason, error.Reason);
      Assert.Equal($"{failure}: {reason}", error.Message);
    }
    Assert.DoesNotContain(observed.SelectMany(update => update.Contents), content => content is FunctionCallContent);
    Assert.All(observed, update => Assert.Null(update.FinishReason));
    Assert.All(observed, update => Assert.Null(LlmProviderContext.GetResponse(update)));
  }

  private static ChatOptions Options(bool tools = false) => new() {
    ModelId = "fixture-model",
    Tools = tools ? [AIFunctionFactory.Create((string key) => key, name: "lookup")] : null
  };

  private static ChatMessage[] History() => [new(ChatRole.User, "hello")];

  private static ProviderState State(LlmApi api = LlmApi.OpenAIResponses, ProviderRoute route = ProviderRoute.Custom) => new(
      new(Guid.NewGuid(), "Fixture", route, api, new Uri("https://fixture.invalid/prefix/v1"), AuthenticationMethod.None),
      7, 2, null, null, false, null);
}

#pragma warning restore MEAI001
