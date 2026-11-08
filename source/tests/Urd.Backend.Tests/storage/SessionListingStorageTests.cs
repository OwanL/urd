using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

// Creation-ordered discovery: bounded keysets, atomic identities and genuine schema-four upgrades.
public sealed class SessionListingStorageTests {
  private const string Original = "old\0 🙂";
  private const string ArgumentsJson = """{"value":1}""";
  private const string ResultJson = """{"error":"old"}""";

  [Fact]
  public void EmptyAndExactPageSizeHaveNoContinuationAndCursorsAreStrictPositiveDecimals() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    SessionIdPage empty = store.Sessions.ListPrimaryIds(null);
    Assert.Empty(empty.SessionIds);
    Assert.Null(empty.NextCursor);
    foreach (string cursor in new[] { "", " ", "0", "-1", "+1", "1 ", " 1", "1.0", "1e2", "abc", "9223372036854775808" })
      Assert.Throws<ArgumentException>(() => store.Sessions.ListPrimaryIds(cursor));

    store.Sessions.CreatePrimary(Id(60));
    Assert.Equal(new[] { Id(60) }, store.Sessions.ListPrimaryIds(null).SessionIds);
    foreach (int value in Enumerable.Range(1, 59).Reverse())
      store.Sessions.CreatePrimary(Id(value));

