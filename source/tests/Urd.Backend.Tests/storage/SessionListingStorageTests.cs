using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class SessionListingStorageTests {
  private const string Original = "old\0 🙂";

  [Fact]
  public void KeysetPagesSurviveInsertionDeletedBoundaryAndReopen() {
    using var database = new TemporaryDatabase();
    Guid[] created = Enumerable.Range(1, 125).Reverse().Select(Id).ToArray();
    SessionIdPage first;
    Guid inserted = Id(200);
    using (UrdStore store = UrdStore.Open(database.Path)) {
      foreach (Guid id in created)
        store.Sessions.CreatePrimary(id);
      first = store.Sessions.ListPrimaryIds(null);
      Assert.Equal(created.Reverse().Take(60), first.SessionIds);
      Assert.Equal("66", first.NextCursor);
      store.Sessions.CreatePrimary(inserted);
      database.Execute($"PRAGMA foreign_keys = ON; DELETE FROM sessions WHERE session_id = '{first.SessionIds[^1]:D}';");
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    SessionIdPage second = reopened.Sessions.ListPrimaryIds(first.NextCursor);
    SessionIdPage third = reopened.Sessions.ListPrimaryIds(second.NextCursor);
    Assert.Equal(created.Reverse(), first.SessionIds.Concat(second.SessionIds).Concat(third.SessionIds));
    Assert.Equal("6", second.NextCursor);
    Assert.Equal(5, third.SessionIds.Count);
    Assert.Null(third.NextCursor);
    Assert.Equal(inserted, reopened.Sessions.ListPrimaryIds(null).SessionIds[0]);
  }

  [Fact]
  public void GenuineSchemaFourUpgradeBackfillsCreationOrderAndRollsBackAtomically() {
    using var database = new TemporaryDatabase();
    CreateSchemaFour(database);

    // Exhaust the new table's allocator so migration fails after creating it and starting the backfill.
    database.Execute("INSERT INTO sqlite_sequence (name, seq) VALUES ('session_creation', 9223372036854775807);");
    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(4L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed migration preserves its starting version
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'session_creation';")); // raw-sql: rolled-back migration must not leave its table
    Assert.Single(StoredExecutions.Read(database));
    Assert.Single(StoredLlmResponses.Read(database));
    database.Execute("DELETE FROM sqlite_sequence WHERE name = 'session_creation';");

    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Equal(new[] { Id(3), Id(2), Id(1) }, store.Sessions.ListPrimaryIds(null).SessionIds);
      Assert.Equal(Original, store.UserMessages.ReadReceipt(Id(1), Id(11))!.Submission.Text);
      Assert.Equal(ExecutionOutcome.Failed, store.Executions.ReadSettlement(Id(1), Id(12))!.Outcome);
      Assert.Equal(new[] { Original, "\0🙂" }, store.Snapshots.ReadStored(Id(1)).Messages.Select(message => message.Preview));
      store.Sessions.CreatePrimary(Id(4));
      Assert.Equal(new[] { Id(4), Id(3), Id(2), Id(1) }, store.Sessions.ListPrimaryIds(null).SessionIds);
    }

    Assert.Equal(9L, database.Scalar("PRAGMA user_version;")); // raw-sql: verify the committed migration version
    Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: verify migrated referential integrity
  }

  private static Guid Id(int value) => Guid.Parse($"{value:x8}-0000-0000-0000-000000000000");

  // Frozen schema-four DDL. This fixture does not derive the old version from the current initializer.
  private static void CreateSchemaFour(TemporaryDatabase database) => database.Execute($"""
    PRAGMA foreign_keys = ON;
    CREATE TABLE sessions (
        session_id TEXT NOT NULL PRIMARY KEY,
        history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
    ) STRICT;
    CREATE TABLE input_decisions (
        session_id TEXT NOT NULL REFERENCES sessions(session_id),
        input_id TEXT NOT NULL,
        expected_history_revision INTEGER NOT NULL CHECK (expected_history_revision >= 0),
        original_text_utf8 BLOB NOT NULL CHECK (length(original_text_utf8) <= 16384),
        decision INTEGER NOT NULL CHECK (decision IN (1, 2)),
        run_id TEXT,
        refusal_reason INTEGER CHECK (refusal_reason IN (1, 2, 3)),
        PRIMARY KEY (session_id, input_id),
        CHECK (
            (decision = 1 AND run_id IS NOT NULL AND run_id <> '00000000-0000-0000-0000-000000000000'
                AND refusal_reason IS NULL)
            OR (decision = 2 AND run_id IS NULL AND refusal_reason IS NOT NULL)
        )
    ) STRICT;
    CREATE TABLE execution_runs (
        ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL REFERENCES sessions(session_id),
        run_id TEXT NOT NULL UNIQUE,
        history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
        input_id TEXT NOT NULL,
        stopping INTEGER NOT NULL DEFAULT 0 CHECK (stopping IN (0, 1)),
        outcome INTEGER CHECK (outcome IN (0, 1, 2)),
        UNIQUE (session_id, input_id),
        FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id)
    ) STRICT;
    CREATE UNIQUE INDEX one_active_execution ON execution_runs(session_id) WHERE outcome IS NULL;
    CREATE INDEX execution_runs_history_order ON execution_runs(session_id, history_revision, ordinal DESC);
    CREATE INDEX execution_runs_terminal_order ON execution_runs(session_id, ordinal DESC) WHERE outcome IS NOT NULL;
    CREATE TABLE execution_requests (
        request_id TEXT PRIMARY KEY NOT NULL,
        run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
        request_index INTEGER NOT NULL DEFAULT 0 CHECK (request_index >= 0),
        output_id TEXT,
        next_batch_index INTEGER NOT NULL DEFAULT 0 CHECK (next_batch_index >= 0),
        output_final INTEGER NOT NULL DEFAULT 0 CHECK (output_final IN (0, 1)),
        outcome INTEGER CHECK (outcome IN (0, 1, 2)),
        error TEXT,
        UNIQUE (run_id, request_index)
    ) STRICT;
    CREATE UNIQUE INDEX one_unfinished_request ON execution_requests(run_id) WHERE outcome IS NULL;
    CREATE TABLE execution_output (
        request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
        batch_index INTEGER NOT NULL CHECK (batch_index >= 0),
        text_utf8 BLOB NOT NULL CHECK (length(text_utf8) <= 16384),
        is_final INTEGER NOT NULL CHECK (is_final IN (0, 1)),
        PRIMARY KEY (request_id, batch_index)
    ) STRICT;
    CREATE TABLE execution_inputs (
        ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL REFERENCES sessions(session_id),
        input_id TEXT NOT NULL,
        run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
        request_id TEXT REFERENCES execution_requests(request_id),
        outcome INTEGER CHECK (outcome IN (0, 1, 2)),
        UNIQUE (session_id, input_id),
        FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id)
    ) STRICT;
    CREATE INDEX pending_execution_inputs ON execution_inputs(run_id, ordinal) WHERE request_id IS NULL AND outcome IS NULL;
    CREATE INDEX nonterminal_execution_inputs ON execution_inputs(run_id) WHERE outcome IS NULL;
    CREATE INDEX execution_inputs_request_order ON execution_inputs(request_id, ordinal) WHERE request_id IS NOT NULL;
    CREATE TABLE execution_messages (
        ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL REFERENCES sessions(session_id),
        history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
        run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
        role INTEGER NOT NULL CHECK (role IN (0, 1)),
        message_id TEXT NOT NULL,
        input_id TEXT,
        request_id TEXT REFERENCES execution_requests(request_id),
        UNIQUE (run_id, role, message_id),
        FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id),
        CHECK ((role = 0 AND input_id IS NOT NULL AND request_id IS NULL)
            OR (role = 1 AND input_id IS NULL AND request_id IS NOT NULL))
    ) STRICT;
    CREATE INDEX execution_messages_history_order ON execution_messages(session_id, history_revision, ordinal DESC);
    CREATE TABLE execution_tools (
        run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
        request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
        call_index INTEGER NOT NULL CHECK (call_index >= 0),
        call_id TEXT NOT NULL,
        name TEXT NOT NULL,
        arguments_json TEXT NOT NULL CHECK (length(CAST(arguments_json AS BLOB)) <= 16384),
        started INTEGER NOT NULL DEFAULT 0 CHECK (started IN (0, 1)),
        outcome INTEGER CHECK (outcome IN (0, 1, 2)),
        result_json TEXT CHECK (length(CAST(result_json AS BLOB)) <= 16384),
        error TEXT,
        PRIMARY KEY (request_id, call_id),
        UNIQUE (request_id, call_index),
        UNIQUE (run_id, call_id)
    ) STRICT;
    INSERT INTO sessions VALUES ('{Id(3):D}', 0), ('{Id(1):D}', 7), ('{Id(2):D}', 0);
    INSERT INTO input_decisions VALUES ('{Id(1):D}', '{Id(11):D}', 7,
        X'{Convert.ToHexString(StorageText.StrictUtf8.GetBytes(Original))}', 1, '{Id(12):D}', NULL);
    INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, stopping, outcome)
        VALUES ('{Id(1):D}', '{Id(12):D}', 7, '{Id(11):D}', 1, 2);
    INSERT INTO execution_requests VALUES ('{Id(13):D}', '{Id(12):D}', 0, '{Id(14):D}', 1, 1, 2, 'old error');
    INSERT INTO execution_output VALUES ('{Id(13):D}', 0, X'00F09F9982', 1);
    INSERT INTO execution_inputs (session_id, input_id, run_id, request_id, outcome)
        VALUES ('{Id(1):D}', '{Id(11):D}', '{Id(12):D}', '{Id(13):D}', 2);
    INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id, request_id)
        VALUES ('{Id(1):D}', 7, '{Id(12):D}', 0, '{Id(11):D}', '{Id(11):D}', NULL),
            ('{Id(1):D}', 7, '{Id(12):D}', 1, '{Id(14):D}', NULL, '{Id(13):D}');
    PRAGMA user_version = 4;
    """);
}
