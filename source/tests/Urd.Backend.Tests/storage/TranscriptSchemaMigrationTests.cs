using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class TranscriptSchemaMigrationTests {
  [Fact]
  public void SchemaEightUpgradeMergesToolsPreservesLegacyFactsAndLiftsOnlyTheIntendedCaps() {
    using var database = new TemporaryDatabase();
    SchemaEightDatabase.Create(database);
    Assert.Equal(8L, database.Scalar("PRAGMA user_version;")); // raw-sql: verify fixture, upgrade and rollback schema versions

    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Equal(10L, database.Scalar("PRAGMA user_version;")); // raw-sql: verify fixture, upgrade and rollback schema versions
      IReadOnlyList<TranscriptEntry> entries = ReadEntries(database, SchemaEightDatabase.SessionId);
      Assert.Equal(new (int Role, string? MessageId, string? CallId)[] {
        (0, SchemaEightDatabase.FirstInputId.ToString("D"), null),
        (1, SchemaEightDatabase.FirstOutputId.ToString("D"), null),
        (2, entries[2].MessageId, "call-a"),
        (2, entries[3].MessageId, "call-b"),
        (0, SchemaEightDatabase.SecondInputId.ToString("D"), null),
        (1, SchemaEightDatabase.SecondOutputId.ToString("D"), null),
        (2, entries[6].MessageId, "call-c"),
        (0, SchemaEightDatabase.SettledInputId.ToString("D"), null),
        (1, SchemaEightDatabase.SettledOutputId.ToString("D"), null),
        (0, SchemaEightDatabase.ToolOnlyInputId.ToString("D"), null),
        (2, entries[10].MessageId, "call-d"),
        (2, entries[11].MessageId, "call-e"),
        (2, entries[12].MessageId, "call-f")
      }, entries.Select(entry => (entry.Role, MessageId: (string?)entry.MessageId, entry.CallId)).ToArray());

      Assert.All(entries.Where(entry => entry.Role == 2), entry => Assert.True(Guid.TryParse(entry.MessageId, out _)));
      Assert.All(entries, entry => {
        Assert.Equal(0, entry.FirstBatchIndex);
        Assert.Null(entry.CreatedAt);
      });
      Assert.Equal(3, entries[1].EndBatchIndex);
      Assert.Null(entries[5].EndBatchIndex);
      Assert.Equal(2, entries[8].EndBatchIndex);
      Assert.Equal(SchemaEightDatabase.FirstRequestId.ToString("D"), entries[2].RequestId);
      Assert.Equal(SchemaEightDatabase.FirstRequestId.ToString("D"), entries[3].RequestId);
      Assert.Equal(SchemaEightDatabase.SecondRequestId.ToString("D"), entries[6].RequestId);
      Assert.Equal(SchemaEightDatabase.FirstToolOnlyRequestId.ToString("D"), entries[10].RequestId);
      Assert.Equal(SchemaEightDatabase.FirstToolOnlyRequestId.ToString("D"), entries[11].RequestId);
      Assert.Equal(SchemaEightDatabase.SecondToolOnlyRequestId.ToString("D"), entries[12].RequestId);

      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_messages WHERE created_at IS NOT NULL;")); // raw-sql: legacy entry times must remain unknown
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_tools WHERE started_at IS NOT NULL OR ended_at IS NOT NULL;")); // raw-sql: legacy tool times must remain unknown
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_requests WHERE usage_json IS NOT NULL;")); // raw-sql: legacy usage must remain unknown
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_requests WHERE attempt <> 0;")); // raw-sql: legacy attempt provenance remains unknown
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_inputs WHERE not_sent <> 0;")); // raw-sql: legacy inputs are not marked as not sent
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_messages WHERE role = 0 AND request_id IS NOT NULL;")); // raw-sql: migration must not invent user request links
      Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: verify rebuild and rollback foreign-key integrity

      string largeInput = new('x', 17000);
      Guid largeInputId = Guid.NewGuid();
      database.Execute($"""
        INSERT INTO input_decisions
            (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason)
        VALUES ('{SchemaEightDatabase.SessionId:D}', '{largeInputId:D}', 7, X'{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(largeInput))}', 2, NULL, 1);
        """);
      Assert.Equal(17000L, database.Scalar($"SELECT length(original_text_utf8) FROM input_decisions WHERE input_id = '{largeInputId:D}';")); // raw-sql: verify migration-specific stored fact unavailable through Stored* readers

      string largeArguments = new('a', 17000);
      string largeResult = new('b', 17000);
      database.Execute($"""
        INSERT INTO execution_tools (run_id, request_id, call_index, call_id, name, arguments_json, result_json)
        VALUES ('{SchemaEightDatabase.ActiveRunId:D}', '{SchemaEightDatabase.FirstRequestId:D}', 9,
            'wide-call', 'large', '{largeArguments}', '{largeResult}');
        """);
      Assert.Equal(17000L, database.Scalar("SELECT length(CAST(arguments_json AS BLOB)) FROM execution_tools WHERE call_id = 'wide-call';")); // raw-sql: verify lifted argument CHECK retains the full value
      Assert.Equal(17000L, database.Scalar("SELECT length(CAST(result_json AS BLOB)) FROM execution_tools WHERE call_id = 'wide-call';")); // raw-sql: verify lifted result CHECK retains the full value
      Assert.Throws<SqliteException>(() => database.Execute($"""
        INSERT INTO execution_tools (run_id, request_id, call_index, call_id, name, arguments_json, started)
        VALUES ('{SchemaEightDatabase.ActiveRunId:D}', '{SchemaEightDatabase.FirstRequestId:D}', 10,
            'invalid-call', 'invalid', 'not-json', 2);
        """));

      long priorLastOrdinal = (long)database.Scalar("SELECT MAX(ordinal) FROM execution_messages;")!; // raw-sql: verify rebuilt index high-water ordinal
      Guid newSessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      var admission = store.UserMessages.Admit(newSessionId, 0,
          new(Guid.NewGuid(), 0, "new entry", TestLlmSelection.Default), true, Guid.NewGuid());
      Assert.True(admission.NewlyAccepted);
      Guid newRun = admission.Receipt.ExecutionId!.Value;
      Guid newRequest = store.Executions.ClaimNextRequest(newSessionId, newRun, 0)!.RequestId;
      Assert.True(store.Executions.StartRequest(newSessionId, newRun, 0, newRequest));
      Assert.Equal(priorLastOrdinal + 1,
          database.Scalar($"SELECT ordinal FROM execution_messages WHERE session_id = '{newSessionId:D}';")); // raw-sql: verify migrated ordinal or sequence preservation
    }
  }

  [Fact]
  public void SchemaEightUpgradePlacesToolOnlyRequestsBetweenTheirNeighboringTextEntries() {
    using var database = new TemporaryDatabase();
    SchemaEightDatabase.CreateWithInterleavedToolOnlyRequest(database);

    using UrdStore store = UrdStore.Open(database.Path);

    IReadOnlyList<TranscriptEntry> entries = ReadEntries(database, SchemaEightDatabase.SessionId);
    Assert.Equal(new (int Role, string? InputId, string? RequestId, string? CallId)[] {
      (0, SchemaEightDatabase.InterleavingInputId.ToString("D"), null, null),
      (1, null, SchemaEightDatabase.InterleavingFirstRequestId.ToString("D"), null),
      (2, null, SchemaEightDatabase.InterleavingToolOnlyRequestId.ToString("D"), "interleaved-call"),
      (1, null, SchemaEightDatabase.InterleavingLastRequestId.ToString("D"), null),
      (0, SchemaEightDatabase.LaterInputId.ToString("D"), null, null)
    }, entries.Where(entry => entry.InputId == SchemaEightDatabase.InterleavingInputId.ToString("D")
          || entry.InputId == SchemaEightDatabase.LaterInputId.ToString("D")
          || entry.RequestId == SchemaEightDatabase.InterleavingFirstRequestId.ToString("D")
          || entry.RequestId == SchemaEightDatabase.InterleavingToolOnlyRequestId.ToString("D")
          || entry.RequestId == SchemaEightDatabase.InterleavingLastRequestId.ToString("D"))
        .Select(entry => (entry.Role, entry.InputId, entry.RequestId, entry.CallId)).ToArray());
  }

  [Fact]
  public void SchemaNineUpgradePreservesTranscriptOrdinalsFactsIndexesAndSequenceHighWater() {
    using var database = new TemporaryDatabase();
    SchemaNineDatabase.Create(database);
    Assert.Equal(9L, database.Scalar("PRAGMA user_version;")); // raw-sql: verify frozen schema-nine fixture

    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Equal(10L, database.Scalar("PRAGMA user_version;")); // raw-sql: verify schema-nine upgrade
      Assert.Equal(new long[] { 3, 17, 30 }, ReadLongs(database, "SELECT ordinal FROM execution_messages ORDER BY ordinal;")); // raw-sql: verify preserved sparse migration ordinals
      Assert.Equal(SchemaNineDatabase.InputId.ToString("D"), database.Scalar("SELECT message_id FROM execution_messages WHERE ordinal = 3;")); // raw-sql: verify migrated ordinal or sequence preservation
      Assert.Equal(SchemaNineDatabase.InputId.ToString("D"), database.Scalar("SELECT input_id FROM execution_messages WHERE ordinal = 3;")); // raw-sql: verify migrated ordinal or sequence preservation
      Assert.Equal("legacy-user-time", database.Scalar("SELECT created_at FROM execution_messages WHERE ordinal = 3;")); // raw-sql: verify migrated ordinal or sequence preservation
      Assert.Equal("legacy-assistant", database.Scalar("SELECT message_id FROM execution_messages WHERE ordinal = 17;")); // raw-sql: verify migrated ordinal or sequence preservation
      Assert.Equal("legacy-assistant-time", database.Scalar("SELECT created_at FROM execution_messages WHERE ordinal = 17;")); // raw-sql: verify migrated ordinal or sequence preservation
      Assert.Equal(3L, database.Scalar("SELECT end_batch_index FROM execution_messages WHERE ordinal = 17;")); // raw-sql: verify migrated ordinal or sequence preservation
      Assert.Equal("tool-entry", database.Scalar("SELECT message_id FROM execution_messages WHERE ordinal = 30;")); // raw-sql: verify migrated ordinal or sequence preservation
      Assert.Equal("legacy-call", database.Scalar("SELECT call_id FROM execution_messages WHERE ordinal = 30;")); // raw-sql: verify migrated ordinal or sequence preservation
      Assert.Equal("legacy-tool", database.Scalar("SELECT tool_name FROM execution_messages WHERE ordinal = 30;")); // raw-sql: verify migrated ordinal or sequence preservation
      Assert.Equal("[]", database.Scalar("SELECT usage_json FROM execution_requests WHERE request_id = '" + SchemaNineDatabase.RequestId.ToString("D") + "';")); // raw-sql: verify migration-specific stored fact unavailable through Stored* readers
      Assert.Equal(SchemaNineDatabase.RequestId.ToString("D"),
          database.Scalar("SELECT request_id FROM execution_messages WHERE ordinal = 17;")); // raw-sql: verify migrated ordinal or sequence preservation
      Assert.Equal(0L, database.Scalar("SELECT attempt FROM execution_requests WHERE request_id = '" + SchemaNineDatabase.RequestId.ToString("D") + "';")); // raw-sql: verify send-attempt marker state
      Assert.Equal(0L, database.Scalar("SELECT not_sent FROM execution_inputs WHERE input_id = '" + SchemaNineDatabase.InputId.ToString("D") + "';")); // raw-sql: verify send-attempt marker state
      Assert.Equal("0", database.Scalar("SELECT dflt_value FROM pragma_table_info('execution_requests') WHERE name = 'attempt';")); // raw-sql: verify attempt schema default
      Assert.Equal("0", database.Scalar("SELECT dflt_value FROM pragma_table_info('execution_inputs') WHERE name = 'not_sent';")); // raw-sql: verify not-sent schema default
      Assert.Equal(1L, database.Scalar("SELECT partial FROM pragma_index_list('execution_inputs') WHERE name = 'execution_inputs_not_sent_order';")); // raw-sql: verify not-sent index is partial
      Assert.Equal("session_id,ordinal", database.Scalar("SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_index_info('execution_inputs_not_sent_order') ORDER BY seqno);")); // raw-sql: inspect migrated schema or index definition
      Assert.Throws<SqliteException>(() => database.Execute($"UPDATE execution_requests SET attempt = 3 WHERE request_id = '{SchemaNineDatabase.RequestId:D}';"));
      Assert.Throws<SqliteException>(() => database.Execute($"UPDATE execution_inputs SET not_sent = 2 WHERE input_id = '{SchemaNineDatabase.InputId:D}';"));
      Assert.Equal(100L, database.Scalar("SELECT seq FROM sqlite_sequence WHERE name = 'execution_messages';")); // raw-sql: verify preserved ordinal sequence high-water
      Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'execution_messages_history_order';")); // raw-sql: inspect migrated schema or index definition
      Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'execution_messages_request_order';")); // raw-sql: inspect migrated schema or index definition
      Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'execution_inputs_not_sent_order';")); // raw-sql: inspect migrated schema or index definition
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;")); // raw-sql: inspect migrated schema or index definition

      database.Execute($"PRAGMA foreign_keys = ON; UPDATE execution_messages SET request_id = '{SchemaNineDatabase.RequestId:D}' WHERE ordinal = 3;");
      Assert.Equal(SchemaNineDatabase.RequestId.ToString("D"),
          database.Scalar("SELECT request_id FROM execution_messages WHERE ordinal = 3;")); // raw-sql: verify migrated ordinal or sequence preservation
      Assert.Throws<SqliteException>(() => database.Execute($"PRAGMA foreign_keys = ON; UPDATE execution_messages SET request_id = 'missing-request' WHERE ordinal = 3;"));
      Assert.Throws<SqliteException>(() => database.Execute("PRAGMA foreign_keys = ON; UPDATE execution_messages SET input_id = 'missing-input' WHERE ordinal = 3;"));

      database.Execute("DELETE FROM execution_messages WHERE ordinal = 30;");
      database.Execute($"INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, request_id) VALUES ('{SchemaNineDatabase.SessionId:D}', 4, '{SchemaNineDatabase.RunId:D}', 1, 'new-after-rebuild', '{SchemaNineDatabase.RequestId:D}');");
      Assert.Equal(101L, database.Scalar("SELECT ordinal FROM execution_messages WHERE message_id = 'new-after-rebuild';")); // raw-sql: verify migrated ordinal or sequence preservation
      Assert.Equal(101L, database.Scalar("SELECT seq FROM sqlite_sequence WHERE name = 'execution_messages';")); // raw-sql: verify migrated ordinal or sequence preservation
    }
  }

  [Fact]
  public void FailedSchemaTenUpgradeRollsBackAndPreservesTheSchemaNineDatabase() {
    using var database = new TemporaryDatabase();
    SchemaNineDatabase.Create(database);
    database.Execute("PRAGMA foreign_keys = OFF; UPDATE execution_inputs SET request_id = 'missing-request';");

    Assert.Throws<InvalidOperationException>(() => UrdStore.Open(database.Path));

    Assert.Equal(9L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed v10 migration preserves its starting version
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('execution_requests') WHERE name = 'attempt';")); // raw-sql: rollback removes new columns
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('execution_inputs') WHERE name = 'not_sent';")); // raw-sql: rollback removes new columns
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'execution_inputs_not_sent_order';")); // raw-sql: rollback removes new indexes
    Assert.Equal(3L, database.Scalar("SELECT COUNT(*) FROM execution_messages;")); // raw-sql: verify transaction-time history placement
    Assert.Equal(3L, database.Scalar("SELECT MIN(ordinal) FROM execution_messages;")); // raw-sql: verify migrated ordinal or sequence preservation
    Assert.Equal(30L, database.Scalar("SELECT MAX(ordinal) FROM execution_messages;")); // raw-sql: verify migrated ordinal or sequence preservation
    Assert.Equal(100L, database.Scalar("SELECT seq FROM sqlite_sequence WHERE name = 'execution_messages';")); // raw-sql: verify migrated ordinal or sequence preservation
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;")); // raw-sql: rollback retains the original violating row
    database.Execute($"PRAGMA foreign_keys = ON; UPDATE execution_inputs SET request_id = '{SchemaNineDatabase.RequestId:D}';");
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;")); // raw-sql: inspect migrated schema or index definition
  }

  [Fact]
  public void MigratedTranscriptChecksValidateBatchRangesAndToolCallIdentity() {
    using var database = new TemporaryDatabase();
    SchemaEightDatabase.Create(database);
    using UrdStore store = UrdStore.Open(database.Path);

    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, request_id,
          first_batch_index, end_batch_index)
      VALUES ('{SchemaEightDatabase.SessionId:D}', 7, '{SchemaEightDatabase.ActiveRunId:D}', 1,
          '{Guid.NewGuid():D}', '{SchemaEightDatabase.SecondRequestId:D}', 3, 2);
      """));
    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, request_id,
          call_id, tool_name)
      VALUES ('{SchemaEightDatabase.SessionId:D}', 7, '{SchemaEightDatabase.ActiveRunId:D}', 2,
          '{Guid.NewGuid():D}', '{SchemaEightDatabase.SecondRequestId:D}', NULL, 'missing-call-id');
      """));
  }

  [Fact]
  public void FailedSchemaNineUpgradeRollsBackAndPreservesTheSchemaEightDatabase() {
    using var database = new TemporaryDatabase();
    SchemaEightDatabase.Create(database);
    database.Execute("CREATE TABLE execution_messages_v9 (witness TEXT); INSERT INTO execution_messages_v9 VALUES ('retained');");

    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));

    Assert.Equal(8L, database.Scalar("PRAGMA user_version;")); // raw-sql: verify fixture, upgrade and rollback schema versions
    Assert.Equal("retained", database.Scalar("SELECT witness FROM execution_messages_v9;")); // raw-sql: rollback must preserve the conflicting witness table
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('execution_messages') WHERE name = 'first_batch_index';")); // raw-sql: rollback must restore the old entry schema
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('execution_tools') WHERE name = 'started_at';")); // raw-sql: rollback must restore the old tool schema
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('execution_requests') WHERE name = 'usage_json';")); // raw-sql: rollback must restore the old request schema
    Assert.Equal(7L, database.Scalar($"SELECT history_revision FROM sessions WHERE session_id = '{SchemaEightDatabase.SessionId:D}';")); // raw-sql: verify migration-specific stored fact unavailable through Stored* readers
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_messages WHERE role NOT IN (0, 1);")); // raw-sql: backfilled entries must not escape rollback
    Assert.Equal(6L, database.Scalar("SELECT COUNT(*) FROM execution_tools;")); // raw-sql: rollback must preserve every legacy tool
    Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: verify rebuild and rollback foreign-key integrity
  }

  private static long[] ReadLongs(TemporaryDatabase database, string sql) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = sql;
    using SqliteDataReader reader = command.ExecuteReader();
    var values = new List<long>();
    while (reader.Read())
      values.Add(reader.GetInt64(0));
    return values.ToArray();
  }

  private static List<TranscriptEntry> ReadEntries(TemporaryDatabase database, Guid sessionId) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
      SELECT ordinal, role, message_id, input_id, request_id, first_batch_index,
          end_batch_index, created_at, call_id, tool_name
      FROM execution_messages WHERE session_id = $session ORDER BY ordinal;
      """;
    command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
    using SqliteDataReader reader = command.ExecuteReader();
    var entries = new List<TranscriptEntry>();
    while (reader.Read()) {
      entries.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
          reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
          reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
          reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
          reader.IsDBNull(9) ? null : reader.GetString(9)));
    }
    return entries;
  }

  private static class SchemaNineDatabase {
    internal static Guid SessionId { get; } = Id(1);
    internal static Guid InputId { get; } = Id(11);
    internal static Guid RunId { get; } = Id(101);
    internal static Guid RequestId { get; } = Id(111);

    internal static void Create(TemporaryDatabase database) => database.Execute($"""
      PRAGMA foreign_keys = ON;
      CREATE TABLE sessions (
          session_id TEXT NOT NULL PRIMARY KEY,
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
      ) STRICT;
      CREATE TABLE input_decisions (
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          input_id TEXT NOT NULL,
          expected_history_revision INTEGER NOT NULL CHECK (expected_history_revision >= 0),
          original_text_utf8 BLOB NOT NULL,
          decision INTEGER NOT NULL CHECK (decision IN (1, 2)),
          run_id TEXT,
          refusal_reason INTEGER CHECK (refusal_reason IN (1, 2, 3, 4)),
          llm_selection_json TEXT,
          PRIMARY KEY (session_id, input_id),
          CHECK ((decision = 1 AND run_id IS NOT NULL
                      AND run_id <> '00000000-0000-0000-0000-000000000000' AND refusal_reason IS NULL)
              OR (decision = 2 AND run_id IS NULL AND refusal_reason IS NOT NULL))
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
          usage_json TEXT,
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
      CREATE INDEX pending_execution_inputs ON execution_inputs(run_id, ordinal)
          WHERE request_id IS NULL AND outcome IS NULL;
      CREATE INDEX nonterminal_execution_inputs ON execution_inputs(run_id) WHERE outcome IS NULL;
      CREATE INDEX execution_inputs_request_order ON execution_inputs(request_id, ordinal) WHERE request_id IS NOT NULL;
      CREATE TABLE execution_messages (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          role INTEGER NOT NULL CHECK (role IN (0, 1, 2)),
          message_id TEXT NOT NULL,
          input_id TEXT,
          request_id TEXT REFERENCES execution_requests(request_id),
          first_batch_index INTEGER NOT NULL DEFAULT 0 CHECK (first_batch_index >= 0),
          end_batch_index INTEGER,
          created_at TEXT,
          call_id TEXT,
          tool_name TEXT,
          UNIQUE (run_id, role, message_id),
          UNIQUE (request_id, call_id),
          FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id),
          CHECK (end_batch_index IS NULL OR end_batch_index >= first_batch_index),
          CHECK ((role = 0 AND input_id IS NOT NULL AND request_id IS NULL AND call_id IS NULL
                      AND tool_name IS NULL AND end_batch_index IS NULL)
              OR (role = 1 AND input_id IS NULL AND request_id IS NOT NULL AND call_id IS NULL
                      AND tool_name IS NULL)
              OR (role = 2 AND input_id IS NULL AND request_id IS NOT NULL AND end_batch_index IS NULL
                      AND call_id IS NOT NULL))
      ) STRICT;
      CREATE INDEX execution_messages_history_order
          ON execution_messages(session_id, history_revision, ordinal DESC);
      CREATE INDEX execution_messages_request_order
          ON execution_messages(request_id, role, first_batch_index);
      CREATE TABLE execution_tools (
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
          call_index INTEGER NOT NULL CHECK (call_index >= 0),
          call_id TEXT NOT NULL,
          name TEXT NOT NULL,
          arguments_json TEXT NOT NULL,
          started INTEGER NOT NULL DEFAULT 0 CHECK (started IN (0, 1)),
          outcome INTEGER CHECK (outcome IN (0, 1, 2)),
          result_json TEXT,
          error TEXT,
          started_at TEXT,
          ended_at TEXT,
          PRIMARY KEY (request_id, call_id),
          UNIQUE (request_id, call_index),
          UNIQUE (run_id, call_id)
      ) STRICT;
      CREATE TABLE session_creation (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL UNIQUE REFERENCES sessions(session_id) ON DELETE CASCADE
      ) STRICT;
      CREATE TABLE provider_connections (
          connection_id TEXT PRIMARY KEY NOT NULL,
          name TEXT NOT NULL,
          route INTEGER NOT NULL CHECK (route IN (0, 1, 2)),
          api INTEGER NOT NULL CHECK (api IN (0, 1)),
          endpoint TEXT NOT NULL,
          authentication INTEGER NOT NULL CHECK (authentication IN (0, 1, 2)),
          generation INTEGER NOT NULL CHECK (generation >= 0),
          credential_version INTEGER NOT NULL CHECK (credential_version >= 0),
          registration_json TEXT,
          credentials_json TEXT,
          disconnecting INTEGER NOT NULL CHECK (disconnecting IN (0, 1)),
          catalog_json TEXT,
          CHECK (route <> 2 OR authentication IN (0, 1))
      ) STRICT;
      CREATE TABLE provider_host (
          singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
          host_id TEXT NOT NULL
      ) STRICT;
      CREATE TABLE execution_provider_context (
          request_id TEXT PRIMARY KEY NOT NULL REFERENCES execution_requests(request_id),
          response_json TEXT NOT NULL
      ) STRICT;

      INSERT INTO sessions VALUES ('{SessionId:D}', 4);
      INSERT INTO session_creation (session_id) VALUES ('{SessionId:D}');
      INSERT INTO input_decisions (session_id, input_id, expected_history_revision, original_text_utf8,
          decision, run_id, refusal_reason)
      VALUES ('{SessionId:D}', '{InputId:D}', 4, X'6C6567616379', 1, '{RunId:D}', NULL);
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
      VALUES ('{SessionId:D}', '{RunId:D}', 4, '{InputId:D}', 0);
      INSERT INTO execution_requests (request_id, run_id, request_index, next_batch_index, usage_json, outcome)
      VALUES ('{RequestId:D}', '{RunId:D}', 0, 3, '[]', 0);
      INSERT INTO execution_inputs (session_id, input_id, run_id, request_id, outcome)
      VALUES ('{SessionId:D}', '{InputId:D}', '{RunId:D}', '{RequestId:D}', 0);
      INSERT INTO execution_messages (ordinal, session_id, history_revision, run_id, role, message_id,
          input_id, first_batch_index, created_at)
      VALUES (3, '{SessionId:D}', 4, '{RunId:D}', 0, '{InputId:D}', '{InputId:D}', 0, 'legacy-user-time');
      INSERT INTO execution_messages (ordinal, session_id, history_revision, run_id, role, message_id,
          request_id, first_batch_index, end_batch_index, created_at)
      VALUES (17, '{SessionId:D}', 4, '{RunId:D}', 1, 'legacy-assistant', '{RequestId:D}', 0, 3, 'legacy-assistant-time');
      INSERT INTO execution_tools (run_id, request_id, call_index, call_id, name, arguments_json, outcome)
      VALUES ('{RunId:D}', '{RequestId:D}', 0, 'legacy-call', 'legacy-tool', '[]', 0);
      INSERT INTO execution_messages (ordinal, session_id, history_revision, run_id, role, message_id,
          request_id, call_id, tool_name)
      VALUES (30, '{SessionId:D}', 4, '{RunId:D}', 2, 'tool-entry', '{RequestId:D}', 'legacy-call', 'legacy-tool');
      INSERT INTO execution_messages (ordinal, session_id, history_revision, run_id, role, message_id,
          request_id)
      VALUES (100, '{SessionId:D}', 4, '{RunId:D}', 1, 'deleted-high-water', '{RequestId:D}');
      DELETE FROM execution_messages WHERE ordinal = 100;
      PRAGMA user_version = 9;
      """);

    private static Guid Id(int value) => Guid.Parse($"{value:x8}-0000-0000-0000-000000000000");
  }

  private sealed record TranscriptEntry(long Ordinal, int Role, string MessageId, string? InputId, string? RequestId,
      int FirstBatchIndex, int? EndBatchIndex, string? CreatedAt, string? CallId, string? ToolName);
}