    SessionIdPage full = store.Sessions.ListPrimaryIds(null);
    Assert.Equal(Enumerable.Range(1, 60).Select(Id), full.SessionIds);
    Assert.Null(full.NextCursor);
    Assert.Empty(store.Sessions.ListPrimaryIds("1").SessionIds);
    Assert.Equal(full.SessionIds, store.Sessions.ListPrimaryIds("9223372036854775807").SessionIds);
  }

  [Fact]
  public void KeysetsSurviveInsertionDeletedBoundaryAndReopenWithoutSkippingOrRepeatingIds() {
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
      // The boundary is an empty fixture, not a new public deletion API.
      database.Execute($"PRAGMA foreign_keys = ON; DELETE FROM sessions WHERE session_id = '{first.SessionIds[^1]:D}';");
      Assert.DoesNotContain(StoredSessions.ReadCreation(database), row => row.Id == first.SessionIds[^1]);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    SessionIdPage second = reopened.Sessions.ListPrimaryIds(first.NextCursor);
    SessionIdPage third = reopened.Sessions.ListPrimaryIds(second.NextCursor);
    Assert.Equal(60, second.SessionIds.Count);
    Assert.Equal("6", second.NextCursor);
    Assert.Equal(5, third.SessionIds.Count);
    Assert.Null(third.NextCursor);
    Guid[] listed = first.SessionIds.Concat(second.SessionIds).Concat(third.SessionIds).ToArray();
    Assert.Equal(created.Reverse(), listed);
    Assert.Equal(125, listed.Distinct().Count());
    Assert.Equal(inserted, reopened.Sessions.ListPrimaryIds(null).SessionIds[0]);
    Assert.Empty(StoredExecutions.Read(database));
    Assert.Empty(StoredLlmResponses.Read(database));
  }

  [Fact]
  public void CreationOrdinalsAreNotReusedAfterDeletingHighestOrAllRows() {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(Id(1));
      long first = Assert.Single(StoredSessions.ReadCreation(database)).Ordinal;
      store.Sessions.CreatePrimary(Id(2));
      long highest = StoredSessions.ReadCreation(database)[^1].Ordinal;
      database.Execute($"PRAGMA foreign_keys = ON; DELETE FROM sessions WHERE session_id = '{Id(2):D}';");
      store.Sessions.CreatePrimary(Id(3));
      Assert.True(StoredSessions.ReadCreation(database)[^1].Ordinal > highest);
      Assert.Equal(new[] { Id(1) }, store.Sessions.ListPrimaryIds(highest.ToString(System.Globalization.CultureInfo.InvariantCulture)).SessionIds);
      Assert.Empty(store.Sessions.ListPrimaryIds(first.ToString(System.Globalization.CultureInfo.InvariantCulture)).SessionIds);
    }

    long previous = StoredSessions.ReadCreation(database)[^1].Ordinal;
    database.Execute("PRAGMA foreign_keys = ON; DELETE FROM sessions;");
    Assert.Empty(StoredSessions.ReadCreation(database));
    using UrdStore reopened = UrdStore.Open(database.Path);
    reopened.Sessions.CreatePrimary(Id(4));
    Assert.True(Assert.Single(StoredSessions.ReadCreation(database)).Ordinal > previous);
    Assert.Empty(reopened.Sessions.ListPrimaryIds(previous.ToString(System.Globalization.CultureInfo.InvariantCulture)).SessionIds);
    Assert.Equal(new[] { Id(4) }, reopened.Sessions.ListPrimaryIds(null).SessionIds);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void OrderingInsertAndCommitFailuresRollBackIdentityAndDuplicatePreservesOrder(bool deferredFailure) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    store.Sessions.CreatePrimary(Id(1));
    database.ForceHistoryRevision(7);
    StoredSessionCreation original = Assert.Single(StoredSessions.ReadCreation(database));
    database.Execute(deferredFailure ? """
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_creation AFTER INSERT ON session_creation
      BEGIN INSERT INTO test_child VALUES (1); END;
      """ : """
      CREATE TRIGGER fail_creation BEFORE INSERT ON session_creation
      BEGIN SELECT RAISE(ABORT, 'ordering unavailable'); END;
      """);

    Assert.Throws<SqliteException>(() => store.Sessions.CreatePrimary(Id(2)));
    Assert.Null(store.Sessions.Read(Id(2)));
    Assert.Equal(new StoredSessionRow(Id(1), 7), Assert.Single(StoredSessions.Read(database)));
    Assert.Equal(original, Assert.Single(StoredSessions.ReadCreation(database)));
    if (deferredFailure)
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;")); // raw-sql: fixture-owned deferred-commit rollback witness

    database.Execute("DROP TRIGGER fail_creation;");
    Assert.Throws<SqliteException>(() => store.Sessions.CreatePrimary(Id(1)));
    Assert.Equal(new StoredSessionRow(Id(1), 7), Assert.Single(StoredSessions.Read(database)));
    Assert.Equal(original, Assert.Single(StoredSessions.ReadCreation(database)));
    Assert.Equal(Id(2), store.Sessions.CreatePrimary(Id(2)).Id);
    Assert.Equal(new[] { Id(2), Id(1) }, store.Sessions.ListPrimaryIds(null).SessionIds);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void SchemaFourUpgradePreservesEvidenceAndUsesBinaryIdFallbackWithTransactionalRollback(bool failMigration) {
    using var database = new TemporaryDatabase();
    CreateSchemaFour(database);
    object? root = database.Scalar("SELECT rootpage FROM sqlite_schema WHERE name = 'sessions';"); // raw-sql: migration must not rebuild the foreign-key root
    if (failMigration) {
      // Exhaust this table's allocator so backfill fails after CREATE TABLE, not before any writes.
      database.Execute("INSERT INTO sqlite_sequence (name, seq) VALUES ('session_creation', 9223372036854775807);");
      Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
      Assert.Equal(4L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed upgrade retains the real schema-four version
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'session_creation';")); // raw-sql: failed backfill rolls back its new table
      Assert.Equal(long.MaxValue, database.Scalar("SELECT seq FROM sqlite_sequence WHERE name = 'session_creation';")); // raw-sql: fixture-owned allocator fault survives rollback
      AssertLegacyEvidence(database);
      database.Execute("DELETE FROM sqlite_sequence WHERE name = 'session_creation';");
    }

    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Equal(new[] { Id(3), Id(2), Id(1) }, store.Sessions.ListPrimaryIds(null).SessionIds);
      Assert.Equal(new long[] { 1, 2, 3 }, StoredSessions.ReadCreation(database).Select(row => row.Ordinal));
      Assert.Equal(new UserMessageSubmission(Id(11), 7, Original), store.UserMessages.ReadReceipt(Id(1), Id(11))!.Submission);
      Assert.Equal(ExecutionOutcome.Failed, store.Executions.ReadSettlement(Id(1), Id(12))!.Outcome);
      AssertLegacyEvidence(database);
      store.Sessions.CreatePrimary(Id(4));
      Assert.Equal(new[] { Id(4), Id(3), Id(2), Id(1) }, store.Sessions.ListPrimaryIds(null).SessionIds);
    }

    Assert.Equal(6L, database.Scalar("PRAGMA user_version;")); // raw-sql: successful schema-four upgrade applies the current schema
    Assert.Equal(root, database.Scalar("SELECT rootpage FROM sqlite_schema WHERE name = 'sessions';")); // raw-sql: existing root table was not rebuilt
    Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: original execution evidence and new metadata retain valid foreign keys
    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(new[] { Id(4), Id(3), Id(2), Id(1) }, reopened.Sessions.ListPrimaryIds(null).SessionIds);
    AssertLegacyEvidence(database, hasNewSession: true);
  }

  [Fact]
  public void CurrentSchemaRequiresCreationMetadataShape() {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path))
      store.Sessions.CreatePrimary(Id(1));
    database.Execute("DROP TABLE session_creation; CREATE TABLE session_creation (unrelated TEXT);");
    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(6L, database.Scalar("PRAGMA user_version;")); // raw-sql: invalid current schema must not be silently recreated
    Assert.Equal(Id(1), Assert.Single(StoredSessions.Read(database)).Id);
  }

  private static void AssertLegacyEvidence(TemporaryDatabase database, bool hasNewSession = false) {
    Assert.Equal(hasNewSession ? 4 : 3, StoredSessions.Read(database).Count);
    Assert.Equal(new StoredSessionRow(Id(1), 7), StoredSessions.Read(database)[0]);
    Assert.Equal(new StoredSubmission(Id(11), Id(12)), Assert.Single(StoredSubmissions.Read(database)));
    Assert.Equal(new StoredExecution(Id(1), Id(12), Id(11), true, ExecutionOutcome.Failed), Assert.Single(StoredExecutions.Read(database)));
    Assert.Equal(new StoredRequest(Id(13), Id(12), ExecutionOutcome.Failed, "old error"), Assert.Single(StoredExecutions.ReadRequests(database)));
    Assert.Equal((Id(13), 0L), Assert.Single(StoredDeliveries.ReadRequestOrder(database)));
    Assert.Equal(new StoredDelivery(Id(11), Id(12), Id(13), ExecutionOutcome.Failed), Assert.Single(StoredDeliveries.Read(database)));
    Assert.Equal(new[] { (ChatRole.User, Id(11)), (ChatRole.Assistant, Id(14)) }, StoredDeliveries.ReadMessages(database));
    var output = Assert.Single(StoredLlmResponses.Read(database));
    Assert.Equal(Id(13), output.RequestId);
    Assert.Equal(Id(14), output.ResponseId);
    Assert.Equal(0L, output.ChunkIndex);
    Assert.Equal(new byte[] { 0, 240, 159, 153, 130 }, output.Bytes);
    Assert.True(output.Final);
    Assert.Equal(new StoredTool(Id(12), Id(13), 0, "call", "fake", ArgumentsJson, true,
        ExecutionOutcome.Failed, ResultJson, "old tool error"), Assert.Single(StoredTools.Read(database)));
  }

  private static Guid Id(int value) => Guid.Parse($"{value:x8}-0000-0000-0000-000000000000");

  // Frozen schema-four DDL: this fixture never depends on the current initializer or a version relabel.
  private static void CreateSchemaFour(TemporaryDatabase database) => database.Execute($"""
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
    CREATE INDEX execution_runs_history_order ON execution_runs(session_id, history_revision, ordinal DESC);
    CREATE INDEX execution_runs_terminal_order ON execution_runs(session_id, ordinal DESC) WHERE outcome IS NOT NULL;
    CREATE TABLE execution_requests (
        request_id TEXT PRIMARY KEY NOT NULL, run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
        request_index INTEGER NOT NULL DEFAULT 0 CHECK (request_index >= 0), output_id TEXT,
        next_batch_index INTEGER NOT NULL DEFAULT 0 CHECK (next_batch_index >= 0),
        output_final INTEGER NOT NULL DEFAULT 0 CHECK (output_final IN (0, 1)),
        outcome INTEGER CHECK (outcome IN (0, 1, 2)), error TEXT, UNIQUE (run_id, request_index)
    ) STRICT;
    CREATE UNIQUE INDEX one_unfinished_request ON execution_requests(run_id) WHERE outcome IS NULL;
    CREATE TABLE execution_output (
        request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
        batch_index INTEGER NOT NULL CHECK (batch_index >= 0),
        text_utf8 BLOB NOT NULL CHECK (length(text_utf8) <= 16384),
        is_final INTEGER NOT NULL CHECK (is_final IN (0, 1)), PRIMARY KEY (request_id, batch_index)
    ) STRICT;
    CREATE TABLE execution_inputs (
        ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL REFERENCES sessions(session_id), input_id TEXT NOT NULL,
        run_id TEXT NOT NULL REFERENCES execution_runs(run_id), request_id TEXT REFERENCES execution_requests(request_id),
        outcome INTEGER CHECK (outcome IN (0, 1, 2)), UNIQUE (session_id, input_id),
        FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id)
    ) STRICT;
    CREATE INDEX pending_execution_inputs ON execution_inputs(run_id, ordinal) WHERE request_id IS NULL AND outcome IS NULL;
    CREATE INDEX nonterminal_execution_inputs ON execution_inputs(run_id) WHERE outcome IS NULL;
    CREATE INDEX execution_inputs_request_order ON execution_inputs(request_id, ordinal) WHERE request_id IS NOT NULL;
    CREATE TABLE execution_messages (
        ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL REFERENCES sessions(session_id),
        history_revision INTEGER NOT NULL CHECK (history_revision >= 0), run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
        role INTEGER NOT NULL CHECK (role IN (0, 1)), message_id TEXT NOT NULL, input_id TEXT,
        request_id TEXT REFERENCES execution_requests(request_id), UNIQUE (run_id, role, message_id),
        FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id),
        CHECK ((role = 0 AND input_id IS NOT NULL AND request_id IS NULL)
            OR (role = 1 AND input_id IS NULL AND request_id IS NOT NULL))
    ) STRICT;
    CREATE INDEX execution_messages_history_order ON execution_messages(session_id, history_revision, ordinal DESC);
    CREATE TABLE execution_tools (
        run_id TEXT NOT NULL REFERENCES execution_runs(run_id), request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
        call_index INTEGER NOT NULL CHECK (call_index >= 0), call_id TEXT NOT NULL, name TEXT NOT NULL,
        arguments_json TEXT NOT NULL CHECK (length(CAST(arguments_json AS BLOB)) <= 16384),
        started INTEGER NOT NULL DEFAULT 0 CHECK (started IN (0, 1)), outcome INTEGER CHECK (outcome IN (0, 1, 2)),
        result_json TEXT CHECK (length(CAST(result_json AS BLOB)) <= 16384), error TEXT,
        PRIMARY KEY (request_id, call_id), UNIQUE (request_id, call_index), UNIQUE (run_id, call_id)
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
    INSERT INTO execution_tools VALUES ('{Id(12):D}', '{Id(13):D}', 0, 'call', 'fake',
        '{ArgumentsJson}', 1, 2, '{ResultJson}', 'old tool error');
    PRAGMA user_version = 4;
    """);
}
