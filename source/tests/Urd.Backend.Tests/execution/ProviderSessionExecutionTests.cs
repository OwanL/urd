using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

#pragma warning disable MEAI001

namespace Urd.Backend.Tests.Execution;

public sealed class ProviderSessionExecutionTests {
  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task ManagedToolLoopPersistsNativeContextAndReplaysItAfterReopening(LlmApi api) {
    using var database = new TemporaryDatabase();
    using var first = new ScriptedOpenAIHandler(ToolResponse(api));
    using var done = new ScriptedOpenAIHandler(api == LlmApi.OpenAIResponses
        ? ProviderModelStreams.Responses([ProviderModelStreams.Message.Replace("hello", "done")], text: "done")
            .Replace("msg_fixture", "msg_done").Replace("resp_fixture", "resp_done")
        : OpenAIStreams.ChatStream().Replace("hello", "done").Replace("chatcmpl_fixture", "chatcmpl_done"));
    using var firstTransport = new HttpMessageInvoker(first, disposeHandler: false);
    using var doneTransport = new HttpMessageInvoker(done, disposeHandler: false);
    int providerCalls = 0, effects = 0;
    AIFunction lookup = AIFunctionFactory.Create((string key, CancellationToken token) => {
      Assert.Equal("abc", key);
      Assert.False(token.IsCancellationRequested);
      Assert.Equal(1, first.StreamDisposals);
      StoredRequest request = Assert.Single(StoredExecutions.ReadRequests(database));
      Assert.Equal(ExecutionOutcome.Completed, request.Outcome);
      var context = Assert.Single(ReadProviderResponses(database));
      Assert.Equal(request.RequestId, context.Key);
      Assert.Equal(api, context.Value.Scope.Api);
      Assert.Equal("fixture-model", context.Value.Scope.ModelId);
      ChatMessage restored = LlmProviderContext.Restore(context.Value);
      Assert.Equal("hello", restored.Text);
      Assert.Single(restored.Contents.OfType<TextReasoningContent>());
      Assert.Equal("call_fixture", Assert.Single(restored.Contents.OfType<FunctionCallContent>()).CallId);
      StoredTool tool = Assert.Single(StoredTools.Read(database));
      Assert.Equal(request.RequestId, tool.RequestId);
      Assert.True(tool.Started);
      Assert.Null(tool.ResultJson);
      Interlocked.Increment(ref effects);
      return new { key, answer = "saved \"value\"", entries = new[] { 1, 2 } };
    }, name: "lookup");
    var initialHandler = new ControlledProviderHandler(Respond);
    Guid sessionId, connectionId;
    string sentResult, storedResult;
    Dictionary<Guid, LlmProviderResponse> initialContexts;
    await using (var fixture = new ManagedSession(database, initialHandler)) {
      await fixture.OpenAsync(api, [lookup]);
      sessionId = fixture.Session.Id;
      connectionId = fixture.Connection.Id;
      await fixture.SendAsync("go");

      Assert.Equal(2, providerCalls);
      Assert.Equal(1, effects);
      Assert.Equal(2, initialHandler.Requests.Count);
      Assert.All(initialHandler.Requests, request => Assert.Null(request.Authorization));
      StoredTool tool = Assert.Single(StoredTools.Read(database));
      Assert.Equal(ExecutionOutcome.Completed, tool.Outcome);
      storedResult = Assert.IsType<string>(tool.ResultJson);
      sentResult = AssertNativeReplay(api, initialHandler.Requests[1], storedResult, followup: false);
      initialContexts = AssertCompletedContexts(database, 2, connectionId, api);
      Assert.Contains(tool.RequestId, initialContexts.Keys);
      Assert.Equal(1, first.StreamDisposals);
      Assert.Equal(1, done.StreamDisposals);
      await AssertPublicTextAsync(fixture.Session, "hello", "done");
    }
    Assert.Equal(1, initialHandler.DisposeCalls);

    var reopenedHandler = new ControlledProviderHandler(Respond);
    await using (var reopened = new ManagedSession(database, reopenedHandler)) {
      await reopened.OpenAsync(api, [lookup], sessionId, connectionId);
      Assert.Equal(sessionId, reopened.Session.Id);
      await AssertPublicTextAsync(reopened.Session, "hello", "done");
      await reopened.SendAsync("followup");

      Assert.Equal(3, providerCalls);
      Assert.Equal(1, effects);
      ScriptedOpenAIRequest third = Assert.Single(reopenedHandler.Requests);
      Assert.Null(third.Authorization);
      Assert.Equal(sentResult, AssertNativeReplay(api, third, storedResult, followup: true));
      Assert.Equal(storedResult, Assert.Single(StoredTools.Read(database)).ResultJson);
      Dictionary<Guid, LlmProviderResponse> contexts = AssertCompletedContexts(database, 3, connectionId, api);
      foreach (var context in initialContexts)
        Assert.Equal(context.Value, contexts[context.Key]);
      Assert.Equal(2, StoredExecutions.Read(database).Count);
      Assert.All(StoredExecutions.Read(database), execution => Assert.Equal(ExecutionOutcome.Completed, execution.Outcome));
      Assert.Equal(2, done.StreamDisposals);
      await AssertPublicTextAsync(reopened.Session, "hello", "done", "done");
    }
    Assert.Equal(1, reopenedHandler.DisposeCalls);
    Assert.Single(first.Requests);
    Assert.Equal(2, done.Requests.Count);

    Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token) =>
        Interlocked.Increment(ref providerCalls) switch {
          1 => firstTransport.SendAsync(request, token),
          2 or 3 => doneTransport.SendAsync(request, token),
          _ => throw new InvalidOperationException("Unexpected provider request.")
        };
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task TransportFailureAfterNativeToolFinishWithholdsEffectsAndContext(LlmApi api) {
    using var database = new TemporaryDatabase();
    // Chat's [DONE] would let the SDK stop without observing the failing stream tail.
    var handler = new ScriptedOpenAIHandler(ToolResponse(api).Replace("data: [DONE]\n\n", "")) { FailAtEnd = true };
    int effects = 0;
    AIFunction lookup = AIFunctionFactory.Create((string key) => Interlocked.Increment(ref effects), name: "lookup");
    await using var fixture = new ManagedSession(database, handler);
    await fixture.OpenAsync(api, [lookup]);
    await fixture.SendAsync("go", ExecutionOutcome.Failed);

    Assert.Equal(0, effects);
    Assert.Single(handler.Requests);
    Assert.Equal(1, handler.StreamDisposals);
    Assert.Empty(StoredTools.Read(database));
    Assert.Empty(ReadProviderResponses(database));
    Assert.Equal(ExecutionOutcome.Failed, Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
    Assert.All(StoredLlmResponses.Read(database), chunk => Assert.False(chunk.Final));
    Assert.False((await fixture.Session.ReadSnapshotAsync()).AdmissionFenced);
  }

  private static string ToolResponse(LlmApi api) => api == LlmApi.OpenAIResponses
      ? ProviderModelStreams.Responses([ProviderModelStreams.Message,
          ProviderModelStreams.Reasoning.Replace("\"summary\":[]", "\"summary\":[{\"type\":\"summary_text\",\"text\":\"thought\"}]"),
          ProviderModelStreams.Call])
      : ProviderModelStreams.ChatCalls(reasoning: true);

  // Inspect the actual SDK request, not a reconstructed context or another serialization pass.
  private static string AssertNativeReplay(LlmApi api, ScriptedOpenAIRequest request, string storedResult, bool followup) {
    using JsonDocument document = JsonDocument.Parse(request.Body);
    JsonElement root = document.RootElement;
    Assert.Equal("fixture-model", root.GetProperty("model").GetString());
    Assert.True(root.GetProperty("stream").GetBoolean());
    Assert.False(root.GetProperty("store").GetBoolean());
    Assert.False(root.TryGetProperty("previous_response_id", out _));
    Assert.False(root.TryGetProperty("conversation", out _));
    JsonElement declaredTool = Assert.Single(root.GetProperty("tools").EnumerateArray());
    Assert.Equal("lookup", (api == LlmApi.OpenAIResponses ? declaredTool : declaredTool.GetProperty("function"))
        .GetProperty("name").GetString());
    JsonElement result;
    if (api == LlmApi.OpenAIResponses) {
      Assert.EndsWith("/responses", request.Uri.AbsolutePath);
      JsonElement[] input = root.GetProperty("input").EnumerateArray().ToArray();
      Assert.Equal(followup ? 7 : 5, input.Length);
      Assert.Equal("msg_fixture", input[1].GetProperty("id").GetString());
      Assert.Equal("hello", WireText(input[1].GetProperty("content")));
      Assert.Equal("reasoning", input[2].GetProperty("type").GetString());
      Assert.Equal("rs_fixture", input[2].GetProperty("id").GetString());
      Assert.Equal("private-reasoning", input[2].GetProperty("encrypted_content").GetString());
      Assert.Equal("thought", Assert.Single(input[2].GetProperty("summary").EnumerateArray()).GetProperty("text").GetString());
      Assert.Equal("function_call", input[3].GetProperty("type").GetString());
      Assert.Equal("fc_fixture", input[3].GetProperty("id").GetString());
      Assert.Equal("call_fixture", input[3].GetProperty("call_id").GetString());
      AssertCall(input[3]);
      result = input[4];
      Assert.Equal("function_call_output", result.GetProperty("type").GetString());
      Assert.Equal("call_fixture", result.GetProperty("call_id").GetString());
      AssertResult(storedResult, result.GetProperty("output").GetString()!);
    } else {
      Assert.EndsWith("/chat/completions", request.Uri.AbsolutePath);
      JsonElement[] messages = root.GetProperty("messages").EnumerateArray().ToArray();
      Assert.Equal(followup ? 5 : 3, messages.Length);
      JsonElement assistant = messages[1];
      Assert.Equal("assistant", assistant.GetProperty("role").GetString());
      Assert.Equal("hello", WireText(assistant.GetProperty("content")));
      Assert.Equal("thought", assistant.GetProperty("reasoning_content").GetString());
      JsonElement call = Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray());
      Assert.Equal("call_fixture", call.GetProperty("id").GetString());
      Assert.Equal("function", call.GetProperty("type").GetString());
      AssertCall(call.GetProperty("function"));
      result = messages[2];
      Assert.Equal("tool", result.GetProperty("role").GetString());
      Assert.Equal("call_fixture", result.GetProperty("tool_call_id").GetString());
      AssertResult(storedResult, result.GetProperty("content").GetString()!);
    }
    return result.GetRawText();
  }

