using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

// Exercise the actual old request/output relationship, not an approximation of schema four.
public sealed class SteeringMigrationTests {
  private static Guid Session { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
  private static Guid Input { get; } = Guid.Parse("00000000-0000-0000-0000-000000000002");
  private static Guid Run { get; } = Guid.Parse("00000000-0000-0000-0000-000000000003");
  private static Guid Request { get; } = Guid.Parse("00000000-0000-0000-0000-000000000004");
  private static Guid Output { get; } = Guid.Parse("00000000-0000-0000-0000-000000000005");
  private const string Original = "old \0 🙂\r\n";

  [Theory]
  [InlineData(2, ExecutionOutcome.Completed)]
  [InlineData(3, ExecutionOutcome.Completed)]
  [InlineData(3, ExecutionOutcome.Cancelled)]
  [InlineData(3, ExecutionOutcome.Failed)]
  public void UpgradePreservesOriginalEvidenceAndAddsDeliveryAndMessageIdentities(int version, ExecutionOutcome outcome) {
    using var database = new TemporaryDatabase();
    CreateLegacy(database, version, outcome);
    using (SessionStore store = SessionStore.Open(database.Path)) {
      InputReceipt receipt = store.Inputs.ReadReceipt(Session, Input)!;
      Assert.Equal(new InputCommand(Input, 7, Original), receipt.Command);
      Assert.Equal(InputDecision.Accepted, receipt.Decision);
      Assert.Equal(Run, receipt.RunId);
      Assert.Equal(outcome, store.Runs.ReadSettlement(Session, Run)!.Outcome);
      Assert.Null(store.Runs.ClaimNextRequest(Session, Run, 7));
      StoredRun run = Assert.Single(StoredRuns.Read(database));
      Assert.Equal(Run, run.RunId);
      Assert.Equal(Input, run.InputId);
      var request = Assert.Single(StoredRuns.ReadRequests(database));
      Assert.Equal(Request, request.RequestId);
      Assert.Equal(Output, Assert.Single(StoredOutput.Read(database)).ResponseId);
      Assert.Equal("old error", request.Error);
      Assert.Equal(outcome, request.Outcome);
      Assert.Equal(new byte[] { 0, 240, 159, 153, 130 }, Assert.Single(StoredOutput.Read(database)).Bytes);
      Assert.Equal(0L, Assert.Single(StoredDeliveries.ReadRequestOrder(database)).Index);
      StoredDelivery delivery = Assert.Single(StoredDeliveries.Read(database));
      Assert.Equal(Request, delivery.RequestId);
      Assert.Equal(outcome, delivery.Outcome);
      var messages = StoredDeliveries.ReadMessages(database);
      Assert.Equal(2, messages.Count);
      Assert.Equal(Input, Assert.Single(messages, message => message.Role == ChatRole.User).MessageId);
      Assert.Equal(Output, Assert.Single(messages, message => message.Role == ChatRole.Assistant).MessageId);
      Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: migration must preserve referential integrity
      Assert.Equal(4L, database.Scalar("PRAGMA user_version;")); // raw-sql: the upgraded schema version is under test
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    Assert.Equal(outcome, reopened.Runs.ReadSettlement(Session, Run)!.Outcome);
    Assert.Equal(2, StoredDeliveries.ReadMessages(database).Count);
    Assert.Single(StoredDeliveries.Read(database));
    AssertForeignKeysRemainEnforced(database);
  }

  [Fact]
  public void RetainedNonterminalRequestIsNotReplayedOrOwnedAfterUpgradeAndReopen() {
    using var database = new TemporaryDatabase();
    CreateLegacy(database, 3, outcome: null);
    using (SessionStore store = SessionStore.Open(database.Path)) {
      Assert.Null(store.Runs.ReadSettlement(Session, Run));
      Assert.Null(store.Runs.ClaimNextRequest(Session, Run, 7));
      Assert.Equal(InputRefusalReason.Capacity, store.Inputs.Admit(Session, 7,
          new(Guid.NewGuid(), 7, "unowned"), true, Guid.NewGuid()).Receipt.RefusalReason);
      Assert.Null(Assert.Single(StoredDeliveries.Read(database)).Outcome);
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    Assert.Null(reopened.Runs.ClaimNextRequest(Session, Run, 7));
    Assert.Equal(Request, Assert.Single(StoredRuns.ReadRequests(database)).RequestId);
    Assert.False(reopened.Inputs.Admit(Session, 7, new(Input, 7, Original), false, Guid.Empty).NewlyAccepted);
  }

  [Fact]
  public void LateMigrationFailureRollsBackRebuildBytesIndexesAndVersion() {
    using var database = new TemporaryDatabase();
    CreateLegacy(database, 3, ExecutionOutcome.Failed);
    database.Execute("CREATE TABLE execution_messages (witness TEXT); INSERT INTO execution_messages VALUES ('retained');");
    Assert.Throws<SqliteException>(() => SessionStore.Open(database.Path));
    Assert.Equal(3L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed migration must retain the old schema version
    Assert.Equal("retained", database.Scalar("SELECT witness FROM execution_messages;")); // raw-sql: fixture-owned schema collision witness must survive rollback
    Assert.Equal("00F09F9982", database.Scalar("SELECT hex(text_utf8) FROM execution_output;")); // raw-sql: failed rebuild must retain original output bytes in the old schema
    Assert.Equal(Request, Assert.Single(StoredRuns.ReadRequests(database)).RequestId);
    Assert.Equal(ExecutionOutcome.Failed, Assert.Single(StoredRuns.ReadRequests(database)).Outcome);
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('execution_inputs', 'execution_requests_v4', 'execution_output_v4', 'one_unfinished_request');")); // raw-sql: failed migration must roll back new tables and indexes
    AssertForeignKeysRemainEnforced(database);
    database.Execute("DROP TABLE execution_messages;");
    using SessionStore recovered = SessionStore.Open(database.Path);
    Assert.Equal(ExecutionOutcome.Failed, recovered.Runs.ReadSettlement(Session, Run)!.Outcome);
  }

  private static void AssertForeignKeysRemainEnforced(TemporaryDatabase database) {
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
      DataSource = database.Path, Pooling = false
    }.ToString());
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "PRAGMA foreign_keys = ON;";
    command.ExecuteNonQuery();
    command.CommandText = "INSERT INTO execution_output VALUES ('missing-request', 0, X'', 1);";
    Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    command.CommandText = "PRAGMA foreign_key_check;";
    Assert.Null(command.ExecuteScalar()); // raw-sql: migration and rollback must leave no foreign-key violations
  }

  private static void CreateLegacy(TemporaryDatabase database, int version, ExecutionOutcome? outcome) {
    database.Execute($"""
      PRAGMA foreign_keys = ON;
      CREATE TABLE sessions (
          session_id TEXT NOT NULL PRIMARY KEY,
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
      ) STRICT;
      CREATE TABLE input_decisions (
          session_id TEXT NOT NULL REFERENCES sessions(session_id), input_id TEXT NOT NULL,
          expected_history_revision INTEGER NOT NULL CHECK (expected_history_revision >= 0),
          original_text_utf8 BLOB NOT NULL CHECK (length(original_text_utf8) <= 16384),
          decision INTEGER NOT NULL CHECK (decision IN (1, 2)), run_id TEXT,
          refusal_reason INTEGER CHECK (refusal_reason IN (1, 2, 3)), PRIMARY KEY (session_id, input_id),
          CHECK ((decision = 1 AND run_id IS NOT NULL AND run_id <> '00000000-0000-0000-0000-000000000000'
              AND refusal_reason IS NULL) OR (decision = 2 AND run_id IS NULL AND refusal_reason IS NOT NULL))
      ) STRICT;
      CREATE TABLE execution_runs (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL REFERENCES sessions(session_id), run_id TEXT NOT NULL UNIQUE,
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0), input_id TEXT NOT NULL,
          stopping INTEGER NOT NULL DEFAULT 0 CHECK (stopping IN (0, 1)), outcome INTEGER CHECK (outcome IN (0, 1, 2)),
          UNIQUE (session_id, input_id), FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id)
      ) STRICT;
      CREATE UNIQUE INDEX one_active_execution ON execution_runs(session_id) WHERE outcome IS NULL;
      CREATE TABLE execution_requests (
          request_id TEXT PRIMARY KEY NOT NULL, run_id TEXT NOT NULL UNIQUE REFERENCES execution_runs(run_id),
          output_id TEXT, next_batch_index INTEGER NOT NULL DEFAULT 0 CHECK (next_batch_index >= 0),
          output_final INTEGER NOT NULL DEFAULT 0 CHECK (output_final IN (0, 1)),
          outcome INTEGER CHECK (outcome IN (0, 1, 2)), error TEXT
      ) STRICT;
      CREATE TABLE execution_output (
          request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
          batch_index INTEGER NOT NULL CHECK (batch_index >= 0),
          text_utf8 BLOB NOT NULL CHECK (length(text_utf8) <= 16384),
          is_final INTEGER NOT NULL CHECK (is_final IN (0, 1)), PRIMARY KEY (request_id, batch_index)
      ) STRICT;
      INSERT INTO sessions VALUES ('{Session:D}', 7);
      INSERT INTO input_decisions VALUES ('{Session:D}', '{Input:D}', 7,
          X'{Convert.ToHexString(StorageText.StrictUtf8.GetBytes(Original))}', 1, '{Run:D}', NULL);
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
      VALUES ('{Session:D}', '{Run:D}', 7, '{Input:D}', {OutcomeSql(outcome)});
      INSERT INTO execution_requests VALUES ('{Request:D}', '{Run:D}', '{Output:D}', 1, 1, {OutcomeSql(outcome)}, 'old error');
      INSERT INTO execution_output VALUES ('{Request:D}', 0, X'00F09F9982', 1);
      PRAGMA user_version = {version};
      """);
    if (version == 3) {
      database.Execute("""
        CREATE INDEX execution_runs_history_order ON execution_runs(session_id, history_revision, ordinal DESC);
        CREATE INDEX execution_runs_terminal_order ON execution_runs(session_id, ordinal DESC) WHERE outcome IS NOT NULL;
        """);
    }
  }

  private static string OutcomeSql(ExecutionOutcome? outcome) => outcome is { } value ? ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";
}
