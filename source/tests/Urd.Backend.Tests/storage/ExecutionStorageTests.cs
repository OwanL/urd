using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

// Transactional run and output gates: commit rollback, revision fences, retries and settlement.
public sealed class ExecutionStorageTests {
  [Fact]
  public void AcceptanceAndRunRollbackTogetherOnCommitFailure() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var input = new InputCommand(Guid.NewGuid(), 0, "atomic input");
    Guid run = Guid.NewGuid();
    database.Execute("""
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_run AFTER INSERT ON execution_runs
      BEGIN
          INSERT INTO test_child VALUES (1);
      END;
      """);
    Assert.Throws<SqliteException>(() => store.Inputs.Admit(session, 0, input, true, run));
    Assert.Null(store.Inputs.ReadReceipt(session, input.InputId));
    Assert.Empty(StoredInputs.Read(database));
    Assert.Empty(StoredRuns.Read(database));
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;")); // raw-sql: test_child is a fixture-owned rollback witness

    database.Execute("DROP TRIGGER fail_run;");
    var admitted = store.Inputs.Admit(session, 0, input, true, run);
    Assert.True(admitted.NewlyAccepted);
    Assert.Equal(run, admitted.Receipt.RunId);
    StoredRun stored = Assert.Single(StoredRuns.Read(database));
    Assert.Equal(run, stored.RunId);
    Assert.Equal(session, stored.SessionId);
    Assert.Equal(input.InputId, stored.InputId);
  }

  [Fact]
  public void RetainedAcceptanceResolvesBeforeAllCurrentGatesAndCannotCreateAnotherRun() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), run = Guid.NewGuid();
    var input = new InputCommand(Guid.NewGuid(), 0, "original \0 🙂");
    InputReceipt receipt;
    using (SessionStore store = SessionStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      receipt = store.Inputs.Admit(session, 0, input, true, run).Receipt;
      Assert.Equal(ExecutionOutcome.Failed, store.Runs.TrySettle(session, run, ExecutionOutcome.Failed)!.Outcome);
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    // Settlement alone must prevent a claim: no Stop, revision mismatch or prior request can mask it.
    Assert.Null(reopened.Runs.ClaimNextRequest(session, run, 0));
    database.ForceHistoryRevision(7);
    var retry = reopened.Inputs.Admit(session, 7, input, false, Guid.Empty);
    Assert.Equal(receipt, retry.Receipt);
    Assert.False(retry.NewlyAccepted);
    Assert.Throws<InputConflictException>(() => reopened.Inputs.Admit(session, 7, input with { Text = "changed" }, false, Guid.NewGuid()));
    Assert.Throws<InputConflictException>(() => reopened.Inputs.Admit(session, 7, input with { ExpectedHistoryRevision = 7 }, false, Guid.NewGuid()));
    Assert.Single(StoredRuns.Read(database));
  }

  [Fact]
  public void BusyAndStoppingRefusalsAreDurableWithoutTranscriptOrRunFacts() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "accepted"), true, run);
    var busyInput = new InputCommand(Guid.NewGuid(), 0, "refused while busy");
    var busy = store.Inputs.Admit(session, 0, busyInput, true, Guid.NewGuid());
    Assert.Equal(InputRefusalReason.Capacity, busy.Receipt.RefusalReason);
    Assert.False(busy.NewlyAccepted);
    Assert.Null(busy.Receipt.RunId);
    store.Runs.RequestStop(session, run, ownsRun: true);
    var stopped = store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "refused while stopping"), true, Guid.NewGuid());
    Assert.Equal(InputRefusalReason.Stopping, stopped.Receipt.RefusalReason);
    store.Runs.TrySettle(session, run, ExecutionOutcome.Cancelled);
    Assert.Equal(busy.Receipt, store.Inputs.Admit(session, 0, busyInput, true, Guid.NewGuid()).Receipt);
    Guid nextRun = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "next accepted"), true, nextRun);
    LlmRequest next = store.Runs.ClaimNextRequest(session, nextRun, 0)!;
    Assert.Equal(new[] { "next accepted" }, next.Messages.Select(message => message.Text));
    Assert.Equal(2, StoredRuns.Read(database).Count);
    Assert.Equal(4, StoredInputs.Read(database).Count);
  }

  [Theory]
  [InlineData(false, false, 0, 1, InputRefusalReason.Stopping)]
  [InlineData(true, true, 0, 1, InputRefusalReason.Stopping)]
  [InlineData(true, false, 0, 1, InputRefusalReason.StaleRevision)]
  [InlineData(true, false, 1, 0, InputRefusalReason.StaleRevision)]
  [InlineData(true, false, 0, 0, InputRefusalReason.Capacity)]
  public void NewInputRefusalUsesStoppingThenRevisionThenCapacityPriority(
      bool admissionEnabled, bool stopRequested, long handleRevision,
      long expectedRevision, InputRefusalReason expectedReason) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "accepted"), true, run);
    if (stopRequested)
      store.Runs.RequestStop(session, run, ownsRun: true);

    var input = new InputCommand(Guid.NewGuid(), expectedRevision, "new input");
    var admissionResult = store.Inputs.Admit(session, handleRevision, input, admissionEnabled, Guid.NewGuid());

    Assert.Equal(InputDecision.Refused, admissionResult.Receipt.Decision);
    Assert.Equal(expectedReason, admissionResult.Receipt.RefusalReason);
    Assert.False(admissionResult.NewlyAccepted);
    Assert.Null(admissionResult.Receipt.RunId);
    Assert.Equal(admissionResult.Receipt, store.Inputs.ReadReceipt(session, input.InputId));
    Assert.Single(StoredRuns.Read(database));
  }

  [Fact]
  public void ClaimIsPersistedOnceAndReopenDoesNotReplayActiveInput() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), run = Guid.NewGuid(), requestId;
    using (SessionStore store = SessionStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "claimed"), true, run);
      LlmRequest request = store.Runs.ClaimNextRequest(session, run, 0)!;
      requestId = request.RequestId;
      Assert.NotEqual(Guid.Empty, requestId);
      Assert.Single(request.Messages);
      Assert.Null(store.Runs.ClaimNextRequest(session, run, 0));
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    Assert.Null(reopened.Runs.ClaimNextRequest(session, run, 0));
    Assert.Null(reopened.Runs.ReadSettlement(session, run));
    Assert.Equal(requestId, Assert.Single(StoredRuns.ReadRequests(database)).RequestId);
    Assert.Equal(InputRefusalReason.Capacity,
        reopened.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "not replay or replacement"), true, Guid.NewGuid()).Receipt.RefusalReason);
  }

  [Fact]
  public void OrderedBatchesAndExplicitCompletionProduceOrderedPlainTextContext() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid(), output = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "first \0 input"), true, run);
    LlmRequest request = store.Runs.ClaimNextRequest(session, run, 0)!;
    var firstChunk = new LlmResponseChunk(request.RequestId, output, 0, "part🙂", false);
    Assert.False(store.Output.Append(session, run, 0, firstChunk with { ChunkIndex = 1 }));
    Assert.True(store.Output.Append(session, run, 0, firstChunk));
    Assert.False(store.Output.Append(session, run, 0, firstChunk));
    Assert.False(store.Output.Append(session, run, 0, firstChunk with { ChunkIndex = 1, ResponseId = Guid.NewGuid() }));
    Assert.False(store.Output.CompleteRequest(session, run, 0, new(request.RequestId, ExecutionOutcome.Completed)));
    Assert.Null(store.Runs.TrySettle(session, run, ExecutionOutcome.Completed));
    Assert.True(store.Output.Append(session, run, 0, firstChunk with { ChunkIndex = 1, Text = "\0 final", IsFinal = true }));
    Assert.False(store.Output.Append(session, run, 0, firstChunk with { ChunkIndex = 2 }));
    Assert.Null(store.Runs.TrySettle(session, run, ExecutionOutcome.Completed)); // Final text is not request success.
    var completion = new LlmRequestOutcome(request.RequestId, ExecutionOutcome.Completed);
    Assert.True(store.Output.CompleteRequest(session, run, 0, completion));
    Assert.True(store.Output.CompleteRequest(session, run, 0, completion));
    Assert.False(store.Output.CompleteRequest(session, run, 0, completion with { Outcome = ExecutionOutcome.Failed }));
    Assert.Equal(new ExecutionResult(run, ExecutionOutcome.Completed), store.Runs.TrySettle(session, run, ExecutionOutcome.Completed));
    Assert.Equal(new ExecutionResult(run, ExecutionOutcome.Completed), store.Runs.TrySettle(session, run, ExecutionOutcome.Failed));

    Guid nextRun = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "second"), true, nextRun);
    LlmRequest next = store.Runs.ClaimNextRequest(session, nextRun, 0)!;
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User }, next.Messages.Select(message => message.Role));
    Assert.Equal(new[] { "first \0 input", "part🙂\0 final", "second" }, next.Messages.Select(message => message.Text));
    Assert.Equal(2, StoredOutput.Read(database).Count);
    Assert.Equal(ExecutionOutcome.Completed,
        Assert.Single(StoredRuns.ReadRequests(database), request => request.RunId == run).Outcome);
  }

  [Theory]
  [InlineData(ExecutionOutcome.Completed)]
  [InlineData(ExecutionOutcome.Cancelled)]
  [InlineData(ExecutionOutcome.Failed)]
  public void CompletionRetriesAfterSettlementNeverRewriteSavedEvidence(ExecutionOutcome outcome) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid run = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "request"), true, run);
    LlmRequest request = store.Runs.ClaimNextRequest(session, run, 0)!;
    Assert.True(store.Output.Append(session, run, 0,
        new(request.RequestId, Guid.NewGuid(), 0, "answer", outcome == ExecutionOutcome.Completed)));
    var completion = new LlmRequestOutcome(request.RequestId, outcome,
        outcome == ExecutionOutcome.Failed ? "retained failure" : null);
    Assert.True(store.Output.CompleteRequest(session, run, 0, completion));
    Assert.Equal(outcome, store.Runs.TrySettle(session, run, outcome)!.Outcome);
    database.Execute("""
      CREATE TRIGGER reject_completion_rewrite BEFORE UPDATE ON execution_requests
      BEGIN SELECT RAISE(ABORT, 'A retry must not rewrite saved completion'); END;
      """);

    Assert.True(store.Output.CompleteRequest(session, run, 0, completion));
    Assert.False(store.Output.CompleteRequest(session, run, 0, completion with { Error = "changed" }));
    Assert.False(store.Output.CompleteRequest(Guid.NewGuid(), run, 0, completion));
    Assert.False(store.Output.CompleteRequest(session, Guid.NewGuid(), 0, completion));
    Assert.False(store.Output.CompleteRequest(session, run, 1, completion));
    Assert.False(store.Output.CompleteRequest(session, run, 0, completion with { RequestId = Guid.NewGuid() }));
    Assert.Equal(outcome, Assert.Single(StoredRuns.ReadRequests(database)).Outcome);
  }

  [Fact]
  public void RevisionAndRunFencesRejectStaleWritesAndAdmission() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "current"), true, run);
    Assert.Null(store.Runs.ClaimNextRequest(session, run, 1));
    Assert.Null(store.Runs.ClaimNextRequest(Guid.NewGuid(), run, 0));
    LlmRequest request = store.Runs.ClaimNextRequest(session, run, 0)!;
    var chunk = new LlmResponseChunk(request.RequestId, Guid.NewGuid(), 0, "late", true);
    Assert.False(store.Output.Append(session, Guid.NewGuid(), 0, chunk));
    Assert.False(store.Output.Append(session, run, 1, chunk));
    database.ForceHistoryRevision(1);
    Assert.False(store.Output.Append(session, run, 0, chunk));
    Assert.False(store.Output.CompleteRequest(session, run, 0, new(request.RequestId, ExecutionOutcome.Failed)));
    Assert.Equal(InputRefusalReason.StaleRevision,
        store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "stale"), true, Guid.NewGuid()).Receipt.RefusalReason);
    Assert.Empty(StoredOutput.Read(database));
  }

  [Fact]
  public void StopRejectsLateSuccessAndOnlyFencesAddressedRun() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "stop"), true, run);
    LlmRequest request = store.Runs.ClaimNextRequest(session, run, 0)!;
    var chunk = new LlmResponseChunk(request.RequestId, Guid.NewGuid(), 0, "committed before stop", true);
    Assert.True(store.Output.Append(session, run, 0, chunk));
    store.Runs.RequestStop(session, run, ownsRun: true);
    Assert.False(store.Output.CompleteRequest(session, run, 0, new(request.RequestId, ExecutionOutcome.Completed)));
    Assert.False(store.Output.Append(session, run, 0, chunk with { ChunkIndex = 1 }));
    Assert.True(store.Output.CompleteRequest(session, run, 0, new(request.RequestId, ExecutionOutcome.Cancelled)));
    Assert.Equal(ExecutionOutcome.Cancelled, store.Runs.TrySettle(session, run, ExecutionOutcome.Completed)!.Outcome);

    Guid nextRun = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "replacement after cleanup"), true, nextRun);
    store.Runs.RequestStop(session, run, ownsRun: true);
    store.Runs.RequestStop(Guid.NewGuid(), nextRun, ownsRun: true);
    Assert.NotNull(store.Runs.ClaimNextRequest(session, nextRun, 0));
    Assert.Null(store.Runs.ReadSettlement(session, nextRun));
    Assert.Equal(ExecutionOutcome.Cancelled, store.Runs.ReadSettlement(session, run)!.Outcome);
    Assert.Null(store.Runs.TrySettle(session, Guid.NewGuid(), ExecutionOutcome.Failed));
  }

  [Fact]
  public void PendingInputsAreClaimedOnceInOrderAndNeverLeakIntoEarlierRequests() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), run = Guid.NewGuid();
    var secondInput = new InputCommand(Guid.NewGuid(), 0, "second");
    using (SessionStore store = SessionStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "first"), true, run);
      LlmRequest first = store.Runs.ClaimNextRequest(session, run, 0)!;
      store.Inputs.Admit(session, 0, secondInput, true, Guid.NewGuid(), run);
      store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "third"), true, Guid.NewGuid(), run);
      Assert.Equal(new[] { "first" }, first.Messages.Select(message => message.Text));
      Assert.Null(store.Runs.ClaimNextRequest(session, run, 0));
      SaveResponse(store, session, run, first, "answer one");
      Assert.Null(store.Runs.TrySettle(session, run, ExecutionOutcome.Completed));

      LlmRequest second = Assert.IsType<LlmRequest>(store.Runs.ClaimNextRequest(session, run, 0));
      Assert.Equal(new[] { "first", "answer one", "second", "third" }, second.Messages.Select(message => message.Text));
      Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.User }, second.Messages.Select(message => message.Role));
      store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "fourth"), true, Guid.NewGuid(), run);
      SaveResponse(store, session, run, second, "answer two");
      Assert.Equal(3, StoredDeliveries.Read(database).Count(input => input.Outcome == ExecutionOutcome.Completed));
      Assert.Null(store.Runs.TrySettle(session, run, ExecutionOutcome.Completed));

      LlmRequest third = Assert.IsType<LlmRequest>(store.Runs.ClaimNextRequest(session, run, 0));
      Assert.Equal(new[] { "first", "answer one", "second", "third", "answer two", "fourth" }, third.Messages.Select(message => message.Text));
      SaveResponse(store, session, run, third, "done");
      Assert.Null(store.Runs.ClaimNextRequest(session, run, 0));
      Assert.Equal(ExecutionOutcome.Completed, store.Runs.TrySettle(session, run, ExecutionOutcome.Completed)!.Outcome);
      Assert.Equal(3, StoredRuns.ReadRequests(database).Count);
      Assert.Equal(2L, StoredDeliveries.ReadRequestOrder(database).Max(request => request.Index));
      Assert.Equal(4, StoredDeliveries.Read(database).Count(input => input.RequestId is not null && input.Outcome == ExecutionOutcome.Completed));
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    Assert.Null(reopened.Runs.ClaimNextRequest(session, run, 0));
    Assert.Equal(run, reopened.Inputs.Admit(session, 0, secondInput, false, Guid.Empty).Receipt.RunId);
    Assert.Equal(ExecutionOutcome.Completed, reopened.Runs.ReadSettlement(session, run)!.Outcome);
  }

  [Fact]
  public void SavedToolErrorsStillRequireModelContinuationBeforeSettlement() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "first"), true, run);
    LlmRequest first = store.Runs.ClaimNextRequest(session, run, 0)!;
    LlmToolCall[] calls = [new("call-one", "fake", JsonSerializer.SerializeToElement(new { value = 1 })),
        new("call-two", "fake", JsonSerializer.SerializeToElement(new { value = 2 }))];
    SaveResponse(store, session, run, first, "calling", calls);
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "steer one"), true, Guid.NewGuid(), run);
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "steer two"), true, Guid.NewGuid(), run);
    Assert.Null(store.Runs.ClaimNextRequest(session, run, 0));
    Assert.Null(store.Runs.TrySettle(session, run, ExecutionOutcome.Completed));
    Assert.True(store.Tools.TryStart(session, run, 0, first.RequestId, "call-one"));
    Assert.True(store.Tools.Complete(session, run, 0, first.RequestId,
        new("call-one", ExecutionOutcome.Failed, JsonSerializer.SerializeToElement(new { error = "syntax" }), "syntax")));
    Assert.Null(store.Runs.ClaimNextRequest(session, run, 0));
    Assert.True(store.Tools.TryStart(session, run, 0, first.RequestId, "call-two"));
    Assert.True(store.Tools.Complete(session, run, 0, first.RequestId,
        new("call-two", ExecutionOutcome.Completed, JsonSerializer.SerializeToElement(new { value = "ok" }))));
    Assert.Null(store.Runs.TrySettle(session, run, ExecutionOutcome.Completed));

    LlmRequest next = Assert.IsType<LlmRequest>(store.Runs.ClaimNextRequest(session, run, 0));
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.User },
        next.Messages.Select(message => message.Role));
    Assert.Equal(new[] { "call-one", "call-two" }, next.Messages[1].Contents.OfType<FunctionCallContent>().Select(call => call.CallId));
    FunctionResultContent[] results = next.Messages[2].Contents.OfType<FunctionResultContent>().ToArray();
    Assert.Equal(new[] { "call-one", "call-two" }, results.Select(result => result.CallId));
    Assert.Equal("syntax", Assert.IsType<JsonElement>(results[0].Result).GetProperty("error").GetString());
    Assert.Equal(new[] { "steer one", "steer two" }, next.Messages.Skip(3).Select(message => message.Text));
    SaveResponse(store, session, run, next, "corrected");
    Assert.Equal(ExecutionOutcome.Completed, store.Runs.TrySettle(session, run, ExecutionOutcome.Completed)!.Outcome);
  }

  [Fact]
  public void ToolBatchAloneClaimsContinuationButStopRetiresUndeliveredInputs() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "first"), true, run);
    LlmRequest first = store.Runs.ClaimNextRequest(session, run, 0)!;
    SaveResponse(store, session, run, first, "", [new("call", "fake", JsonSerializer.SerializeToElement(new { }))]);
    Assert.True(store.Tools.TryStart(session, run, 0, first.RequestId, "call"));
    Assert.True(store.Tools.Complete(session, run, 0, first.RequestId,
        new("call", ExecutionOutcome.Completed, JsonSerializer.SerializeToElement("ok"))));
    LlmRequest continuation = store.Runs.ClaimNextRequest(session, run, 0)!;
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool }, continuation.Messages.Select(message => message.Role));
    var pending = new InputCommand(Guid.NewGuid(), 0, "never delivered");
    InputReceipt receipt = store.Inputs.Admit(session, 0, pending, true, Guid.NewGuid(), run).Receipt;
    store.Runs.RequestStop(session, run, ownsRun: true);
    Assert.Single(StoredDeliveries.Read(database), input => input.RequestId is null && input.Outcome == ExecutionOutcome.Cancelled);
    Assert.Null(store.Runs.ClaimNextRequest(session, run, 0));
    Assert.Equal(ExecutionOutcome.Cancelled, store.Runs.TrySettle(session, run, ExecutionOutcome.Completed)!.Outcome);
    Assert.Equal(receipt, store.Inputs.ReadReceipt(session, pending.InputId));
    Guid nextRun = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "next run"), true, nextRun);
    Assert.DoesNotContain(store.Runs.ClaimNextRequest(session, nextRun, 0)!.Messages,
        message => message.Text == pending.Text);
  }

  private static void SaveResponse(SessionStore store, Guid session, Guid run, LlmRequest request,
      string text, IReadOnlyList<LlmToolCall>? calls = null) {
    Assert.True(store.Output.Append(session, run, 0, new(request.RequestId, Guid.NewGuid(), 0, text, true)));
    Assert.True(store.Output.CompleteRequest(session, run, 0,
        new(request.RequestId, ExecutionOutcome.Completed, ToolCalls: calls)));
  }

  [Fact]
  public void OutputValidationIsUtf8BoundedAndFailuresRemainTruthful() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "fail"), true, run);
    LlmRequest request = store.Runs.ClaimNextRequest(session, run, 0)!;
    var chunk = new LlmResponseChunk(request.RequestId, Guid.NewGuid(), 0, new string('é', 8192), false);
    Assert.Throws<ArgumentException>(() => store.Output.Append(session, run, 0, chunk with { Text = chunk.Text + "x" }));
    Assert.ThrowsAny<ArgumentException>(() => store.Output.Append(session, run, 0, chunk with { Text = "\ud800" }));
    Assert.True(store.Output.Append(session, run, 0, chunk));
    Assert.True(store.Output.CompleteRequest(session, run, 0, new(request.RequestId, ExecutionOutcome.Failed, "actual error")));
    Assert.False(store.Output.CompleteRequest(session, run, 0, new(request.RequestId, ExecutionOutcome.Failed, "different")));
    Assert.False(store.Output.Append(session, run, 0, chunk with { ChunkIndex = 1 }));
    Assert.Equal(ExecutionOutcome.Failed, store.Runs.TrySettle(session, run, ExecutionOutcome.Completed)!.Outcome);
    Assert.Equal("actual error", Assert.Single(StoredRuns.ReadRequests(database)).Error);
    Assert.Equal(16384, Assert.Single(StoredOutput.Read(database)).Bytes.Length);
  }
}
