using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class LlmProviderResponseStorageTests {
  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public void EnvelopeSurvivesReopenAndReplacesDisplayTextInContext(LlmApi api) {
    using var database = new TemporaryDatabase();
    LlmProviderResponse response = Response(api);
    Guid session, execution, request;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      (session, execution, request) = Begin(store);
      FinalText(store, session, execution, request);
      Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
          new(request, ExecutionOutcome.Completed, ProviderResponse: response)));
      Assert.Equal(response, Assert.Single(ReadProviderResponses(database)));
      Assert.Equal(ExecutionOutcome.Completed, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Null(reopened.Executions.ClaimNextRequest(session, execution, 0));
    Guid nextExecution = Guid.NewGuid();
    reopened.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "next input"), true, nextExecution);
    LlmRequest next = Assert.IsType<LlmRequest>(reopened.Executions.ClaimNextRequest(session, nextExecution, 0));
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User }, next.Messages.Select(message => message.Role));
    Assert.Equal(new[] { "request", "native answer", "next input" }, next.Messages.Select(message => message.Text));
    ChatMessage assistant = next.Messages[1];
    Assert.Equal(response, Assert.IsType<LlmProviderResponse>(assistant.RawRepresentation));
    TextReasoningContent reasoning = Assert.Single(assistant.Contents.OfType<TextReasoningContent>());
    Assert.Equal("private reasoning", reasoning.Text);
    Assert.Equal(api == LlmApi.OpenAIResponses ? "encrypted-state" : null, reasoning.ProtectedData);
    Assert.Equal(response, Assert.Single(ReadProviderResponses(database)));
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void RetainedRetriesRequireTheExactEnvelopeIncludingAbsence(bool withEnvelope) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    FinalText(store, session, execution, request);
    LlmProviderResponse response = Response();
    var completion = new LlmRequestResult(request, ExecutionOutcome.Completed,
        ProviderResponse: withEnvelope ? response : null);
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.Equal(ExecutionOutcome.Completed, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
    database.Execute("""
      CREATE TRIGGER reject_request_rewrite BEFORE UPDATE ON execution_requests
      BEGIN SELECT RAISE(ABORT, 'retained completion'); END;
      CREATE TRIGGER reject_context_insert BEFORE INSERT ON execution_provider_context
      BEGIN SELECT RAISE(ABORT, 'retained context'); END;
      CREATE TRIGGER reject_context_update BEFORE UPDATE ON execution_provider_context
      BEGIN SELECT RAISE(ABORT, 'retained context'); END;
      CREATE TRIGGER reject_context_delete BEFORE DELETE ON execution_provider_context
      BEGIN SELECT RAISE(ABORT, 'retained context'); END;
      """);

    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0,
        completion with { ProviderResponse = withEnvelope ? null : response }));
    if (withEnvelope) {
      LlmProviderResponse[] changed = [
          response with { Json = response.Json + " " },
          response with { Scope = response.Scope with { ConnectionId = Guid.NewGuid() } },
          response with { Scope = response.Scope with { Generation = response.Scope.Generation + 1 } },
          response with { Scope = response.Scope with { ModelId = "other-model" } },
          Response(LlmApi.OpenAIChatCompletions) with { Scope = response.Scope with { Api = LlmApi.OpenAIChatCompletions } }
      ];
      foreach (LlmProviderResponse different in changed)
        Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0, completion with { ProviderResponse = different }));
      Assert.Equal(response, Assert.Single(ReadProviderResponses(database)));
    } else {
      Assert.Empty(ReadProviderResponses(database));
    }
    Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
  }

  [Theory]
  [InlineData(ExecutionOutcome.Failed)]
  [InlineData(ExecutionOutcome.Cancelled)]
  public void UnsuccessfulRequestsCannotRetainProviderContext(ExecutionOutcome outcome) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    FinalText(store, session, execution, request);
    var completion = new LlmRequestResult(request, outcome, ProviderResponse: Response());

    Assert.Throws<ArgumentException>(() => store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.Null(Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
    Assert.Null(Assert.Single(StoredDeliveries.Read(database)).Outcome);
    Assert.Empty(ReadProviderResponses(database));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion with { ProviderResponse = null }));
  }

  [Fact]
  public void CodecValidationAndSizeLimitApplyBeforeCompletionIsSaved() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    FinalText(store, session, execution, request);
    LlmProviderResponse response = Response();
    LlmProviderResponse[] invalid = [
        response with { Json = "not json" },
        response with { Json = "{\"version\":2,\"items\":[]}" },
        response with { Scope = response.Scope with { ConnectionId = Guid.Empty } },
        response with { Scope = response.Scope with { Generation = -1 } },
        response with { Json = new string('x', LlmProviderContext.MaximumBytes + 1) }
    ];
    foreach (LlmProviderResponse envelope in invalid) {
      Assert.Throws<InvalidDataException>(() => store.LlmResponses.CompleteRequest(session, execution, 0,
          new(request, ExecutionOutcome.Completed, ProviderResponse: envelope)));
    }
    Assert.Null(Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
    Assert.Null(Assert.Single(StoredDeliveries.Read(database)).Outcome);
    Assert.Empty(ReadProviderResponses(database));
    Assert.Empty(StoredTools.Read(database));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
        new(request, ExecutionOutcome.Completed, ProviderResponse: response)));
  }

  [Theory]
  [InlineData("execution_provider_context", "NEW.request_id IS NOT NULL")]
  [InlineData("execution_tools", "NEW.call_index = 1")]
  public void CommitFailureRollsBackContextCompletionDeliveryAndToolAuthorization(string table, string condition) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    FinalText(store, session, execution, request);
    var completion = new LlmRequestResult(request, ExecutionOutcome.Completed,
        ToolCalls: [Call("one"), Call("two")], ProviderResponse: Response());
    database.Execute($"""
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_completion AFTER INSERT ON {table} WHEN {condition}
      BEGIN INSERT INTO test_child VALUES (1); END;
      """);

    Assert.Throws<SqliteException>(() => store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.Null(Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
    Assert.Null(Assert.Single(StoredDeliveries.Read(database)).Outcome);
    Assert.Empty(ReadProviderResponses(database));
    Assert.Empty(StoredTools.Read(database));
    Assert.False(store.Tools.TryStart(session, execution, 0, request, "one"));
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;")); // raw-sql: fixture-owned deferred-commit rollback witness
    database.Execute("DROP TRIGGER fail_completion;");
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.Equal(completion.ProviderResponse, Assert.Single(ReadProviderResponses(database)));
    Assert.Equal(2, StoredTools.Read(database).Count);
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void NativeAndGenericResponsesKeepToolResultsBeforeClaimedSteering(bool native) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    FinalText(store, session, execution, request);
    LlmProviderResponse? response = native ? Response(toolCall: true) : null;
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
        new(request, ExecutionOutcome.Completed, ToolCalls: [Call("call-one")], ProviderResponse: response)));
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "steer one"), true, Guid.NewGuid(), execution);
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "steer two"), true, Guid.NewGuid(), execution);
    Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
    Assert.True(store.Tools.TryStart(session, execution, 0, request, "call-one"));
    Assert.True(store.Tools.Complete(session, execution, 0, request,
        new("call-one", ExecutionOutcome.Completed, JsonSerializer.SerializeToElement(new { saved = true }))));
    Assert.False(store.Tools.TryStart(session, execution, 0, request, "call-one"));

    LlmRequest next = Assert.IsType<LlmRequest>(store.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.User },
        next.Messages.Select(message => message.Role));
    ChatMessage assistant = next.Messages[1];
    Assert.Equal(native ? "native answer" : "display answer", assistant.Text);
    Assert.Equal(response, assistant.RawRepresentation);
    Assert.Equal("call-one", Assert.Single(assistant.Contents.OfType<FunctionCallContent>()).CallId);
    FunctionResultContent result = Assert.Single(next.Messages[2].Contents.OfType<FunctionResultContent>());
    Assert.Equal("call-one", result.CallId);
    Assert.True(Assert.IsType<JsonElement>(result.Result).GetProperty("saved").GetBoolean());
    Assert.Equal(new[] { "steer one", "steer two" }, next.Messages.Skip(3).Select(message => message.Text));
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "later steering"), true, Guid.NewGuid(), execution);
    FinalText(store, session, execution, next.RequestId);
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, new(next.RequestId, ExecutionOutcome.Completed)));

    LlmRequest third = Assert.IsType<LlmRequest>(store.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.User,
        ChatRole.Assistant, ChatRole.User }, third.Messages.Select(message => message.Role));
    Assert.Equal(response, third.Messages[1].RawRepresentation);
    Assert.Null(third.Messages[5].RawRepresentation);
    Assert.Equal("display answer", third.Messages[5].Text);
    Assert.Equal("later steering", third.Messages[6].Text);
  }

  [Fact]
  public void StopStillRejectsNativeCompletionAndDoesNotCreateContext() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    FinalText(store, session, execution, request);
    store.Executions.RequestStop(session, execution, ownsExecution: true);

    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0,
        new(request, ExecutionOutcome.Completed, ProviderResponse: Response())));
    Assert.Empty(ReadProviderResponses(database));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, new(request, ExecutionOutcome.Cancelled)));
    Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Equal(ExecutionOutcome.Cancelled, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
  }

  private static (Guid Session, Guid Execution, Guid Request) Begin(UrdStore store) {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "request"), true, execution);
    return (session, execution, store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId);
  }

  private static void FinalText(UrdStore store, Guid session, Guid execution, Guid request) =>
      Assert.True(store.LlmResponses.Append(session, execution, 0, new(request, Guid.NewGuid(), 0, "display answer", true)));

  private static LlmToolCall Call(string id) => new(id, "fake", JsonSerializer.SerializeToElement(new { }));

  private static LlmProviderResponse Response(LlmApi api = LlmApi.OpenAIResponses, bool toolCall = false) {
    string json;
    if (api == LlmApi.OpenAIResponses) {
      json = """
          {"version":1,"items":[
            {"type":"reasoning","id":"rs_native","summary":[{"type":"summary_text","text":"private reasoning"}],"encrypted_content":"encrypted-state"},
            {"type":"message","id":"msg_native","role":"assistant","content":[{"type":"output_text","text":"native answer"}]}
          """ + (toolCall ? ",{\"type\":\"function_call\",\"call_id\":\"call-one\",\"name\":\"fake\",\"arguments\":\"{}\"}" : "") + "]}";
    } else {
      json = """
          {"version":1,"message":{"role":"assistant","content":"native answer","reasoning_content":"private reasoning"
          """ + (toolCall ? ",\"tool_calls\":[{\"type\":\"function\",\"id\":\"call-one\",\"function\":{\"name\":\"fake\",\"arguments\":\"{}\"}}]" : "") + "}}";
    }
    return new(new(Guid.NewGuid(), 3, api, "test-model"), json);
  }

  private static List<LlmProviderResponse> ReadProviderResponses(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT response_json FROM execution_provider_context ORDER BY request_id;";
    using SqliteDataReader reader = command.ExecuteReader();
    var responses = new List<LlmProviderResponse>();
    while (reader.Read())
      responses.Add(JsonSerializer.Deserialize<LlmProviderResponse>(reader.GetString(0))!);
    return responses;
  }
}
