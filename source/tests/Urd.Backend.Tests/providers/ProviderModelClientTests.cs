using System.Net;
using System.Text.Json;
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
  public async Task TextIsIncrementalAndCompletionFollowsNativeCleanup(LlmApi api) {
    using var handler = new ScriptedOpenAIHandler(api == LlmApi.OpenAIResponses ? Responses() : ChatStream(done: false)) { HoldAtEnd = true };
    using var http = new HttpClient(handler);
    using var cancellation = new CancellationTokenSource();
    await using var stream = new ProviderModelClient(http).GetStreamingResponseAsync(State(api), History(), Options(), cancellation.Token).GetAsyncEnumerator();

    Assert.True(await stream.MoveNextAsync());
    Assert.Equal("hello", Assert.IsType<TextContent>(Assert.Single(stream.Current.Contents)).Text);
    Assert.Null(stream.Current.FinishReason);
    Assert.Null(LlmProviderContext.GetResponse(stream.Current));
    var next = stream.MoveNextAsync().AsTask();
    await handler.ReachedEnd.Task.WaitAsync(TestTimeout);
    Assert.False(next.IsCompleted);
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    Assert.Equal(1, handler.StreamDisposals);
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task SuccessfulStreamHasOneFinalContextAndBorrowsTransport(LlmApi api) {
    using var handler = new ScriptedOpenAIHandler(Wire(api));
    using var http = new HttpClient(handler);
    var state = State(api);
    var client = new ProviderModelClient(http);
    var updates = await Read(client, state);

    var final = updates[^1];
    Assert.Equal(ChatFinishReason.Stop, final.FinishReason);
    Assert.Single(updates, update => update.FinishReason is not null);
    var response = Assert.IsType<LlmProviderResponse>(LlmProviderContext.GetResponse(final));
    Assert.Equal(new LlmProviderScope(state.Connection.Id, state.Generation, api, "fixture-model"), response.Scope);
    Assert.Equal("hello", LlmProviderContext.Restore(response).Text);
    Assert.Equal(1, handler.StreamDisposals);
    await Read(client, state);
    Assert.Equal(2, handler.Requests.Count);
    Assert.All(handler.Requests, request => {
      Assert.Null(request.Authorization);
      Assert.Equal(api == LlmApi.OpenAIResponses ? "/prefix/v1/responses" : "/prefix/v1/chat/completions", request.Uri.AbsolutePath);
      using var body = JsonDocument.Parse(request.Body);
      Assert.Equal("fixture-model", body.RootElement.GetProperty("model").GetString());
      Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
      Assert.False(body.RootElement.GetProperty("store").GetBoolean());
      Assert.False(body.RootElement.TryGetProperty("conversation", out _));
      Assert.False(body.RootElement.TryGetProperty("previous_response_id", out _));
      if (api == LlmApi.OpenAIResponses)
        Assert.Contains(body.RootElement.GetProperty("include").EnumerateArray(), value => value.GetString() == "reasoning.encrypted_content");
    });
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task CallsAreAssembledButNeverPublishedBeforeTerminalAndCleanup(LlmApi api) {
    string wire = api == LlmApi.OpenAIResponses ? Responses([Message, Reasoning, Call]) : ChatCalls(reasoning: true).Replace("data: [DONE]\n\n", "");
    using var handler = new ScriptedOpenAIHandler(wire) { FailAtEnd = true };
    using var http = new HttpClient(handler);
    List<ChatResponseUpdate> observed = [];

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var update in new ProviderModelClient(http).GetStreamingResponseAsync(State(api), History(), Options(tools: true), default))
        observed.Add(update);
    });
    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.NotEmpty(observed.SelectMany(update => update.Contents).OfType<TextContent>());
    Assert.DoesNotContain(observed.SelectMany(update => update.Contents), content => content is FunctionCallContent);
    Assert.All(observed, update => Assert.Null(update.FinishReason));
    Assert.Equal(1, handler.StreamDisposals);
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task DisposalFailureAfterNativeTerminalStillWithholdsCallsAndFinish(LlmApi api) {
    using var handler = new DisposalFailingOpenAIHandler(api == LlmApi.OpenAIResponses ? Responses([Message, Call]) : ChatCalls());
    using var http = new HttpClient(handler);
    List<ChatResponseUpdate> observed = [];
    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var update in new ProviderModelClient(http).GetStreamingResponseAsync(State(api), History(), Options(tools: true), default))
        observed.Add(update);
    });
    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.Null(error.InnerException);
    Assert.Equal(1, handler.Disposals);
    Assert.DoesNotContain(observed.SelectMany(update => update.Contents), content => content is FunctionCallContent);
    Assert.All(observed, update => Assert.Null(update.FinishReason));
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task CompletedFunctionCallsAndReasoningRoundTripWithoutInvokingTools(LlmApi api) {
    int invocations = 0;
    var options = Options();
    options.Tools = [AIFunctionFactory.Create((string key) => { invocations++; return key; }, name: "lookup")];
    using var handler = new ScriptedOpenAIHandler(api == LlmApi.OpenAIResponses ? Responses([Message, Reasoning, Call]) : ChatCalls(reasoning: true));
    using var http = new HttpClient(handler);
    var updates = await Read(new ProviderModelClient(http), State(api), options);

    Assert.Equal(0, invocations);
    Assert.Equal(ChatFinishReason.ToolCalls, updates[^1].FinishReason);
    Assert.DoesNotContain(updates[..^1].SelectMany(update => update.Contents), content => content is FunctionCallContent);
    var call = Assert.IsType<FunctionCallContent>(Assert.Single(updates[^1].Contents));
    Assert.Equal("lookup", call.Name);
    Assert.Equal("call_fixture", call.CallId);
    Assert.Equal("abc", call.Arguments!["key"]!.ToString());
    var restored = LlmProviderContext.Restore(LlmProviderContext.GetResponse(updates[^1])!);
    Assert.Single(restored.Contents.OfType<FunctionCallContent>());
    Assert.Single(restored.Contents.OfType<TextReasoningContent>());
    Assert.Equal(1, handler.StreamDisposals);
  }

  [Fact]
  public async Task PlanToolsUseAnOrderedNamespaceAndVerifyTheReturnedNamespace() {
    string call = Call.Replace("\"name\":\"lookup\"", "\"name\":\"lookup\",\"namespace\":\"urd\"");
    using var handler = new ScriptedOpenAIHandler(Responses([Message, call]));
    using var http = new HttpClient(handler);
    var options = Options(tools: true);
    options.Reasoning = new() { Effort = ReasoningEffort.Low };
    var updates = await Read(new ProviderModelClient(http), State(route: ProviderRoute.ChatGpt), options);
    Assert.Equal(ChatFinishReason.ToolCalls, updates[^1].FinishReason);
    using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
    var tool = Assert.Single(body.RootElement.GetProperty("tools").EnumerateArray());
    Assert.Equal("namespace", tool.GetProperty("type").GetString());
    Assert.Equal("urd", tool.GetProperty("name").GetString());
    Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
    var function = Assert.Single(tool.GetProperty("tools").EnumerateArray());
    Assert.Equal("function", function.GetProperty("type").GetString());
    Assert.Equal("lookup", function.GetProperty("name").GetString());
    Assert.True(!function.TryGetProperty("description", out var description) || description.ValueKind == JsonValueKind.String);
    Assert.Equal("low", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    Assert.Single(body.RootElement.GetProperty("input").EnumerateArray());
  }

  [Fact]
  public async Task PlanDoesNotSilentlyDropRequestedToolStrictness() {
    using var handler = new ScriptedOpenAIHandler(Responses());
    using var http = new HttpClient(handler);
    var options = Options();
    options.Tools = [AIFunctionFactory.Create((string key) => key, new AIFunctionFactoryOptions {
      Name = "lookup", AdditionalProperties = new Dictionary<string, object?> { ["strict"] = true }
    })];
    var failure = await Assert.ThrowsAsync<ProviderOperationException>(() =>
        Read(new ProviderModelClient(http), State(route: ProviderRoute.ChatGpt), options));
    Assert.Equal(ProviderFailure.InvalidProviderResponse, failure.Failure);
    Assert.Empty(handler.Requests);
  }

  [Theory]
  [InlineData("")]
  [InlineData(",\"namespace\":\"other\"")]
  public async Task PlanNeverExecutesAnUnqualifiedOrWrongNamespace(string ns) {
    string call = Call.Replace("\"name\":\"lookup\"", "\"name\":\"lookup\"" + ns);
    await Rejected(Responses([Message, call]), ProviderFailure.InvalidProviderResponse, State(route: ProviderRoute.ChatGpt), Options(tools: true));
  }

  [Theory]
  [InlineData("response.failed", ProviderFailure.ProviderUnavailable)]
  [InlineData("response.incomplete", ProviderFailure.IncompleteResponse)]
  [InlineData(null, ProviderFailure.IncompleteResponse)]
  public async Task NativeResponsesFailureAndMissingTerminalNeverReleaseCalls(string? terminal, ProviderFailure failure) =>
      await Rejected(Responses([Message, Call], terminal), failure, options: Options(tools: true));

  [Theory]
  [InlineData("length", ProviderFailure.IncompleteResponse)]
  [InlineData("content_filter", ProviderFailure.IncompleteResponse)]
  [InlineData("stop", ProviderFailure.InvalidProviderResponse)]
  public async Task ChatRequiresToolFinishForActualCalls(string finish, ProviderFailure failure) =>
      await Rejected(ChatCalls(finish), failure, State(LlmApi.OpenAIChatCompletions), Options(tools: true));

  [Fact]
  public async Task DuplicateAndPostTerminalResponsesAreRejected() {
    string complete = Responses();
    await Rejected(complete + complete, ProviderFailure.InvalidProviderResponse);
    await Rejected(complete + Event("response.output_text.delta", new {
      type = "response.output_text.delta", sequence_number = 99, item_id = "msg_fixture", output_index = 0, content_index = 0, delta = "late"
    }), ProviderFailure.InvalidProviderResponse);
    await Rejected(Responses(terminalItems: "[]"), ProviderFailure.InvalidProviderResponse);
    await Rejected(Responses([Message.Replace("hello", "different")]), ProviderFailure.InvalidProviderResponse);
  }

  [Fact]
  public async Task ChatRejectsDuplicateNativeTerminalButAllowsUsageAfterFinish() {
    await Rejected(ChatStream(done: false) + ChatStream(), ProviderFailure.InvalidProviderResponse, State(LlmApi.OpenAIChatCompletions));
    using var handler = new ScriptedOpenAIHandler(ChatStream(usage: new { prompt_tokens = 2, completion_tokens = 1, total_tokens = 3 }));
    using var http = new HttpClient(handler);
    var updates = await Read(new ProviderModelClient(http), State(LlmApi.OpenAIChatCompletions));
    Assert.Single(updates.SelectMany(update => update.Contents).OfType<UsageContent>());
    Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);
  }

  [Fact]
  public async Task TerminalOnlyNativeOutputStillSuppliesCompleteCalls() {
    using var handler = new ScriptedOpenAIHandler(Responses([Message, Call], itemDone: false));
    using var http = new HttpClient(handler);
    var updates = await Read(new ProviderModelClient(http), State(), Options(tools: true));
    Assert.Single(updates[^1].Contents.OfType<FunctionCallContent>());
  }

  [Fact]
  public async Task InvalidOrUndeclaredCallsAndOversizedStateAreRejected() {
    await Rejected(ChatCalls(arguments: "not-json"), ProviderFailure.InvalidProviderResponse, State(LlmApi.OpenAIChatCompletions), Options(tools: true));
    await Rejected(Responses([Message, Call]), ProviderFailure.InvalidProviderResponse);
    await Rejected(Responses(text: new string('x', LlmProviderContext.MaximumBytes + 1)), ProviderFailure.InvalidProviderResponse);
  }

  [Fact]
  public async Task EmptyChatFunctionFragmentsStillConsumeTheRetainedStateBudget() {
    var calls = Enumerable.Range(0, LlmProviderContext.MaximumBytes / 256 + 1).Select(index => new {
      index, type = "function", function = new { arguments = "" }
    });
    string wire = "data: " + JsonSerializer.Serialize(new {
      id = "chatcmpl_fixture", @object = "chat.completion.chunk", created = 1700000000, model = "fixture-model",
      choices = new[] { new { index = 0, delta = new { tool_calls = calls }, finish_reason = (string?)null } }
    }) + "\n\n";
    await Rejected(wire, ProviderFailure.InvalidProviderResponse, State(LlmApi.OpenAIChatCompletions), Options(tools: true));
  }

  [Theory]
  [InlineData(HttpStatusCode.Unauthorized, ProviderFailure.AuthenticationRequired)]
  [InlineData(HttpStatusCode.Forbidden, ProviderFailure.PlanAccessDenied)]
  [InlineData(HttpStatusCode.InternalServerError, ProviderFailure.ProviderUnavailable)]
  public async Task ErrorsAreSecretSafeAndNeverRetried(HttpStatusCode status, ProviderFailure failure) {
    using var handler = new ScriptedOpenAIHandler("{\"error\":{\"message\":\"private-token\"}}", "application/json", status);
    using var http = new HttpClient(handler);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() => Read(new ProviderModelClient(http), State()));
    Assert.Equal(failure, error.Failure);
    Assert.Equal(failure.ToString(), error.Message);
    Assert.Null(error.InnerException);
    Assert.Single(handler.Requests);
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task UndefinedReasoningEffortFailsBeforeSending(LlmApi api) {
    using var handler = new ScriptedOpenAIHandler(Wire(api));
    using var http = new HttpClient(handler);
    var options = Options();
    options.Reasoning = new() { Effort = (ReasoningEffort)int.MaxValue };
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() => Read(new ProviderModelClient(http), State(api), options));
    Assert.Equal(ProviderFailure.InvalidProviderResponse, error.Failure);
    Assert.Empty(handler.Requests);
  }

  [Theory]
  [InlineData("future_visible_output", true)]
  [InlineData("future_visible_output", false)]
  [InlineData("additional_tools", true)]
  [InlineData("additional_tools", false)]
  public async Task UnknownOutputIsRejectedAtItemDoneOrTerminal(string type, bool itemDone) {
    string item = JsonSerializer.Serialize(new { type, id = "unknown_fixture", role = "developer", tools = Array.Empty<object>() });
    await Rejected(Responses([item], text: "", itemDone: itemDone), ProviderFailure.InvalidProviderResponse);
  }

  [Fact]
  public async Task UnsupportedOptionsFailBeforeSendingAndRawFactoryNeverRuns() {
    bool invoked = false;
    List<ChatOptions> invalid = [
      new(),
      new() { ModelId = "fixture-model", ConversationId = "hidden-history" },
      new() { ModelId = "fixture-model", AllowBackgroundResponses = true },
      new() { ModelId = "fixture-model", TopK = 3 },
      new() { ModelId = "fixture-model", RawRepresentationFactory = _ => { invoked = true; return new object(); } },
      new() { ModelId = "fixture-model", AdditionalProperties = new() { ["store"] = true } }
    ];
    using var handler = new ScriptedOpenAIHandler(Responses());
    using var http = new HttpClient(handler);
    foreach (var options in invalid)
      await Assert.ThrowsAsync<ProviderOperationException>(() => Read(new ProviderModelClient(http), State(), options));
    await Assert.ThrowsAsync<ProviderOperationException>(() => Read(new ProviderModelClient(http), State(route: ProviderRoute.ChatGpt), new() { ModelId = "fixture-model", Temperature = 1 }));
    Assert.False(invoked);
    Assert.Empty(handler.Requests);
  }

  private static async Task Rejected(string wire, ProviderFailure failure, ProviderState? state = null, ChatOptions? options = null) {
    using var handler = new ScriptedOpenAIHandler(wire);
    using var http = new HttpClient(handler);
    List<ChatResponseUpdate> observed = [];
    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var update in new ProviderModelClient(http).GetStreamingResponseAsync(state ?? State(), History(), options ?? Options(), default))
        observed.Add(update);
    });
    Assert.Equal(failure, error.Failure);
    Assert.DoesNotContain(observed.SelectMany(update => update.Contents), content => content is FunctionCallContent);
    Assert.All(observed, update => Assert.Null(update.FinishReason));
    Assert.All(observed, update => Assert.Null(LlmProviderContext.GetResponse(update)));
  }

  private static async Task<List<ChatResponseUpdate>> Read(ProviderModelClient client, ProviderState state, ChatOptions? options = null) {
    List<ChatResponseUpdate> updates = [];
    await foreach (var update in client.GetStreamingResponseAsync(state, History(), options ?? Options(), default)) updates.Add(update);
    return updates;
  }

  private static ChatOptions Options(bool tools = false) => new() {
    ModelId = "fixture-model",
    Tools = tools ? [AIFunctionFactory.Create((string key) => key, name: "lookup")] : null
  };
  private static ChatMessage[] History() => [new(ChatRole.User, "hello")];
  private static string Wire(LlmApi api) => api == LlmApi.OpenAIResponses ? Responses() : ChatStream();
  private static ProviderState State(LlmApi api = LlmApi.OpenAIResponses, ProviderRoute route = ProviderRoute.Custom) => new(
      new(Guid.NewGuid(), "Fixture", route, api, new Uri("https://fixture.invalid/prefix/v1"), AuthenticationMethod.None),
      7, 2, null, null, false, null);
}

#pragma warning restore MEAI001