  private static void AssertResult(string stored, string sent) {
    using JsonDocument saved = JsonDocument.Parse(stored);
    using JsonDocument wire = JsonDocument.Parse(sent);
    Assert.True(JsonElement.DeepEquals(saved.RootElement, wire.RootElement));
  }

  private static void AssertCall(JsonElement function) {
    Assert.Equal("lookup", function.GetProperty("name").GetString());
    Assert.Equal("{\"key\":\"abc\"}", function.GetProperty("arguments").GetString());
  }

  private static string WireText(JsonElement content) => content.ValueKind == JsonValueKind.String
      ? content.GetString()! : string.Concat(content.EnumerateArray().Select(part => part.GetProperty("text").GetString()));

  private static async Task AssertPublicTextAsync(PrimarySession session, params string[] expected) {
    SessionSnapshot snapshot = await session.ReadSnapshotAsync();
    Assert.False(snapshot.AdmissionFenced);
    Assert.Null(snapshot.UnsettledExecutionId);
    Assert.Equal(expected, snapshot.Messages.Where(message => message.Message.Role == MessageRole.Assistant)
        .Select(message => message.Preview));
    Assert.All(snapshot.Messages, message => {
      Assert.DoesNotContain("thought", message.Preview);
      Assert.DoesNotContain("private-reasoning", message.Preview);
    });
  }

