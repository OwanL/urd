using System.Net;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Xunit;
using static Urd.Backend.Tests.Support.OpenAIStreams;
using static Urd.Backend.Tests.Support.ProviderModelStreams;
using static Urd.Backend.Tests.Support.TestAsync;

#pragma warning disable MEAI001

namespace Urd.Backend.Tests.Providers;

public sealed class ProviderModelClientTests {
  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task CallsAndFinishRemainWithheldWhenNativeStreamCleanupFails(LlmApi api) {
    using var handler = new DisposalFailingOpenAIHandler(
        api == LlmApi.OpenAIResponses ? Responses([Message, Call]) : ChatCalls());
    using var http = new HttpClient(handler);
    List<ChatResponseUpdate> observed = [];

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var update in new ProviderModelClient(http).GetStreamingResponseAsync(
          State(api), History(), Options(tools: true), default))
        observed.Add(update);
    });

    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.Null(error.InnerException);
    Assert.Equal(1, handler.Disposals);
    Assert.DoesNotContain(observed.SelectMany(update => update.Contents), content => content is FunctionCallContent);
    Assert.All(observed, update => Assert.Null(update.FinishReason));
  }

  [Theory]
  [InlineData("response.failed", ProviderFailure.ProviderUnavailable)]
  [InlineData("response.incomplete", ProviderFailure.IncompleteResponse)]
  [InlineData(null, ProviderFailure.IncompleteResponse)]
  public async Task ResponsesFailureOrMissingTerminalNeverReleasesCalls(string? terminal, ProviderFailure failure) =>
      await Rejected(Responses([Message, Call], terminal), failure, options: Options(tools: true));

  [Theory]
  [InlineData("")]
  [InlineData(",\"namespace\":\"other\"")]
  public async Task PlanRejectsUnqualifiedOrWrongNamespaceCalls(string ns) {
    string call = Call.Replace("\"name\":\"lookup\"", "\"name\":\"lookup\"" + ns, StringComparison.Ordinal);
    await Rejected(Responses([Message, call]), ProviderFailure.InvalidProviderResponse,
        State(route: ProviderRoute.ChatGpt), Options(tools: true));
  }

  [Theory]
  [InlineData("\"reasoning\":42")]
  [InlineData("\"reasoning_content\":42")]
  [InlineData("\"reasoning\":\"other\",\"reasoning_content\":\"thought\"")]
  public async Task InvalidOrAmbiguousReasoningCannotAuthorizeCalls(string replacement) =>
      await Rejected(ChatCalls(reasoning: true).Replace(
          "\"reasoning_content\":\"thought\"", replacement, StringComparison.Ordinal),
          ProviderFailure.InvalidProviderResponse, State(LlmApi.OpenAIChatCompletions), Options(tools: true));

  [Theory]
  [InlineData(HttpStatusCode.Unauthorized, ProviderFailure.AuthenticationRequired)]
  [InlineData(HttpStatusCode.Forbidden, ProviderFailure.PlanAccessDenied)]
  [InlineData(HttpStatusCode.InternalServerError, ProviderFailure.ProviderUnavailable)]
  public async Task ErrorsAreSecretSafeAndNeverRetried(HttpStatusCode status, ProviderFailure failure) {
    using var handler = new ScriptedOpenAIHandler("{\"error\":{\"message\":\"private-token\"}}", "application/json", status);
    using var http = new HttpClient(handler);

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var update in new ProviderModelClient(http).GetStreamingResponseAsync(
          State(), History(), Options(), default))
        Assert.Fail($"HTTP failure yielded content: {update}");
    });

    Assert.Equal(failure, error.Failure);
    Assert.Equal(failure.ToString(), error.Message);
    Assert.Null(error.InnerException);
    Assert.DoesNotContain("private-token", error.ToString());
    Assert.Single(handler.Requests);
  }

  private static async Task Rejected(string wire, ProviderFailure failure, ProviderState? state = null, ChatOptions? options = null) {
    using var handler = new ScriptedOpenAIHandler(wire);
    using var http = new HttpClient(handler);
    List<ChatResponseUpdate> observed = [];

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var update in new ProviderModelClient(http).GetStreamingResponseAsync(
          state ?? State(), History(), options ?? Options(), default))
        observed.Add(update);
    });

    Assert.Equal(failure, error.Failure);
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
