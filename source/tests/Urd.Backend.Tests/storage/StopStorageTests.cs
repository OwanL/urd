using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

// Stop classification and pending-input retirement share one durable transaction.
public sealed class StopStorageTests {
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void MissingAndWrongSessionRunsAreNotFoundBeforeOwnership(bool ownsRun) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid otherSession = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid run = Guid.NewGuid(), missing = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "untouched"), true, run);
    StoredRun savedRun = Assert.Single(StoredRuns.Read(database));
    StoredDelivery delivery = Assert.Single(StoredDeliveries.Read(database));
    RejectUpdates(database);

    Assert.Equal(new StopReceipt(run, StopStatus.NotFound), store.Runs.RequestStop(otherSession, run, ownsRun));
    Assert.Equal(new StopReceipt(run, StopStatus.NotFound), store.Runs.RequestStop(Guid.Empty, run, ownsRun));
    Assert.Equal(new StopReceipt(missing, StopStatus.NotFound), store.Runs.RequestStop(session, missing, ownsRun));
    Assert.Equal(new StopReceipt(Guid.Empty, StopStatus.NotFound), store.Runs.RequestStop(session, Guid.Empty, ownsRun));
    Assert.Equal(savedRun, Assert.Single(StoredRuns.Read(database)));
    Assert.Equal(delivery, Assert.Single(StoredDeliveries.Read(database)));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void ReopenedUnownedRunIsUnavailableWithoutMutation(bool stopping) {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), run = Guid.NewGuid();
    using (SessionStore store = SessionStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "orphaned input"), true, run);
      if (stopping)
        Assert.Equal(StopStatus.Requested, store.Runs.RequestStop(session, run, ownsRun: true).Status);
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    StoredRun savedRun = Assert.Single(StoredRuns.Read(database));
    StoredDelivery delivery = Assert.Single(StoredDeliveries.Read(database));
    RejectUpdates(database);

    Assert.Equal(new StopReceipt(run, StopStatus.Unavailable), reopened.Runs.RequestStop(session, run, ownsRun: false));
    Assert.Equal(new StopReceipt(run, StopStatus.Unavailable), reopened.Runs.RequestStop(session, run, ownsRun: false));
    Assert.Equal(savedRun, Assert.Single(StoredRuns.Read(database)));
    Assert.Equal(delivery, Assert.Single(StoredDeliveries.Read(database)));
    Assert.Null(reopened.Runs.ReadSettlement(session, run));
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
    Guid run = Guid.NewGuid(), replacement = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "settled"), true, run);
    LlmRequest request = store.Runs.ClaimNextRequest(session, run, 0)!;
    Assert.True(store.Output.Append(session, run, 0, new(request.RequestId, Guid.NewGuid(), 0, "answer", true)));
    Assert.True(store.Output.CompleteRequest(session, run, 0, new(request.RequestId, outcome)));
    Assert.Equal(outcome, store.Runs.TrySettle(session, run, outcome)!.Outcome);
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "replacement"), true, replacement);
    StoredRun[] runs = StoredRuns.Read(database).ToArray();
    StoredDelivery[] deliveries = StoredDeliveries.Read(database).ToArray();
    RejectUpdates(database);

    var receipt = new StopReceipt(run, StopStatus.Settled, outcome);
    Assert.Equal(receipt, store.Runs.RequestStop(session, run, ownsRun: false));
    Assert.Equal(receipt, store.Runs.RequestStop(session, run, ownsRun: true));
    Assert.Equal(new StopReceipt(run, StopStatus.NotFound), store.Runs.RequestStop(otherSession, run, ownsRun: true));
    Assert.Equal(runs, StoredRuns.Read(database).ToArray());
    Assert.Equal(deliveries, StoredDeliveries.Read(database).ToArray());
    Assert.False(Assert.Single(StoredRuns.Read(database), saved => saved.RunId == replacement).Stopping);
  }

  [Fact]
  public void OwnedStopRetiresOnlyPendingInputsAndRetriesAdvanceToSettlement() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    var initial = new InputCommand(Guid.NewGuid(), 0, "claimed");
    var pending = new InputCommand(Guid.NewGuid(), 0, "pending one");
    var another = new InputCommand(Guid.NewGuid(), 0, "pending two");
    InputReceipt initialReceipt = store.Inputs.Admit(session, 0, initial, true, run).Receipt;
    LlmRequest request = store.Runs.ClaimNextRequest(session, run, 0)!;
    InputReceipt pendingReceipt = store.Inputs.Admit(session, 0, pending, true, Guid.NewGuid(), run).Receipt;
    InputReceipt anotherReceipt = store.Inputs.Admit(session, 0, another, true, Guid.NewGuid(), run).Receipt;

    var requested = new StopReceipt(run, StopStatus.Requested);
    Assert.Equal(requested, store.Runs.RequestStop(session, run, ownsRun: true));
    Assert.True(Assert.Single(StoredRuns.Read(database)).Stopping);
    Assert.Null(Assert.Single(StoredRuns.Read(database)).Outcome);
    StoredDelivery claimed = Assert.Single(StoredDeliveries.Read(database), input => input.InputId == initial.InputId);
    Assert.Equal(request.RequestId, claimed.RequestId);
    Assert.Null(claimed.Outcome);
    Assert.All(StoredDeliveries.Read(database).Where(input => input.RequestId is null),
        input => Assert.Equal(ExecutionOutcome.Cancelled, input.Outcome));
    Assert.Equal(2, StoredDeliveries.Read(database).Count(input => input.RequestId is null));
    Assert.Null(Assert.Single(StoredRuns.ReadRequests(database)).Outcome);
    Assert.Null(store.Runs.ClaimNextRequest(session, run, 0));
    Assert.Equal(initialReceipt, store.Inputs.ReadReceipt(session, initial.InputId));
    Assert.Equal(pendingReceipt, store.Inputs.ReadReceipt(session, pending.InputId));
    Assert.Equal(anotherReceipt, store.Inputs.ReadReceipt(session, another.InputId));
    RejectUpdates(database);
    Assert.Equal(requested, store.Runs.RequestStop(session, run, ownsRun: true));
    database.Execute("DROP TRIGGER reject_run_update; DROP TRIGGER reject_delivery_update;");

    Assert.Equal(ExecutionOutcome.Cancelled, store.Runs.TrySettle(session, run, ExecutionOutcome.Completed)!.Outcome);
    RejectUpdates(database);
    Assert.Equal(new StopReceipt(run, StopStatus.Settled, ExecutionOutcome.Cancelled),
        store.Runs.RequestStop(session, run, ownsRun: false));
  }

  [Fact]
  public void AlreadyStoppingRetryStillRetiresPendingInputsWithoutRewritingRun() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    var input = new InputCommand(Guid.NewGuid(), 0, "unclaimed");
    InputReceipt accepted = store.Inputs.Admit(session, 0, input, true, run).Receipt;
    // raw-sql: establish an existing Stop flag with an unretired delivery to exercise the retry invariant.
    database.Execute("""
      UPDATE execution_runs SET stopping = 1;
      CREATE TRIGGER reject_run_update BEFORE UPDATE ON execution_runs
      BEGIN SELECT RAISE(ABORT, 'Stop retry must not rewrite the run'); END;
      """);

    var requested = new StopReceipt(run, StopStatus.Requested);
    Assert.Equal(requested, store.Runs.RequestStop(session, run, ownsRun: true));
    StoredDelivery retired = Assert.Single(StoredDeliveries.Read(database));
    Assert.Null(retired.RequestId);
    Assert.Equal(ExecutionOutcome.Cancelled, retired.Outcome);
    Assert.Equal(accepted, store.Inputs.ReadReceipt(session, input.InputId));
    Assert.Null(Assert.Single(StoredRuns.Read(database)).Outcome);
    Assert.Equal(requested, store.Runs.RequestStop(session, run, ownsRun: true));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void RetirementAndStopFlagRollbackTogetherOnStatementOrCommitFailure(bool failAtCommit) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "claimed"), true, run);
    store.Runs.ClaimNextRequest(session, run, 0);
    var pending = new InputCommand(Guid.NewGuid(), 0, "pending");
    InputReceipt receipt = store.Inputs.Admit(session, 0, pending, true, Guid.NewGuid(), run).Receipt;
    StoredRun savedRun = Assert.Single(StoredRuns.Read(database));
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

    Assert.Throws<SqliteException>(() => store.Runs.RequestStop(session, run, ownsRun: true));
    Assert.Equal(savedRun, Assert.Single(StoredRuns.Read(database)));
    Assert.Equal(deliveries, StoredDeliveries.Read(database).ToArray());
    Assert.Equal(receipt, store.Inputs.ReadReceipt(session, pending.InputId));
    if (failAtCommit)
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;")); // raw-sql: fixture-owned rollback witness

    database.Execute("DROP TRIGGER fail_retirement;");
    Assert.Equal(new StopReceipt(run, StopStatus.Requested), store.Runs.RequestStop(session, run, ownsRun: true));
    Assert.True(Assert.Single(StoredRuns.Read(database)).Stopping);
    Assert.Equal(ExecutionOutcome.Cancelled,
        Assert.Single(StoredDeliveries.Read(database), input => input.InputId == pending.InputId).Outcome);
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
