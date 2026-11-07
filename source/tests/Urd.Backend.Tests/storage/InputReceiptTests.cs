using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Storage;

// Durable input decisions: exact retries, conflicting originals, rollback and version-one migration.
public sealed class InputReceiptTests {
  [Fact]
  public void LostRefusalReplyDoesNotPermitAcceptanceAfterReopen() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid();
    Guid firstProposedRunId = Guid.NewGuid(), retryProposedRunId = Guid.NewGuid();
    var command = new InputCommand(Guid.NewGuid(), 0, "original \r\n e\u0301 \0 🙂");
    using (SessionStore store = SessionStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(sessionId);
      // Deliberately discard the committed reply, as an uncertain caller would.
      store.Inputs.Admit(sessionId, 0, command, accepting: false, proposedRunId: firstProposedRunId);
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    var retry = reopened.Inputs.Admit(sessionId, 0, command, accepting: true, proposedRunId: retryProposedRunId);
    InputReceipt receipt = reopened.Inputs.ReadReceipt(sessionId, command.InputId)!;
    Assert.Equal(sessionId, receipt.SessionId);
    Assert.Equal(command, receipt.Command);
    Assert.Equal(InputDecision.Refused, receipt.Decision);
    Assert.Null(receipt.RunId);
    Assert.Equal(InputRefusalReason.Stopping, receipt.RefusalReason);
    Assert.Equal(receipt, retry.Receipt);
    Assert.False(retry.NewlyAccepted);
    Assert.Single(StoredInputs.Read(database));
    Assert.Empty(StoredRuns.Read(database));
    Assert.Equal(0L, reopened.Sessions.Read(sessionId)!.HistoryRevision);
  }

  [Fact]
  public void DelayedStaleAdmissionCannotReplaceFirstRefusal() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var command = new InputCommand(Guid.NewGuid(), 0, "delayed original");
    InputReceipt firstReceipt = store.Inputs.Admit(
        sessionId, 0, command, accepting: false, proposedRunId: Guid.NewGuid()).Receipt;
    database.ForceHistoryRevision(1);

