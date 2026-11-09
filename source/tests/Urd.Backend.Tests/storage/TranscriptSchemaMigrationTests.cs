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
      Assert.Equal(9L, database.Scalar("PRAGMA user_version;")); // raw-sql: verify fixture, upgrade and rollback schema versions
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
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('input_decisions') WHERE name = 'attempted';")); // raw-sql: packet 1A adds no attempt provenance
      Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: verify rebuild and rollback foreign-key integrity

      string largeInput = new('x', 17000);
      Guid largeInputId = Guid.NewGuid();
      database.Execute($"""
        INSERT INTO input_decisions
            (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason)
        VALUES ('{SchemaEightDatabase.SessionId:D}', '{largeInputId:D}', 7, X'{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(largeInput))}', 2, NULL, 1);
        """);
      Assert.Equal(17000L, database.Scalar($"SELECT length(original_text_utf8) FROM input_decisions WHERE input_id = '{largeInputId:D}';"));

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
      Assert.Equal(priorLastOrdinal + 1,
          database.Scalar($"SELECT ordinal FROM execution_messages WHERE session_id = '{newSessionId:D}';"));
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
    Assert.Equal(7L, database.Scalar($"SELECT history_revision FROM sessions WHERE session_id = '{SchemaEightDatabase.SessionId:D}';"));
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_messages WHERE role NOT IN (0, 1);")); // raw-sql: backfilled entries must not escape rollback
    Assert.Equal(6L, database.Scalar("SELECT COUNT(*) FROM execution_tools;")); // raw-sql: rollback must preserve every legacy tool
    Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: verify rebuild and rollback foreign-key integrity
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

  private sealed record TranscriptEntry(long Ordinal, int Role, string MessageId, string? InputId, string? RequestId,
      int FirstBatchIndex, int? EndBatchIndex, string? CreatedAt, string? CallId, string? ToolName);
}
