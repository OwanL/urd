using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

// Authorization, invocation claims and durable results use separate transactional gates.
public sealed class ToolStorageTests {
  [Fact]
  public void OnlyFinalSuccessfulCompletionAuthorizesDispatchAndDeliversSubmissions() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    var call = Call("first");
    var completion = new LlmRequestResult(request, ExecutionOutcome.Completed, ToolCalls: [call]);

    Assert.False(store.Tools.TryStart(session, execution, 0, request, call.CallId));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Guid output = Guid.NewGuid();
    Assert.True(store.LlmResponses.Append(session, execution, 0, new(request, output, 0, "working", false)));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.Empty(StoredTools.Read(database));
    Assert.False(store.Tools.Complete(session, execution, 0, request, Result(call.CallId)));
    Assert.True(store.LlmResponses.Append(session, execution, 0, new(request, output, 1, "", true)));
    Assert.False(store.Tools.TryStart(session, execution, 0, request, call.CallId));

    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    StoredTool saved = Assert.Single(StoredTools.Read(database));
    Assert.Equal(call.CallId, saved.CallId);
    Assert.Equal(call.Arguments.GetRawText(), saved.ArgumentsJson);
    Assert.False(saved.Started);
    Assert.Null(saved.Outcome);
    Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredDeliveries.Read(database)).Outcome);
    Assert.True(store.Tools.TryStart(session, execution, 0, request, call.CallId));
    Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
  }

  [Fact]
  public void StopBeforeCompletionNeverAuthorizesCalls() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    FinalText(store, session, execution, request);
    store.Executions.RequestStop(session, execution, ownsExecution: true);

    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0,
        new(request, ExecutionOutcome.Completed, ToolCalls: [Call("late")])));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, new(request, ExecutionOutcome.Cancelled)));
    Assert.False(store.Tools.TryStart(session, execution, 0, request, "late"));
    Assert.Empty(StoredTools.Read(database));
  }

  [Fact]
  public void AssistantIndexAndEmptyFinalChunkCommitTogetherOrNotAtAll() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    var chunk = new LlmResponseChunk(request, Guid.NewGuid(), 0, "", true);
    database.Execute("""
      CREATE TRIGGER reject_assistant BEFORE INSERT ON execution_messages WHEN NEW.role = 1
      BEGIN SELECT RAISE(ABORT, 'index unavailable'); END;
      """);

    Assert.Throws<SqliteException>(() => store.LlmResponses.Append(session, execution, 0, chunk));
    Assert.Empty(StoredLlmResponses.Read(database));
    database.Execute("DROP TRIGGER reject_assistant;");
    Assert.True(store.LlmResponses.Append(session, execution, 0, chunk));
    Assert.False(store.LlmResponses.Append(session, execution, 0, chunk));
    var assistant = Assert.Single(StoredDeliveries.ReadMessages(database), message => message.Role == ChatRole.Assistant);
    Assert.Equal(chunk.ResponseId, assistant.MessageId);
  }

  [Fact]
  public void AuthorizationCommitFailureRollsBackRequestDeliveryAndWholeBatch() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    FinalText(store, session, execution, request);
    var completion = new LlmRequestResult(request, ExecutionOutcome.Completed, ToolCalls: [Call("one"), Call("two")]);
    database.Execute("""
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_authorization AFTER INSERT ON execution_tools WHEN NEW.call_index = 1
      BEGIN INSERT INTO test_child VALUES (1); END;
      """);

    Assert.Throws<SqliteException>(() => store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.Null(Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
    Assert.Null(Assert.Single(StoredDeliveries.Read(database)).Outcome);
    Assert.Empty(StoredTools.Read(database));
    Assert.False(store.Tools.TryStart(session, execution, 0, request, "one"));
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;")); // raw-sql: test_child is a fixture-owned rollback witness
    database.Execute("DROP TRIGGER fail_authorization;");
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.Equal(2, StoredTools.Read(database).Count);
  }

  [Fact]
  public void InvalidBatchesCannotAuthorizeCallsButSixteenBoundedCallsCan() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    FinalText(store, session, execution, request);
    var completion = new LlmRequestResult(request, ExecutionOutcome.Completed);
    LlmToolCall call = Call("same");
    Assert.Throws<ArgumentException>(() => store.LlmResponses.CompleteRequest(session, execution, 0,
        completion with { ToolCalls = [call, call] }));
    Assert.Throws<ArgumentException>(() => store.LlmResponses.CompleteRequest(session, execution, 0,
        completion with { ToolCalls = Enumerable.Range(0, 17).Select(index => Call($"call-{index}")).ToArray() }));
    Assert.Throws<ArgumentException>(() => store.LlmResponses.CompleteRequest(session, execution, 0,
        completion with { ToolCalls = [call with { Name = " " }] }));
    Assert.Throws<ArgumentException>(() => store.LlmResponses.CompleteRequest(session, execution, 0,
        completion with { ToolCalls = [call with { Arguments = Json("[]") }] }));
    Assert.Throws<ArgumentException>(() => store.LlmResponses.CompleteRequest(session, execution, 0,
        completion with { ToolCalls = [call with { Arguments = default }] }));
    Assert.Throws<ArgumentException>(() => store.LlmResponses.CompleteRequest(session, execution, 0,
        completion with { Outcome = ExecutionOutcome.Failed, ToolCalls = [call] }));
    // Object punctuation occupies eight bytes; each unescaped é occupies two UTF-8 bytes.
    JsonElement bounded = Json("{\"x\":\"" + new string('é', 8188) + "\"}");
    JsonElement oversized = Json("{\"x\":\"" + new string('é', 8189) + "\"}");
    Assert.Throws<ArgumentException>(() => store.LlmResponses.CompleteRequest(session, execution, 0,
        completion with { ToolCalls = [call with { Arguments = oversized }] }));
    Assert.Empty(StoredTools.Read(database));
    Assert.Null(Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion with {
      ToolCalls = Enumerable.Range(0, 16).Select(index => Call($"call-{index}") with { Arguments = bounded }).ToArray()
    }));
    Assert.Equal(16, StoredTools.Read(database).Count);
  }

  [Fact]
  public void ClaimsAreSequentialCurrentAndNeverRepeatedAndErrorsPermitContinuation() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    Authorize(store, session, execution, request, Call("one"), Call("two"));
    Assert.False(store.Tools.TryStart(Guid.NewGuid(), execution, 0, request, "one"));
    Assert.False(store.Tools.TryStart(session, Guid.NewGuid(), 0, request, "one"));
    Assert.False(store.Tools.TryStart(session, execution, 1, request, "one"));
    Assert.False(store.Tools.TryStart(session, execution, 0, Guid.NewGuid(), "one"));
    Assert.False(store.Tools.TryStart(session, execution, 0, request, "unknown"));
    Assert.False(store.Tools.TryStart(session, execution, 0, request, "two"));
    Assert.False(store.Tools.Complete(session, execution, 0, request, Result("one")));
    Assert.True(store.Tools.TryStart(session, execution, 0, request, "one"));
    Assert.False(store.Tools.TryStart(session, execution, 0, request, "one"));
    Assert.False(store.Tools.TryStart(session, execution, 0, request, "two"));
    var error = new LlmToolResult("one", ExecutionOutcome.Failed, Json("{\"error\":\"unknown function\"}"), "unknown function");
    Assert.True(store.Tools.Complete(session, execution, 0, request, error));
    Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
    Assert.True(store.Tools.TryStart(session, execution, 0, request, "two"));
    Assert.True(store.Tools.Complete(session, execution, 0, request, Result("two")));
    Assert.NotNull(store.Executions.ClaimNextRequest(session, execution, 0));
    Assert.False(store.Tools.TryStart(session, execution, 0, request, "one"));
    Assert.True(store.Tools.Complete(session, execution, 0, request, error)); // Retained result is not replay.
    Assert.Equal(ExecutionOutcome.Failed, StoredTools.Read(database)[0].Outcome);
  }

  [Fact]
  public void FailedClaimCommitNeverGrantsInvocationPermission() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    Authorize(store, session, execution, request, Call("effect"));
    database.Execute("""
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_claim AFTER UPDATE OF started ON execution_tools
      BEGIN INSERT INTO test_child VALUES (1); END;
      """);

    int effects = 0;
    Assert.Throws<SqliteException>(() => {
      if (store.Tools.TryStart(session, execution, 0, request, "effect"))
        effects++;
    });
    Assert.Equal(0, effects);
    Assert.False(Assert.Single(StoredTools.Read(database)).Started);
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;")); // raw-sql: test_child is a fixture-owned rollback witness
    database.Execute("DROP TRIGGER fail_claim;");
    Assert.True(store.Tools.TryStart(session, execution, 0, request, "effect"));
  }

  [Fact]
  public void StopAllowsActualResultButPreventsNextStartAndRetainedRetriesNeverRewrite() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    LlmToolCall first = Call("one"), second = Call("two");
    Authorize(store, session, execution, request, first, second);
    var completion = new LlmRequestResult(request, ExecutionOutcome.Completed, ToolCalls: [first, second]);
    Assert.True(store.Tools.TryStart(session, execution, 0, request, "one"));
    store.Executions.RequestStop(session, execution, ownsExecution: true);
    Assert.True(store.Tools.Complete(session, execution, 0, request, Result("one")));
    Assert.False(store.Tools.TryStart(session, execution, 0, request, "two"));
    Assert.Equal(ExecutionOutcome.Cancelled, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
    database.Execute("""
      CREATE TRIGGER reject_result_rewrite BEFORE UPDATE ON execution_tools
      BEGIN SELECT RAISE(ABORT, 'retained result'); END;
      CREATE TRIGGER reject_request_rewrite BEFORE UPDATE ON execution_requests
      BEGIN SELECT RAISE(ABORT, 'retained completion'); END;
      CREATE TRIGGER reject_delivery_rewrite BEFORE UPDATE ON execution_inputs
      BEGIN SELECT RAISE(ABORT, 'retained delivery'); END;
      """);

    Assert.True(store.Tools.Complete(session, execution, 0, request, Result("one")));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.False(store.Tools.Complete(session, execution, 0, request, Result("one") with { Error = "different" }));
    Assert.False(store.Tools.Complete(session, execution, 0, request, Result("one") with { Outcome = ExecutionOutcome.Failed }));
    Assert.False(store.Tools.Complete(session, execution, 0, request, Result("one") with { Result = Json("false") }));
    Assert.False(store.Tools.Complete(session, execution, 0, request, Result("two")));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0, completion with { ToolCalls = [second, first] }));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0, completion with { ToolCalls = [first] }));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0,
        completion with { ToolCalls = [first with { Arguments = Json("{\"x\":1}") }, second] }));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0,
        completion with { ToolCalls = [first with { Name = "renamed" }, second] }));
  }

  [Fact]
  public void ResultIdentityAndRevisionFencesAlsoApplyToRetainedWrites() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    Authorize(store, session, execution, request, Call("one"));
    Assert.True(store.Tools.TryStart(session, execution, 0, request, "one"));
    Assert.False(store.Tools.Complete(Guid.NewGuid(), execution, 0, request, Result("one")));
    Assert.False(store.Tools.Complete(session, Guid.NewGuid(), 0, request, Result("one")));
    Assert.False(store.Tools.Complete(session, execution, 1, request, Result("one")));
    Assert.False(store.Tools.Complete(session, execution, 0, Guid.NewGuid(), Result("one")));
    Assert.False(store.Tools.Complete(session, execution, 0, request, Result("unknown")));
    Assert.True(store.Tools.Complete(session, execution, 0, request, Result("one")));
    database.ForceHistoryRevision(1);
    Assert.False(store.Tools.Complete(session, execution, 0, request, Result("one")));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0,
        new(request, ExecutionOutcome.Completed, ToolCalls: [Call("one")])));
  }

  [Fact]
  public void AfterEffectSaveFailureRetainsClaimWithoutResultContinuationOrRestart() {
    using var database = new TemporaryDatabase();
    Guid session, execution, request;
    int effects = 0;
    using (SessionStore store = SessionStore.Open(database.Path)) {
      (session, execution, request) = Begin(store);
      Authorize(store, session, execution, request, Call("effect"));
      if (store.Tools.TryStart(session, execution, 0, request, "effect"))
        effects++; // Simulated invocation effect, strictly after the committed claim.
      Assert.Equal(1, effects);
      database.Execute("""
        CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
        CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
        CREATE TRIGGER fail_result AFTER UPDATE OF result_json ON execution_tools
        BEGIN INSERT INTO test_child VALUES (1); END;
        """);
      Assert.Throws<SqliteException>(() => store.Tools.Complete(session, execution, 0, request, Result("effect")));
      StoredTool retained = Assert.Single(StoredTools.Read(database));
      Assert.True(retained.Started);
      Assert.Null(retained.ResultJson);
      Assert.Null(retained.Outcome);
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;")); // raw-sql: test_child is a fixture-owned rollback witness
      if (store.Tools.TryStart(session, execution, 0, request, "effect"))
        effects++;
      Assert.Equal(1, effects);
      Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
      Assert.Null(store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed));
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    if (reopened.Tools.TryStart(session, execution, 0, request, "effect"))
      effects++;
    Assert.Equal(1, effects);
    Assert.Null(reopened.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Null(reopened.Executions.ReadSettlement(session, execution));
  }

  [Fact]
  public void ResultsRequireValidBoundedUtf8JsonAndRetainTheActualPayload() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    var (session, execution, request) = Begin(store);
    Authorize(store, session, execution, request, Call("one"));
    Assert.True(store.Tools.TryStart(session, execution, 0, request, "one"));
    var result = Result("one");
    Assert.Throws<ArgumentException>(() => store.Tools.Complete(session, execution, 0, request, result with { Result = default }));
    Assert.Throws<ArgumentOutOfRangeException>(() => store.Tools.Complete(session, execution, 0, request,
        result with { Outcome = (ExecutionOutcome)99 }));
    Assert.Throws<ArgumentException>(() => store.Tools.Complete(session, execution, 0, request,
        result with { Result = Json("\"" + new string('é', 8192) + "\"") }));
    JsonElement bounded = Json("\"" + new string('é', 8191) + "\"");
    Assert.True(store.Tools.Complete(session, execution, 0, request, result with { Result = bounded }));
    Assert.Equal(bounded.GetRawText(), Assert.Single(StoredTools.Read(database)).ResultJson);
  }

  private static (Guid Session, Guid Execution, Guid Request) Begin(SessionStore store) {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "request"), true, execution);
    return (session, execution, store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId);
  }

  private static void FinalText(SessionStore store, Guid session, Guid execution, Guid request) =>
      Assert.True(store.LlmResponses.Append(session, execution, 0, new(request, Guid.NewGuid(), 0, "", true)));

  private static void Authorize(SessionStore store, Guid session, Guid execution, Guid request, params LlmToolCall[] calls) {
    FinalText(store, session, execution, request);
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, new(request, ExecutionOutcome.Completed, ToolCalls: calls)));
  }

  private static LlmToolCall Call(string id) => new(id, "fake", Json("{}"));
  private static LlmToolResult Result(string id) => new(id, ExecutionOutcome.Completed, Json("{\"saved\":true}"));

  private static JsonElement Json(string text) {
    using JsonDocument document = JsonDocument.Parse(text);
    return document.RootElement.Clone();
  }
}