  private static Dictionary<Guid, LlmProviderResponse> AssertCompletedContexts(
      TemporaryDatabase database, int count, Guid connectionId, LlmApi api) {
    List<StoredRequest> requests = StoredExecutions.ReadRequests(database);
    Dictionary<Guid, LlmProviderResponse> contexts = ReadProviderResponses(database);
    Assert.Equal(count, requests.Count);
    Assert.Equal(count, contexts.Count);
    Assert.All(requests, request => {
      Assert.Equal(ExecutionOutcome.Completed, request.Outcome);
      Assert.True(contexts.ContainsKey(request.RequestId));
      LlmProviderScope scope = contexts[request.RequestId].Scope;
      Assert.Equal(connectionId, scope.ConnectionId);
      Assert.Equal(api, scope.Api);
      Assert.Equal("fixture-model", scope.ModelId);
    });
    return contexts;
  }

  private static Dictionary<Guid, LlmProviderResponse> ReadProviderResponses(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT request_id, response_json FROM execution_provider_context;";
    using SqliteDataReader reader = command.ExecuteReader();
    var responses = new Dictionary<Guid, LlmProviderResponse>();
    while (reader.Read())
      responses.Add(Guid.Parse(reader.GetString(0)), JsonSerializer.Deserialize<LlmProviderResponse>(reader.GetString(1))!);
    return responses;
  }

  // Own the real bridge and execution together so even assertion failures join work before SQLite closes.
  private sealed class ManagedSession : IAsyncDisposable {
    private readonly UrdStore store;
    private readonly ProviderManager manager;
    private IChatClient? client;
    private PrimarySession? session;

    internal ManagedSession(TemporaryDatabase database, HttpMessageHandler handler) {
      store = UrdStore.Open(database.Path);
      manager = new(store, () => new Uri("http://127.0.0.1:1234/auth/callback"), handler);
    }

    internal PrimarySession Session => session!;
    internal ProviderConnection Connection { get; private set; } = null!;

    internal async Task OpenAsync(LlmApi api, IReadOnlyList<AIFunction> tools,
        Guid? sessionId = null, Guid? connectionId = null) {
      Connection = connectionId is { } savedConnection
          ? await manager.GetAsync(savedConnection)
          : await manager.AddCustomAsync(new("managed execution", api, new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.None));
      client = manager.CreateChatClient(Connection);
      SessionRecord record = sessionId is { } savedSession
          ? Assert.IsType<SessionRecord>(store.Sessions.Read(savedSession))
          : store.Sessions.CreatePrimary(Guid.NewGuid());
      session = new(record, store, _ => client, tools);
    }

    internal async Task SendAsync(string text, ExecutionOutcome outcome = ExecutionOutcome.Completed) {
      UserMessageReceipt receipt = await Session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, text, TestLlmSelection.Create(Connection)));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
      Assert.Equal(outcome, (await Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    }

    public async ValueTask DisposeAsync() {
      if (session is not null) {
        await session.FenceUserMessagesAsync();
        await session.StopExecutionAsync().WaitAsync(TestTimeout);
      }
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      client?.Dispose();
      manager.Dispose();
      store.Dispose();
    }
  }
}

#pragma warning restore MEAI001
