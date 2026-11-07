using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

// Stop classification and pending-submission retirement share one durable transaction.
public sealed class StopStorageTests {
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void MissingAndWrongSessionExecutionsAreNotFoundBeforeOwnership(bool ownsExecution) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid otherSession = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid execution = Guid.NewGuid(), missing = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "untouched"), true, execution);
    StoredExecution savedExecution = Assert.Single(StoredExecutions.Read(database));
    StoredDelivery delivery = Assert.Single(StoredDeliveries.Read(database));
    RejectUpdates(database);

    Assert.Equal(new StopReceipt(execution, StopStatus.NotFound), store.Executions.RequestStop(otherSession, execution, ownsExecution));
    Assert.Equal(new StopReceipt(execution, StopStatus.NotFound), store.Executions.RequestStop(Guid.Empty, execution, ownsExecution));
    Assert.Equal(new StopReceipt(missing, StopStatus.NotFound), store.Executions.RequestStop(session, missing, ownsExecution));
    Assert.Equal(new StopReceipt(Guid.Empty, StopStatus.NotFound), store.Executions.RequestStop(session, Guid.Empty, ownsExecution));
    Assert.Equal(savedExecution, Assert.Single(StoredExecutions.Read(database)));
    Assert.Equal(delivery, Assert.Single(StoredDeliveries.Read(database)));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void ReopenedUnownedExecutionIsUnavailableWithoutMutation(bool stopping) {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), execution = Guid.NewGuid();
    using (SessionStore store = SessionStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "orphaned input"), true, execution);
      if (stopping)
        Assert.Equal(StopStatus.Requested, store.Executions.RequestStop(session, execution, ownsExecution: true).Status);
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    StoredExecution savedExecution = Assert.Single(StoredExecutions.Read(database));
    StoredDelivery delivery = Assert.Single(StoredDeliveries.Read(database));
    RejectUpdates(database);

    Assert.Equal(new StopReceipt(execution, StopStatus.Unavailable), reopened.Executions.RequestStop(session, execution, ownsExecution: false));
    Assert.Equal(new StopReceipt(execution, StopStatus.Unavailable), reopened.Executions.RequestStop(session, execution, ownsExecution: false));
    Assert.Equal(savedExecution, Assert.Single(StoredExecutions.Read(database)));
    Assert.Equal(delivery, Assert.Single(StoredDeliveries.Read(database)));
    Assert.Null(reopened.Executions.ReadSettlement(session, execution));
  }

  [Theory]
  [InlineData(ExecutionOutcome.Completed)]
  [InlineData(ExecutionOutcome.Cancelled)]
  [InlineData(ExecutionOutcome.Failed)]
  public void SettledOutcomeWinsOverOwnershipAndCannotStopReplacement(ExecutionOutcome outcome) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid otherSession = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid execution = Guid.NewGuid(), replacement = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "settled"), true, execution);
    LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    Assert.True(store.LlmResponses.Append(session, execution, 0, new(request.RequestId, Guid.NewGuid(), 0, "answer", true)));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, new(request.RequestId, outcome)));
    Assert.Equal(outcome, store.Executions.TrySettle(session, execution, outcome)!.Outcome);
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "replacement"), true, replacement);
    StoredExecution[] executions = StoredExecutions.Read(database).ToArray();
    StoredDelivery[] deliveries = StoredDeliveries.Read(database).ToArray();
    RejectUpdates(database);

    var receipt = new StopReceipt(execution, StopStatus.Settled, outcome);
    Assert.Equal(receipt, store.Executions.RequestStop(session, execution, ownsExecution: false));
    Assert.Equal(receipt, store.Executions.RequestStop(session, execution, ownsExecution: true));
    Assert.Equal(new StopReceipt(execution, StopStatus.NotFound), store.Executions.RequestStop(otherSession, execution, ownsExecution: true));
    Assert.Equal(executions, StoredExecutions.Read(database).ToArray());
    Assert.Equal(deliveries, StoredDeliveries.Read(database).ToArray());
    Assert.False(Assert.Single(StoredExecutions.Read(database), saved => saved.ExecutionId == replacement).Stopping);
  }

  [Fact]
  public void OwnedStopRetiresOnlyPendingSubmissionsAndRetriesAdvanceToSettlement() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    var initial = new UserMessageSubmission(Guid.NewGuid(), 0, "claimed");
    var pending = new UserMessageSubmission(Guid.NewGuid(), 0, "pending one");
    var another = new UserMessageSubmission(Guid.NewGuid(), 0, "pending two");
    UserMessageReceipt initialReceipt = store.UserMessages.Admit(session, 0, initial, true, execution).Receipt;
    LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    UserMessageReceipt pendingReceipt = store.UserMessages.Admit(session, 0, pending, true, Guid.NewGuid(), execution).Receipt;
    UserMessageReceipt anotherReceipt = store.UserMessages.Admit(session, 0, another, true, Guid.NewGuid(), execution).Receipt;

    var requested = new StopReceipt(execution, StopStatus.Requested);
    Assert.Equal(requested, store.Executions.RequestStop(session, execution, ownsExecution: true));
    Assert.True(Assert.Single(StoredExecutions.Read(database)).Stopping);
    Assert.Null(Assert.Single(StoredExecutions.Read(database)).Outcome);
    StoredDelivery claimed = Assert.Single(StoredDeliveries.Read(database), submission => submission.SubmissionId == initial.SubmissionId);
    Assert.Equal(request.RequestId, claimed.RequestId);
    Assert.Null(claimed.Outcome);
    Assert.All(StoredDeliveries.Read(database).Where(submission => submission.RequestId is null),
        submission => Assert.Equal(ExecutionOutcome.Cancelled, submission.Outcome));
    Assert.Equal(2, StoredDeliveries.Read(database).Count(submission => submission.RequestId is null));
    Assert.Null(Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
    Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Equal(initialReceipt, store.UserMessages.ReadReceipt(session, initial.SubmissionId));
    Assert.Equal(pendingReceipt, store.UserMessages.ReadReceipt(session, pending.SubmissionId));
    Assert.Equal(anotherReceipt, store.UserMessages.ReadReceipt(session, another.SubmissionId));
    RejectUpdates(database);
    Assert.Equal(requested, store.Executions.RequestStop(session, execution, ownsExecution: true));
    database.Execute("DROP TRIGGER reject_run_update; DROP TRIGGER reject_delivery_update;");

    Assert.Equal(ExecutionOutcome.Cancelled, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
    RejectUpdates(database);
    Assert.Equal(new StopReceipt(execution, StopStatus.Settled, ExecutionOutcome.Cancelled),
        store.Executions.RequestStop(session, execution, ownsExecution: false));
  }

  [Fact]
  public void AlreadyStoppingRetryStillRetiresPendingSubmissionsWithoutRewritingExecution() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "unclaimed");
    UserMessageReceipt accepted = store.UserMessages.Admit(session, 0, submission, true, execution).Receipt;
    // raw-sql: establish an existing Stop flag with an unretired delivery to exercise the retry invariant.
    database.Execute("""
      UPDATE execution_runs SET stopping = 1;
      CREATE TRIGGER reject_run_update BEFORE UPDATE ON execution_runs
      BEGIN SELECT RAISE(ABORT, 'Stop retry must not rewrite the run'); END;
      """);

    var requested = new StopReceipt(execution, StopStatus.Requested);
    Assert.Equal(requested, store.Executions.RequestStop(session, execution, ownsExecution: true));
    StoredDelivery retired = Assert.Single(StoredDeliveries.Read(database));
    Assert.Null(retired.RequestId);
    Assert.Equal(ExecutionOutcome.Cancelled, retired.Outcome);
    Assert.Equal(accepted, store.UserMessages.ReadReceipt(session, submission.SubmissionId));
    Assert.Null(Assert.Single(StoredExecutions.Read(database)).Outcome);
    Assert.Equal(requested, store.Executions.RequestStop(session, execution, ownsExecution: true));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void RetirementAndStopFlagRollbackTogetherOnStatementOrCommitFailure(bool failAtCommit) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "claimed"), true, execution);
    store.Executions.ClaimNextRequest(session, execution, 0);
    var pending = new UserMessageSubmission(Guid.NewGuid(), 0, "pending");
    UserMessageReceipt receipt = store.UserMessages.Admit(session, 0, pending, true, Guid.NewGuid(), execution).Receipt;
    StoredExecution savedExecution = Assert.Single(StoredExecutions.Read(database));
    StoredDelivery[] deliveries = StoredDeliveries.Read(database).ToArray();
    database.Execute(failAtCommit ? """
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_retirement AFTER UPDATE OF outcome ON execution_inputs
      BEGIN INSERT INTO test_child VALUES (1); END;
      """ : """
      CREATE TRIGGER fail_retirement BEFORE UPDATE OF outcome ON execution_inputs
      BEGIN SELECT RAISE(ABORT, 'Retirement unavailable'); END;
      """);

    Assert.Throws<SqliteException>(() => store.Executions.RequestStop(session, execution, ownsExecution: true));
    Assert.Equal(savedExecution, Assert.Single(StoredExecutions.Read(database)));
    Assert.Equal(deliveries, StoredDeliveries.Read(database).ToArray());
    Assert.Equal(receipt, store.UserMessages.ReadReceipt(session, pending.SubmissionId));
    if (failAtCommit)
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;")); // raw-sql: fixture-owned rollback witness

    database.Execute("DROP TRIGGER fail_retirement;");
    Assert.Equal(new StopReceipt(execution, StopStatus.Requested), store.Executions.RequestStop(session, execution, ownsExecution: true));
    Assert.True(Assert.Single(StoredExecutions.Read(database)).Stopping);
    Assert.Equal(ExecutionOutcome.Cancelled,
        Assert.Single(StoredDeliveries.Read(database), submission => submission.SubmissionId == pending.SubmissionId).Outcome);
  }

  private static void RejectUpdates(TemporaryDatabase database) {
    database.Execute("""
      CREATE TRIGGER reject_run_update BEFORE UPDATE ON execution_runs
      BEGIN SELECT RAISE(ABORT, 'Run must not change'); END;
      CREATE TRIGGER reject_delivery_update BEFORE UPDATE ON execution_inputs
      BEGIN SELECT RAISE(ABORT, 'Delivery must not change'); END;
      """);
  }
}
