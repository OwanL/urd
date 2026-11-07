using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

// Transactional session execution and LLM response gates: commit rollback, revision fences, retries and settlement.
public sealed class ExecutionStorageTests {
  [Fact]
  public void AcceptanceAndExecutionRollbackTogetherOnCommitFailure() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "atomic input");
    Guid execution = Guid.NewGuid();
    database.Execute("""
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_run AFTER INSERT ON execution_runs
      BEGIN
          INSERT INTO test_child VALUES (1);
      END;
      """);
    Assert.Throws<SqliteException>(() => store.UserMessages.Admit(session, 0, submission, true, execution));
    Assert.Null(store.UserMessages.ReadReceipt(session, submission.SubmissionId));
    Assert.Empty(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;")); // raw-sql: test_child is a fixture-owned rollback witness

    database.Execute("DROP TRIGGER fail_run;");
    var admitted = store.UserMessages.Admit(session, 0, submission, true, execution);
    Assert.True(admitted.NewlyAccepted);
    Assert.Equal(execution, admitted.Receipt.ExecutionId);
    StoredExecution stored = Assert.Single(StoredExecutions.Read(database));
    Assert.Equal(execution, stored.ExecutionId);
    Assert.Equal(session, stored.SessionId);
    Assert.Equal(submission.SubmissionId, stored.SubmissionId);
  }

  [Fact]
  public void RetainedAcceptanceResolvesBeforeAllCurrentGatesAndCannotCreateAnotherExecution() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), execution = Guid.NewGuid();
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "original \0 🙂");
    UserMessageReceipt receipt;
    using (SessionStore store = SessionStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      receipt = store.UserMessages.Admit(session, 0, submission, true, execution).Receipt;
      Assert.Equal(ExecutionOutcome.Failed, store.Executions.TrySettle(session, execution, ExecutionOutcome.Failed)!.Outcome);
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    // Settlement alone must prevent a claim: no Stop, revision mismatch or prior request can mask it.
    Assert.Null(reopened.Executions.ClaimNextRequest(session, execution, 0));
    database.ForceHistoryRevision(7);
    var retry = reopened.UserMessages.Admit(session, 7, submission, false, Guid.Empty);
    Assert.Equal(receipt, retry.Receipt);
    Assert.False(retry.NewlyAccepted);
    Assert.Throws<UserMessageConflictException>(() => reopened.UserMessages.Admit(session, 7, submission with { Text = "changed" }, false, Guid.NewGuid()));
    Assert.Throws<UserMessageConflictException>(() => reopened.UserMessages.Admit(session, 7, submission with { ExpectedHistoryRevision = 7 }, false, Guid.NewGuid()));
    Assert.Single(StoredExecutions.Read(database));
  }

  [Fact]
  public void BusyAndStoppingRefusalsAreDurableWithoutTranscriptOrExecutionFacts() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "accepted"), true, execution);
    var busySubmission = new UserMessageSubmission(Guid.NewGuid(), 0, "refused while busy");
    var busy = store.UserMessages.Admit(session, 0, busySubmission, true, Guid.NewGuid());
    Assert.Equal(MessageRefusalReason.Capacity, busy.Receipt.RefusalReason);
    Assert.False(busy.NewlyAccepted);
    Assert.Null(busy.Receipt.ExecutionId);
    store.Executions.RequestStop(session, execution, ownsExecution: true);
    var stopped = store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "refused while stopping"), true, Guid.NewGuid());
    Assert.Equal(MessageRefusalReason.Stopping, stopped.Receipt.RefusalReason);
    store.Executions.TrySettle(session, execution, ExecutionOutcome.Cancelled);
    Assert.Equal(busy.Receipt, store.UserMessages.Admit(session, 0, busySubmission, true, Guid.NewGuid()).Receipt);
    Guid nextExecution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "next accepted"), true, nextExecution);
    LlmRequest next = store.Executions.ClaimNextRequest(session, nextExecution, 0)!;
    Assert.Equal(new[] { "next accepted" }, next.Messages.Select(message => message.Text));
    Assert.Equal(2, StoredExecutions.Read(database).Count);
    Assert.Equal(4, StoredSubmissions.Read(database).Count);
  }

  [Theory]
  [InlineData(false, false, 0, 1, MessageRefusalReason.Stopping)]
  [InlineData(true, true, 0, 1, MessageRefusalReason.Stopping)]
  [InlineData(true, false, 0, 1, MessageRefusalReason.StaleRevision)]
  [InlineData(true, false, 1, 0, MessageRefusalReason.StaleRevision)]
  [InlineData(true, false, 0, 0, MessageRefusalReason.Capacity)]
  public void NewSubmissionRefusalUsesStoppingThenRevisionThenCapacityPriority(
      bool admissionEnabled, bool stopRequested, long handleRevision,
      long expectedRevision, MessageRefusalReason expectedReason) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "accepted"), true, execution);
    if (stopRequested)
      store.Executions.RequestStop(session, execution, ownsExecution: true);

    var submission = new UserMessageSubmission(Guid.NewGuid(), expectedRevision, "new input");
    var admissionResult = store.UserMessages.Admit(session, handleRevision, submission, admissionEnabled, Guid.NewGuid());

    Assert.Equal(MessageAcceptance.Refused, admissionResult.Receipt.Acceptance);
    Assert.Equal(expectedReason, admissionResult.Receipt.RefusalReason);
    Assert.False(admissionResult.NewlyAccepted);
    Assert.Null(admissionResult.Receipt.ExecutionId);
    Assert.Equal(admissionResult.Receipt, store.UserMessages.ReadReceipt(session, submission.SubmissionId));
    Assert.Single(StoredExecutions.Read(database));
  }

  [Fact]
  public void ClaimIsPersistedOnceAndReopenDoesNotReplayActiveSubmission() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), execution = Guid.NewGuid(), requestId;
    using (SessionStore store = SessionStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "claimed"), true, execution);
      LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
      requestId = request.RequestId;
      Assert.NotEqual(Guid.Empty, requestId);
      Assert.Single(request.Messages);
      Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    Assert.Null(reopened.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Null(reopened.Executions.ReadSettlement(session, execution));
    Assert.Equal(requestId, Assert.Single(StoredExecutions.ReadRequests(database)).RequestId);
    Assert.Equal(MessageRefusalReason.Capacity,
        reopened.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "not replay or replacement"), true, Guid.NewGuid()).Receipt.RefusalReason);
  }

  [Fact]
  public void OrderedBatchesAndExplicitCompletionProduceOrderedPlainTextContext() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid(), output = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "first \0 input"), true, execution);
    LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    var firstChunk = new LlmResponseChunk(request.RequestId, output, 0, "part🙂", false);
    Assert.False(store.LlmResponses.Append(session, execution, 0, firstChunk with { ChunkIndex = 1 }));
    Assert.True(store.LlmResponses.Append(session, execution, 0, firstChunk));
    Assert.False(store.LlmResponses.Append(session, execution, 0, firstChunk));
    Assert.False(store.LlmResponses.Append(session, execution, 0, firstChunk with { ChunkIndex = 1, ResponseId = Guid.NewGuid() }));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0, new(request.RequestId, ExecutionOutcome.Completed)));
    Assert.Null(store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed));
    Assert.True(store.LlmResponses.Append(session, execution, 0, firstChunk with { ChunkIndex = 1, Text = "\0 final", IsFinal = true }));
    Assert.False(store.LlmResponses.Append(session, execution, 0, firstChunk with { ChunkIndex = 2 }));
    Assert.Null(store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)); // Final text is not request success.
    var completion = new LlmRequestResult(request.RequestId, ExecutionOutcome.Completed);
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0, completion with { Outcome = ExecutionOutcome.Failed }));
    Assert.Equal(new SessionExecutionResult(execution, ExecutionOutcome.Completed), store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed));
    Assert.Equal(new SessionExecutionResult(execution, ExecutionOutcome.Completed), store.Executions.TrySettle(session, execution, ExecutionOutcome.Failed));

    Guid nextExecution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "second"), true, nextExecution);
    LlmRequest next = store.Executions.ClaimNextRequest(session, nextExecution, 0)!;
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User }, next.Messages.Select(message => message.Role));
    Assert.Equal(new[] { "first \0 input", "part🙂\0 final", "second" }, next.Messages.Select(message => message.Text));
    Assert.Equal(2, StoredLlmResponses.Read(database).Count);
    Assert.Equal(ExecutionOutcome.Completed,
        Assert.Single(StoredExecutions.ReadRequests(database), request => request.ExecutionId == execution).Outcome);
  }

  [Theory]
  [InlineData(ExecutionOutcome.Completed)]
  [InlineData(ExecutionOutcome.Cancelled)]
  [InlineData(ExecutionOutcome.Failed)]
  public void CompletionRetriesAfterSettlementNeverRewriteSavedEvidence(ExecutionOutcome outcome) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "request"), true, execution);
    LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    Assert.True(store.LlmResponses.Append(session, execution, 0,
        new(request.RequestId, Guid.NewGuid(), 0, "answer", outcome == ExecutionOutcome.Completed)));
    var completion = new LlmRequestResult(request.RequestId, outcome,
        outcome == ExecutionOutcome.Failed ? "retained failure" : null);
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.Equal(outcome, store.Executions.TrySettle(session, execution, outcome)!.Outcome);
    database.Execute("""
      CREATE TRIGGER reject_completion_rewrite BEFORE UPDATE ON execution_requests
      BEGIN SELECT RAISE(ABORT, 'A retry must not rewrite saved completion'); END;
      """);

    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, completion));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0, completion with { Error = "changed" }));
    Assert.False(store.LlmResponses.CompleteRequest(Guid.NewGuid(), execution, 0, completion));
    Assert.False(store.LlmResponses.CompleteRequest(session, Guid.NewGuid(), 0, completion));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 1, completion));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0, completion with { RequestId = Guid.NewGuid() }));
    Assert.Equal(outcome, Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
  }

  [Fact]
  public void RevisionAndExecutionFencesRejectStaleWritesAndAdmission() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "current"), true, execution);
    Assert.Null(store.Executions.ClaimNextRequest(session, execution, 1));
    Assert.Null(store.Executions.ClaimNextRequest(Guid.NewGuid(), execution, 0));
    LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    var chunk = new LlmResponseChunk(request.RequestId, Guid.NewGuid(), 0, "late", true);
    Assert.False(store.LlmResponses.Append(session, Guid.NewGuid(), 0, chunk));
    Assert.False(store.LlmResponses.Append(session, execution, 1, chunk));
    database.ForceHistoryRevision(1);
    Assert.False(store.LlmResponses.Append(session, execution, 0, chunk));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0, new(request.RequestId, ExecutionOutcome.Failed)));
    Assert.Equal(MessageRefusalReason.StaleRevision,
        store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "stale"), true, Guid.NewGuid()).Receipt.RefusalReason);
    Assert.Empty(StoredLlmResponses.Read(database));
  }

  [Fact]
  public void StopRejectsLateSuccessAndOnlyFencesAddressedExecution() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "stop"), true, execution);
    LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    var chunk = new LlmResponseChunk(request.RequestId, Guid.NewGuid(), 0, "committed before stop", true);
    Assert.True(store.LlmResponses.Append(session, execution, 0, chunk));
    store.Executions.RequestStop(session, execution, ownsExecution: true);
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0, new(request.RequestId, ExecutionOutcome.Completed)));
    Assert.False(store.LlmResponses.Append(session, execution, 0, chunk with { ChunkIndex = 1 }));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, new(request.RequestId, ExecutionOutcome.Cancelled)));
    Assert.Equal(ExecutionOutcome.Cancelled, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);

    Guid nextExecution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "replacement after cleanup"), true, nextExecution);
    store.Executions.RequestStop(session, execution, ownsExecution: true);
    store.Executions.RequestStop(Guid.NewGuid(), nextExecution, ownsExecution: true);
    Assert.NotNull(store.Executions.ClaimNextRequest(session, nextExecution, 0));
    Assert.Null(store.Executions.ReadSettlement(session, nextExecution));
    Assert.Equal(ExecutionOutcome.Cancelled, store.Executions.ReadSettlement(session, execution)!.Outcome);
    Assert.Null(store.Executions.TrySettle(session, Guid.NewGuid(), ExecutionOutcome.Failed));
  }

  [Fact]
  public void PendingSubmissionsAreClaimedOnceInOrderAndNeverLeakIntoEarlierRequests() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), execution = Guid.NewGuid();
    var secondSubmission = new UserMessageSubmission(Guid.NewGuid(), 0, "second");
    using (SessionStore store = SessionStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "first"), true, execution);
      LlmRequest first = store.Executions.ClaimNextRequest(session, execution, 0)!;
      store.UserMessages.Admit(session, 0, secondSubmission, true, Guid.NewGuid(), execution);
      store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "third"), true, Guid.NewGuid(), execution);
      Assert.Equal(new[] { "first" }, first.Messages.Select(message => message.Text));
      Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
      SaveResponse(store, session, execution, first, "answer one");
      Assert.Null(store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed));

      LlmRequest second = Assert.IsType<LlmRequest>(store.Executions.ClaimNextRequest(session, execution, 0));
      Assert.Equal(new[] { "first", "answer one", "second", "third" }, second.Messages.Select(message => message.Text));
      Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.User }, second.Messages.Select(message => message.Role));
      store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "fourth"), true, Guid.NewGuid(), execution);
      SaveResponse(store, session, execution, second, "answer two");
      Assert.Equal(3, StoredDeliveries.Read(database).Count(submission => submission.Outcome == ExecutionOutcome.Completed));
      Assert.Null(store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed));

      LlmRequest third = Assert.IsType<LlmRequest>(store.Executions.ClaimNextRequest(session, execution, 0));
      Assert.Equal(new[] { "first", "answer one", "second", "third", "answer two", "fourth" }, third.Messages.Select(message => message.Text));
      SaveResponse(store, session, execution, third, "done");
      Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
      Assert.Equal(ExecutionOutcome.Completed, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
      Assert.Equal(3, StoredExecutions.ReadRequests(database).Count);
      Assert.Equal(2L, StoredDeliveries.ReadRequestOrder(database).Max(request => request.Index));
      Assert.Equal(4, StoredDeliveries.Read(database).Count(submission => submission.RequestId is not null && submission.Outcome == ExecutionOutcome.Completed));
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    Assert.Null(reopened.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Equal(execution, reopened.UserMessages.Admit(session, 0, secondSubmission, false, Guid.Empty).Receipt.ExecutionId);
    Assert.Equal(ExecutionOutcome.Completed, reopened.Executions.ReadSettlement(session, execution)!.Outcome);
  }

  [Fact]
  public void SavedToolErrorsStillRequireLlmContinuationBeforeSettlement() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "first"), true, execution);
    LlmRequest first = store.Executions.ClaimNextRequest(session, execution, 0)!;
    LlmToolCall[] calls = [new("call-one", "fake", JsonSerializer.SerializeToElement(new { value = 1 })),
        new("call-two", "fake", JsonSerializer.SerializeToElement(new { value = 2 }))];
    SaveResponse(store, session, execution, first, "calling", calls);
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "steer one"), true, Guid.NewGuid(), execution);
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "steer two"), true, Guid.NewGuid(), execution);
    Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Null(store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed));
    Assert.True(store.Tools.TryStart(session, execution, 0, first.RequestId, "call-one"));
    Assert.True(store.Tools.Complete(session, execution, 0, first.RequestId,
        new("call-one", ExecutionOutcome.Failed, JsonSerializer.SerializeToElement(new { error = "syntax" }), "syntax")));
    Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
    Assert.True(store.Tools.TryStart(session, execution, 0, first.RequestId, "call-two"));
    Assert.True(store.Tools.Complete(session, execution, 0, first.RequestId,
        new("call-two", ExecutionOutcome.Completed, JsonSerializer.SerializeToElement(new { value = "ok" }))));
    Assert.Null(store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed));

    LlmRequest next = Assert.IsType<LlmRequest>(store.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.User },
        next.Messages.Select(message => message.Role));
    Assert.Equal(new[] { "call-one", "call-two" }, next.Messages[1].Contents.OfType<FunctionCallContent>().Select(call => call.CallId));
    FunctionResultContent[] results = next.Messages[2].Contents.OfType<FunctionResultContent>().ToArray();
    Assert.Equal(new[] { "call-one", "call-two" }, results.Select(result => result.CallId));
    Assert.Equal("syntax", Assert.IsType<JsonElement>(results[0].Result).GetProperty("error").GetString());
    Assert.Equal(new[] { "steer one", "steer two" }, next.Messages.Skip(3).Select(message => message.Text));
    SaveResponse(store, session, execution, next, "corrected");
    Assert.Equal(ExecutionOutcome.Completed, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
  }

  [Fact]
  public void ToolBatchAloneClaimsContinuationButStopRetiresUndeliveredSubmissions() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "first"), true, execution);
    LlmRequest first = store.Executions.ClaimNextRequest(session, execution, 0)!;
    SaveResponse(store, session, execution, first, "", [new("call", "fake", JsonSerializer.SerializeToElement(new { }))]);
    Assert.True(store.Tools.TryStart(session, execution, 0, first.RequestId, "call"));
    Assert.True(store.Tools.Complete(session, execution, 0, first.RequestId,
        new("call", ExecutionOutcome.Completed, JsonSerializer.SerializeToElement("ok"))));
    LlmRequest continuation = store.Executions.ClaimNextRequest(session, execution, 0)!;
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool }, continuation.Messages.Select(message => message.Role));
    var pending = new UserMessageSubmission(Guid.NewGuid(), 0, "never delivered");
    UserMessageReceipt receipt = store.UserMessages.Admit(session, 0, pending, true, Guid.NewGuid(), execution).Receipt;
    store.Executions.RequestStop(session, execution, ownsExecution: true);
    Assert.Single(StoredDeliveries.Read(database), submission => submission.RequestId is null && submission.Outcome == ExecutionOutcome.Cancelled);
    Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Equal(ExecutionOutcome.Cancelled, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
    Assert.Equal(receipt, store.UserMessages.ReadReceipt(session, pending.SubmissionId));
    Guid nextExecution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "next run"), true, nextExecution);
    Assert.DoesNotContain(store.Executions.ClaimNextRequest(session, nextExecution, 0)!.Messages,
        message => message.Text == pending.Text);
  }

  private static void SaveResponse(SessionStore store, Guid session, Guid execution, LlmRequest request,
      string text, IReadOnlyList<LlmToolCall>? calls = null) {
    Assert.True(store.LlmResponses.Append(session, execution, 0, new(request.RequestId, Guid.NewGuid(), 0, text, true)));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
        new(request.RequestId, ExecutionOutcome.Completed, ToolCalls: calls)));
  }

  [Fact]
  public void OutputValidationIsUtf8BoundedAndFailuresRemainTruthful() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "fail"), true, execution);
    LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    var chunk = new LlmResponseChunk(request.RequestId, Guid.NewGuid(), 0, new string('é', 8192), false);
    Assert.Throws<ArgumentException>(() => store.LlmResponses.Append(session, execution, 0, chunk with { Text = chunk.Text + "x" }));
    Assert.ThrowsAny<ArgumentException>(() => store.LlmResponses.Append(session, execution, 0, chunk with { Text = "\ud800" }));
    Assert.True(store.LlmResponses.Append(session, execution, 0, chunk));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, new(request.RequestId, ExecutionOutcome.Failed, "actual error")));
    Assert.False(store.LlmResponses.CompleteRequest(session, execution, 0, new(request.RequestId, ExecutionOutcome.Failed, "different")));
    Assert.False(store.LlmResponses.Append(session, execution, 0, chunk with { ChunkIndex = 1 }));
    Assert.Equal(ExecutionOutcome.Failed, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
    Assert.Equal("actual error", Assert.Single(StoredExecutions.ReadRequests(database)).Error);
    Assert.Equal(16384, Assert.Single(StoredLlmResponses.Read(database)).Bytes.Length);
  }
}
