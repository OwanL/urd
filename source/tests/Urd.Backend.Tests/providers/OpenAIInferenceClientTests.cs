using System.Net;
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
