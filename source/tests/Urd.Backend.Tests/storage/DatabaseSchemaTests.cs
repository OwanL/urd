using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class DatabaseSchemaTests {
  [Fact]
  public void FreshDatabaseCreatesAndReopensCurrentSchema() {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Equal(11L, database.Scalar("PRAGMA user_version;")); // raw-sql: current schema creation, rejection or constraint is the subject under test
      store.Sessions.CreatePrimary(Guid.NewGuid());
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(11L, database.Scalar("PRAGMA user_version;")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sessions;")); // raw-sql: current schema creation, rejection or constraint is the subject under test
  }

  [Fact]
  public void OldDatabaseIsRejectedWithManualResetInstructions() {
    using var database = new TemporaryDatabase();
    database.Execute("PRAGMA user_version = 10;");

    NotSupportedException error = Assert.Throws<NotSupportedException>(() => UrdStore.Open(database.Path));

    Assert.Contains("stop Urd", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("delete", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("urd.sqlite", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("-wal", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("-shm", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("restart", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(File.Exists(database.Path));
    Assert.Equal(10L, database.Scalar("PRAGMA user_version;")); // raw-sql: current schema creation, rejection or constraint is the subject under test
  }

  [Fact]
  public void NonemptyUnversionedDatabaseIsNotClaimedAsCurrent() {
    using var database = new TemporaryDatabase();
    database.Execute("CREATE TABLE unrelated (value TEXT); INSERT INTO unrelated VALUES ('retained');");

    NotSupportedException error = Assert.Throws<NotSupportedException>(() => UrdStore.Open(database.Path));

    Assert.Contains("stop Urd", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(0L, database.Scalar("PRAGMA user_version;")); // raw-sql: rejection must leave the unversioned database untouched
    Assert.Equal("retained", database.Scalar("SELECT value FROM unrelated;")); // raw-sql: rejection must preserve unrelated contents
  }

  [Fact]
  public void CurrentSchemaEnforcesLinksAndRangesWhileRetainingLargePayloads() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid run = Guid.NewGuid();
    Guid input = Guid.NewGuid();
    string text = new('x', 17000);
    Assert.True(store.UserMessages.Admit(session, 0,
        new(input, 0, text, TestLlmSelection.Default), true, run, run).NewlyAccepted);
    Guid request = store.Executions.ClaimNextRequest(session, run, 0)!.RequestId;
    Assert.Equal(1L, database.Scalar($"SELECT attempt FROM execution_requests WHERE request_id = '{request:D}';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Throws<SqliteException>(() => database.Execute($"UPDATE execution_requests SET attempt = 0 WHERE request_id = '{request:D}';"));
    Assert.Throws<SqliteException>(() => database.Execute($"UPDATE execution_requests SET attempt = 3 WHERE request_id = '{request:D}';"));
    Assert.True(store.Executions.StartRequest(session, run, 0, request));
    Assert.Equal(2L, database.Scalar($"SELECT attempt FROM execution_requests WHERE request_id = '{request:D}';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(17000L, database.Scalar($"SELECT length(original_text_utf8) FROM input_decisions WHERE input_id = '{input:D}';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('input_decisions') WHERE name = 'llm_selection_json';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('execution_messages') WHERE name = 'request_id';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason,
              llm_selection_json)
      VALUES ('{session:D}', '{Guid.NewGuid():D}', 0, X'00', 1, '{Guid.NewGuid():D}', NULL, NULL);
      """));

    string largeArguments = new('a', 17000);
    string largeResult = new('b', 17000);
    database.Execute($"""
      INSERT INTO execution_tools (run_id, request_id, call_index, call_id, name, arguments_json, result_json)
      VALUES ('{run:D}', '{request:D}', 0, 'large-call', 'large-tool', '{largeArguments}', '{largeResult}');
      """);
    Assert.Equal(17000L, database.Scalar("SELECT length(CAST(arguments_json AS BLOB)) FROM execution_tools WHERE call_id = 'large-call';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(17000L, database.Scalar("SELECT length(CAST(result_json AS BLOB)) FROM execution_tools WHERE call_id = 'large-call';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE execution_tools SET started = 2 WHERE call_id = 'large-call';"));
    Assert.Throws<SqliteException>(() => database.Execute($"UPDATE execution_inputs SET not_sent = 2 WHERE input_id = '{input:D}';"));

    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id)
      VALUES ('{session:D}', 0, '{run:D}', 0, '{Guid.NewGuid():D}', '{input:D}');
      """));
    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, request_id,
          first_chunk_index, end_chunk_index)
      VALUES ('{session:D}', 0, '{run:D}', 1, '{Guid.NewGuid():D}', '{request:D}', 3, 2);
      """));
    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, request_id,
          call_id, tool_name)
      VALUES ('{session:D}', 0, '{run:D}', 2, '{Guid.NewGuid():D}', '{request:D}', NULL, 'missing-call-id');
      """));
  }
}