    var delayed = store.Inputs.Admit(
        sessionId, 0, command, accepting: true, proposedRunId: Guid.NewGuid());
    Assert.Equal(firstReceipt, delayed.Receipt);
    Assert.False(delayed.NewlyAccepted);
    Assert.Equal(firstReceipt, store.Inputs.ReadReceipt(sessionId, command.InputId));
    Assert.Equal(InputRefusalReason.Stopping, firstReceipt.RefusalReason);
    Assert.Equal(1L, store.Sessions.Read(sessionId)!.HistoryRevision);
    Assert.Single(StoredInputs.Read(database));
    Assert.Empty(StoredRuns.Read(database));
  }

  [Fact]
  public void ConflictingTextOrRevisionNeverResolvesOrOverwritesOriginal() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var command = new InputCommand(Guid.NewGuid(), 0, "e\u0301\r\n");
    InputReceipt original = store.Inputs.Admit(
        sessionId, 0, command, accepting: false, proposedRunId: Guid.NewGuid()).Receipt;
    InputCommand[] conflicts =
    [
        command with { Text = "é\r\n" },
            command with { Text = "e\u0301\n" },
            command with { Text = command.Text + " " },
            command with { ExpectedHistoryRevision = 1 }
    ];

    Assert.All(conflicts, conflict => {
      InputConflictException failure = Assert.Throws<InputConflictException>(() =>
          store.Inputs.Admit(sessionId, 0, conflict, accepting: true, proposedRunId: Guid.NewGuid()));
      Assert.Equal(command.InputId, failure.InputId);
    });

    Assert.Equal(original, store.Inputs.ReadReceipt(sessionId, command.InputId));
    Assert.Single(StoredInputs.Read(database));
    Assert.Empty(StoredRuns.Read(database));
  }

  [Fact]
  public void InputIdentityIsScopedToSessionAndMissingLookupDoesNotInsert() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid first = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid second = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var command = new InputCommand(Guid.NewGuid(), 0, "first session");
    InputReceipt firstReceipt = store.Inputs.Admit(
        first, 0, command, accepting: false, proposedRunId: Guid.NewGuid()).Receipt;
    Assert.Null(store.Inputs.ReadReceipt(second, command.InputId));
    Assert.Null(store.Inputs.ReadReceipt(Guid.NewGuid(), command.InputId));
    Guid secondRunId = Guid.NewGuid();
    InputReceipt secondReceipt = store.Inputs.Admit(second, 0, command with { Text = "second session" },
        accepting: true, proposedRunId: secondRunId).Receipt;
    Assert.Equal(InputDecision.Refused, firstReceipt.Decision);
    Assert.Equal(InputDecision.Accepted, secondReceipt.Decision);
    Assert.Equal(secondRunId, secondReceipt.RunId);
    Assert.NotEqual(firstReceipt, secondReceipt);
    Assert.Equal(2, StoredInputs.Read(database).Count);
    StoredRun storedRun = Assert.Single(StoredRuns.Read(database));
    Assert.Equal(second, storedRun.SessionId);
    Assert.Equal(command.InputId, storedRun.InputId);
  }

  [Fact]
  public void TransactionFailureLeavesNoPartialDecisionAndAllowsRetry() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var command = new InputCommand(Guid.NewGuid(), 0, "rolled back");
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
    SqliteException error = Assert.Throws<SqliteException>(() => store.Inputs.Admit(
        sessionId, 0, command, accepting: false, proposedRunId: Guid.NewGuid()));
    Assert.Equal(19, error.SqliteErrorCode);
    Assert.Null(store.Inputs.ReadReceipt(sessionId, command.InputId));
    Assert.Equal(0L, store.Sessions.Read(sessionId)!.HistoryRevision);
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;")); // raw-sql: test_child is a fixture-owned rollback witness
    database.Execute("DROP TRIGGER fail_decision;");
    InputReceipt receipt = store.Inputs.Admit(
        sessionId, 0, command, accepting: false, proposedRunId: Guid.NewGuid()).Receipt;
    Assert.Equal(InputRefusalReason.Stopping, receipt.RefusalReason);
    Assert.Single(StoredInputs.Read(database));
    Assert.Empty(StoredRuns.Read(database));
  }

  [Fact]
  public void LookupErrorsAreNotReportedAsAbsence() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var command = new InputCommand(Guid.NewGuid(), 0, "error distinction");
    Assert.Null(store.Inputs.ReadReceipt(sessionId, command.InputId));
    database.Execute("DROP TABLE input_decisions;");
    Assert.Throws<SqliteException>(() => store.Inputs.ReadReceipt(sessionId, command.InputId));
    store.Dispose();
    Assert.Throws<ObjectDisposedException>(() => store.Inputs.ReadReceipt(sessionId, command.InputId));
    Assert.Throws<ObjectDisposedException>(() => store.Inputs.Admit(
        sessionId, 0, command, accepting: false, proposedRunId: Guid.NewGuid()));
  }

  [Fact]
  public void InvalidOrMissingSessionCommandsCreateNoDecision() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var command = new InputCommand(Guid.NewGuid(), 0, "valid");
    Assert.Throws<KeyNotFoundException>(() => store.Inputs.Admit(
        Guid.NewGuid(), 0, command, accepting: false, proposedRunId: Guid.NewGuid()));
    Assert.Throws<ArgumentException>(() => store.Inputs.Admit(
        sessionId, 0, command with { InputId = Guid.Empty }, accepting: false, proposedRunId: Guid.NewGuid()));
    Assert.Throws<ArgumentOutOfRangeException>(() => store.Inputs.Admit(
        sessionId, 0, command with { ExpectedHistoryRevision = -1 }, accepting: false, proposedRunId: Guid.NewGuid()));
    Assert.ThrowsAny<ArgumentException>(() => store.Inputs.Admit(
        sessionId, 0, command with { Text = "\ud800" }, accepting: false, proposedRunId: Guid.NewGuid()));
    Assert.Throws<ArgumentException>(() => store.Inputs.Admit(
        sessionId, 0, command with { Text = new string('é', 8193) }, accepting: false, proposedRunId: Guid.NewGuid()));
    Assert.Empty(StoredInputs.Read(database));

    InputReceipt boundary = store.Inputs.Admit(sessionId, 0,
        command with { Text = new string('é', 8192) }, accepting: false, proposedRunId: Guid.NewGuid()).Receipt;
    Assert.Equal(8192, boundary.Command.Text.Length);
    Assert.Equal(InputDecision.Refused, boundary.Decision);
    Assert.Equal(InputRefusalReason.Stopping, boundary.RefusalReason);
    Assert.Equal(boundary, store.Inputs.ReadReceipt(sessionId, command.InputId));
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
      Assert.Null(store.Inputs.ReadReceipt(sessionId, Guid.NewGuid()));
    }

    Assert.Equal(3L, database.Scalar("PRAGMA user_version;")); // raw-sql: the migrated schema version is the subject under test
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
  public async Task ConcurrentStoresRetainOneFirstDecisionAcrossDifferentRefusals() {
    using var database = new TemporaryDatabase();
    using SessionStore first = SessionStore.Open(database.Path);
    using SessionStore second = SessionStore.Open(database.Path);
    Guid sessionId = first.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    database.ForceHistoryRevision(1);
    Assert.Equal(1L, first.Sessions.Read(sessionId)!.HistoryRevision);
    var command = new InputCommand(Guid.NewGuid(), 0, "same command");
    var attempts = new[] {
            (Store: first, HistoryRevision: 0L, Accepting: false, ProposedRunId: Guid.NewGuid()),
            (Store: second, HistoryRevision: 0L, Accepting: true, ProposedRunId: Guid.NewGuid())
        };
    var start = Signal();
    Task<(InputReceipt Receipt, bool NewlyAccepted)>[] tasks = attempts.Select(attempt => Task.Run(async () => {
      await start.Task;
      return attempt.Store.Inputs.Admit(sessionId, attempt.HistoryRevision, command,
          accepting: attempt.Accepting, proposedRunId: attempt.ProposedRunId);
    })).ToArray();
    start.SetResult();
    var results = await Task.WhenAll(tasks);
    Assert.Equal(results[0].Receipt, results[1].Receipt);
    Assert.All(results, result => Assert.False(result.NewlyAccepted));

    InputReceipt persisted = first.Inputs.ReadReceipt(sessionId, command.InputId)!;
    Assert.Equal(persisted, results[0].Receipt);
    Assert.Equal(InputDecision.Refused, persisted.Decision);
    Assert.True(persisted.RefusalReason is InputRefusalReason.Stopping or InputRefusalReason.StaleRevision);
    Assert.Single(StoredInputs.Read(database));
    Assert.Empty(StoredRuns.Read(database));
  }
}
