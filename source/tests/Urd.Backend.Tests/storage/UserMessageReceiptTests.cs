using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Storage;

// Durable message acceptance receipts: exact retries, conflicting originals, rollback and version-one migration.
public sealed class UserMessageReceiptTests {
  [Fact]
  public void LostRefusalReplyDoesNotPermitAcceptanceAfterReopen() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid();
    Guid firstProposedExecutionId = Guid.NewGuid(), retryProposedExecutionId = Guid.NewGuid();
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "original \r\n e\u0301 \0 🙂");
    using (SessionStore store = SessionStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(sessionId);
      // Deliberately discard the committed reply, as an uncertain caller would.
      store.UserMessages.Admit(sessionId, 0, submission, accepting: false, proposedExecutionId: firstProposedExecutionId);
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    var retry = reopened.UserMessages.Admit(sessionId, 0, submission, accepting: true, proposedExecutionId: retryProposedExecutionId);
    UserMessageReceipt receipt = reopened.UserMessages.ReadReceipt(sessionId, submission.SubmissionId)!;
    Assert.Equal(sessionId, receipt.SessionId);
    Assert.Equal(submission, receipt.Submission);
    Assert.Equal(MessageAcceptance.Refused, receipt.Acceptance);
    Assert.Null(receipt.ExecutionId);
    Assert.Equal(MessageRefusalReason.Stopping, receipt.RefusalReason);
    Assert.Equal(receipt, retry.Receipt);
    Assert.False(retry.NewlyAccepted);
    Assert.Single(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
    Assert.Equal(0L, reopened.Sessions.Read(sessionId)!.HistoryRevision);
  }

  [Fact]
  public void DelayedStaleAdmissionCannotReplaceFirstRefusal() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "delayed original");
    UserMessageReceipt firstReceipt = store.UserMessages.Admit(
        sessionId, 0, submission, accepting: false, proposedExecutionId: Guid.NewGuid()).Receipt;
    database.ForceHistoryRevision(1);

    var delayed = store.UserMessages.Admit(
        sessionId, 0, submission, accepting: true, proposedExecutionId: Guid.NewGuid());
    Assert.Equal(firstReceipt, delayed.Receipt);
    Assert.False(delayed.NewlyAccepted);
    Assert.Equal(firstReceipt, store.UserMessages.ReadReceipt(sessionId, submission.SubmissionId));
    Assert.Equal(MessageRefusalReason.Stopping, firstReceipt.RefusalReason);
    Assert.Equal(1L, store.Sessions.Read(sessionId)!.HistoryRevision);
    Assert.Single(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
  }

  [Fact]
  public void ConflictingTextOrRevisionNeverResolvesOrOverwritesOriginal() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "e\u0301\r\n");
    UserMessageReceipt original = store.UserMessages.Admit(
        sessionId, 0, submission, accepting: false, proposedExecutionId: Guid.NewGuid()).Receipt;
    UserMessageSubmission[] conflicts =
    [
        submission with { Text = "é\r\n" },
            submission with { Text = "e\u0301\n" },
            submission with { Text = submission.Text + " " },
            submission with { ExpectedHistoryRevision = 1 }
    ];

    Assert.All(conflicts, conflict => {
      UserMessageConflictException failure = Assert.Throws<UserMessageConflictException>(() =>
          store.UserMessages.Admit(sessionId, 0, conflict, accepting: true, proposedExecutionId: Guid.NewGuid()));
      Assert.Equal(submission.SubmissionId, failure.SubmissionId);
    });

    Assert.Equal(original, store.UserMessages.ReadReceipt(sessionId, submission.SubmissionId));
    Assert.Single(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
  }

  [Fact]
  public void SubmissionIdentityIsScopedToSessionAndMissingLookupDoesNotInsert() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid first = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid second = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "first session");
    UserMessageReceipt firstReceipt = store.UserMessages.Admit(
        first, 0, submission, accepting: false, proposedExecutionId: Guid.NewGuid()).Receipt;
    Assert.Null(store.UserMessages.ReadReceipt(second, submission.SubmissionId));
    Assert.Null(store.UserMessages.ReadReceipt(Guid.NewGuid(), submission.SubmissionId));
    Guid secondExecutionId = Guid.NewGuid();
    UserMessageReceipt secondReceipt = store.UserMessages.Admit(second, 0, submission with { Text = "second session" },
        accepting: true, proposedExecutionId: secondExecutionId).Receipt;
    Assert.Equal(MessageAcceptance.Refused, firstReceipt.Acceptance);
    Assert.Equal(MessageAcceptance.Accepted, secondReceipt.Acceptance);
    Assert.Equal(secondExecutionId, secondReceipt.ExecutionId);
    Assert.NotEqual(firstReceipt, secondReceipt);
    Assert.Equal(2, StoredSubmissions.Read(database).Count);
    StoredExecution storedExecution = Assert.Single(StoredExecutions.Read(database));
    Assert.Equal(second, storedExecution.SessionId);
    Assert.Equal(submission.SubmissionId, storedExecution.SubmissionId);
  }

  [Fact]
  public void TransactionFailureLeavesNoPartialDecisionAndAllowsRetry() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "rolled back");
    database.Execute("""
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (
          parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED
      );
      CREATE TRIGGER fail_decision AFTER INSERT ON input_decisions
      BEGIN
          UPDATE sessions SET history_revision = 17;
          INSERT INTO test_child VALUES (1);
      END;
      """);

    // The insert succeeds; the deferred foreign key makes COMMIT fail.
    SqliteException error = Assert.Throws<SqliteException>(() => store.UserMessages.Admit(
        sessionId, 0, submission, accepting: false, proposedExecutionId: Guid.NewGuid()));
    Assert.Equal(19, error.SqliteErrorCode);
    Assert.Null(store.UserMessages.ReadReceipt(sessionId, submission.SubmissionId));
    Assert.Equal(0L, store.Sessions.Read(sessionId)!.HistoryRevision);
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;")); // raw-sql: test_child is a fixture-owned rollback witness
    database.Execute("DROP TRIGGER fail_decision;");
    UserMessageReceipt receipt = store.UserMessages.Admit(
        sessionId, 0, submission, accepting: false, proposedExecutionId: Guid.NewGuid()).Receipt;
    Assert.Equal(MessageRefusalReason.Stopping, receipt.RefusalReason);
    Assert.Single(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
  }

  [Fact]
  public void LookupErrorsAreNotReportedAsAbsence() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "error distinction");
    Assert.Null(store.UserMessages.ReadReceipt(sessionId, submission.SubmissionId));
    database.Execute("DROP TABLE input_decisions;");
    Assert.Throws<SqliteException>(() => store.UserMessages.ReadReceipt(sessionId, submission.SubmissionId));
    store.Dispose();
    Assert.Throws<ObjectDisposedException>(() => store.UserMessages.ReadReceipt(sessionId, submission.SubmissionId));
    Assert.Throws<ObjectDisposedException>(() => store.UserMessages.Admit(
        sessionId, 0, submission, accepting: false, proposedExecutionId: Guid.NewGuid()));
  }

  [Fact]
  public void InvalidOrMissingSessionCommandsCreateNoDecision() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "valid");
    Assert.Throws<KeyNotFoundException>(() => store.UserMessages.Admit(
        Guid.NewGuid(), 0, submission, accepting: false, proposedExecutionId: Guid.NewGuid()));
    Assert.Throws<ArgumentException>(() => store.UserMessages.Admit(
        sessionId, 0, submission with { SubmissionId = Guid.Empty }, accepting: false, proposedExecutionId: Guid.NewGuid()));
    Assert.Throws<ArgumentOutOfRangeException>(() => store.UserMessages.Admit(
        sessionId, 0, submission with { ExpectedHistoryRevision = -1 }, accepting: false, proposedExecutionId: Guid.NewGuid()));
    Assert.ThrowsAny<ArgumentException>(() => store.UserMessages.Admit(
        sessionId, 0, submission with { Text = "\ud800" }, accepting: false, proposedExecutionId: Guid.NewGuid()));
    Assert.Throws<ArgumentException>(() => store.UserMessages.Admit(
        sessionId, 0, submission with { Text = new string('é', 8193) }, accepting: false, proposedExecutionId: Guid.NewGuid()));
    Assert.Empty(StoredSubmissions.Read(database));

    UserMessageReceipt boundary = store.UserMessages.Admit(sessionId, 0,
        submission with { Text = new string('é', 8192) }, accepting: false, proposedExecutionId: Guid.NewGuid()).Receipt;
    Assert.Equal(8192, boundary.Submission.Text.Length);
    Assert.Equal(MessageAcceptance.Refused, boundary.Acceptance);
    Assert.Equal(MessageRefusalReason.Stopping, boundary.RefusalReason);
    Assert.Equal(boundary, store.UserMessages.ReadReceipt(sessionId, submission.SubmissionId));
  }

  [Fact]
  public void VersionOneIdentityDatabaseMigratesWithoutChangingIdentity() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid();
    database.Execute($"""
      CREATE TABLE sessions (
          session_id TEXT NOT NULL PRIMARY KEY,
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
      ) STRICT;
      INSERT INTO sessions VALUES ('{sessionId:D}', 7);
      PRAGMA user_version = 1;
      """);
    using (SessionStore store = SessionStore.Open(database.Path)) {
      Assert.Equal(7L, store.Sessions.Read(sessionId)!.HistoryRevision);
      Assert.Null(store.UserMessages.ReadReceipt(sessionId, Guid.NewGuid()));
    }

    Assert.Equal(5L, database.Scalar("PRAGMA user_version;")); // raw-sql: the migrated schema version is the subject under test
    using SessionStore reopened = SessionStore.Open(database.Path);
    Assert.Equal(7L, reopened.Sessions.Read(sessionId)!.HistoryRevision);
  }

  [Fact]
  public void FailedVersionOneMigrationRollsBackSchemaAndKeepsIdentity() {
    using var database = new TemporaryDatabase();
    database.Execute("""
      CREATE TABLE sessions (
          session_id TEXT NOT NULL PRIMARY KEY,
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
      ) STRICT;
      INSERT INTO sessions VALUES ('00000000-0000-0000-0000-000000000001', 7);
      CREATE TABLE execution_runs (unrelated TEXT);
      INSERT INTO execution_runs VALUES ('retained');
      PRAGMA user_version = 1;
      """);

    Assert.Throws<SqliteException>(() => SessionStore.Open(database.Path));
    Assert.Equal(1L, database.Scalar("PRAGMA user_version;")); // raw-sql: rolled-back schema version is the subject under test
    Assert.Equal(7L, database.Scalar("SELECT history_revision FROM sessions;")); // raw-sql: pre-migration identity must survive the rollback
    Assert.Equal("retained", database.Scalar("SELECT unrelated FROM execution_runs;")); // raw-sql: fixture witness row must survive the rollback
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'input_decisions';")); // raw-sql: the rolled-back schema must not expose the new table
  }

  [Fact]
  public void OwnedExecutionAcceptsEightNonterminalSubmissionsAndRetainsRetryBeforeCapacityAndStop() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), execution = Guid.NewGuid();
    var first = new UserMessageSubmission(Guid.NewGuid(), 0, "first");
    var steering = new UserMessageSubmission(Guid.NewGuid(), 0, "steer \0 🙂");
    UserMessageReceipt retained;
    using (SessionStore store = SessionStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      store.UserMessages.Admit(session, 0, first, true, execution);
      Assert.NotNull(store.Executions.ClaimNextRequest(session, execution, 0));
      Assert.Equal(MessageRefusalReason.Capacity,
          store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "unowned"), true, Guid.NewGuid()).Receipt.RefusalReason);
      Assert.Equal(MessageRefusalReason.Capacity,
          store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "wrong owner"), true, Guid.NewGuid(), Guid.NewGuid()).Receipt.RefusalReason);
      var accepted = store.UserMessages.Admit(session, 0, steering, true, Guid.Empty, execution);
      retained = accepted.Receipt;
      Assert.True(accepted.NewlyAccepted);
      Assert.Equal(execution, retained.ExecutionId);
      for (int index = 2; index < 8; index++)
        Assert.True(store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, $"steering {index}"), true, Guid.NewGuid(), execution).NewlyAccepted);
      Assert.Equal(MessageRefusalReason.Capacity,
          store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "ninth"), true, Guid.NewGuid(), execution).Receipt.RefusalReason);
      var retry = store.UserMessages.Admit(session, 0, steering, true, Guid.Empty, execution);
      Assert.Equal(retained, retry.Receipt);
      Assert.False(retry.NewlyAccepted);
      store.Executions.RequestStop(session, execution, ownsExecution: true);
      Assert.Equal(retained, store.UserMessages.Admit(session, 0, steering, false, Guid.Empty).Receipt);
      Assert.Equal(7, StoredDeliveries.Read(database).Count(submission => submission.Outcome == ExecutionOutcome.Cancelled));
      Assert.Equal(8, StoredDeliveries.ReadMessages(database).Count);
      Assert.Single(StoredExecutions.Read(database));
      Assert.Equal(ExecutionOutcome.Cancelled, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
      Assert.Equal(8, StoredDeliveries.Read(database).Count(submission => submission.Outcome == ExecutionOutcome.Cancelled));
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    Assert.Equal(retained, reopened.UserMessages.Admit(session, 0, steering, true, Guid.NewGuid()).Receipt);
    Assert.Null(reopened.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Equal(8, StoredDeliveries.Read(database).Count);
    Assert.Equal(MessageAcceptance.Accepted, reopened.UserMessages.ReadReceipt(session, first.SubmissionId)!.Acceptance);
  }

  [Fact]
  public void MessageIndexFailureRollsBackSteeringReceiptAndDeliveryTogether() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "first"), true, execution);
    var steering = new UserMessageSubmission(Guid.NewGuid(), 0, "atomic steering");
    database.Execute("""
      CREATE TRIGGER reject_message BEFORE INSERT ON execution_messages
      BEGIN SELECT RAISE(ABORT, 'message write failed'); END;
      """);
    Assert.Throws<SqliteException>(() => store.UserMessages.Admit(session, 0, steering, true, Guid.NewGuid(), execution));
    Assert.Null(store.UserMessages.ReadReceipt(session, steering.SubmissionId));
    Assert.Single(StoredDeliveries.Read(database));
    Assert.Single(StoredDeliveries.ReadMessages(database));
    database.Execute("DROP TRIGGER reject_message;");
    Assert.True(store.UserMessages.Admit(session, 0, steering, true, Guid.NewGuid(), execution).NewlyAccepted);
    Assert.Single(StoredExecutions.Read(database));
  }

  [Fact]
  public async Task ConcurrentStoresRetainOneFirstDecisionAcrossDifferentRefusals() {
    using var database = new TemporaryDatabase();
    using SessionStore first = SessionStore.Open(database.Path);
    using SessionStore second = SessionStore.Open(database.Path);
    Guid sessionId = first.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    database.ForceHistoryRevision(1);
    Assert.Equal(1L, first.Sessions.Read(sessionId)!.HistoryRevision);
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "same command");
    var attempts = new[] {
            (Store: first, HistoryRevision: 0L, Accepting: false, ProposedExecutionId: Guid.NewGuid()),
            (Store: second, HistoryRevision: 0L, Accepting: true, ProposedExecutionId: Guid.NewGuid())
        };
    var start = Signal();
    Task<(UserMessageReceipt Receipt, bool NewlyAccepted)>[] tasks = attempts.Select(attempt => Task.Run(async () => {
      await start.Task;
      return attempt.Store.UserMessages.Admit(sessionId, attempt.HistoryRevision, submission,
          accepting: attempt.Accepting, proposedExecutionId: attempt.ProposedExecutionId);
    })).ToArray();
    start.SetResult();
    var results = await Task.WhenAll(tasks);
    Assert.Equal(results[0].Receipt, results[1].Receipt);
    Assert.All(results, result => Assert.False(result.NewlyAccepted));

    UserMessageReceipt persisted = first.UserMessages.ReadReceipt(sessionId, submission.SubmissionId)!;
    Assert.Equal(persisted, results[0].Receipt);
    Assert.Equal(MessageAcceptance.Refused, persisted.Acceptance);
    Assert.True(persisted.RefusalReason is MessageRefusalReason.Stopping or MessageRefusalReason.StaleRevision);
    Assert.Single(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
  }
}
