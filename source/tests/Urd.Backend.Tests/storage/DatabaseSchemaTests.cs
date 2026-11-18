using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class DatabaseSchemaTests {
  [Fact]
  public void FreshDatabaseCreatesAndReopensCurrentSchema() {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Equal(35L, database.Scalar("PRAGMA user_version;")); // raw-sql: current schema creation, rejection or constraint is the subject under test
      store.Sessions.CreatePrimary(Guid.NewGuid());
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(35L, database.Scalar("PRAGMA user_version;")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sessions;")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('execution_runs') WHERE name = 'created_at';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Contains("strftime('%Y-%m-%dT%H:%M:%fZ', 'now')",
        Assert.IsType<string>(database.Scalar("SELECT dflt_value FROM pragma_table_info('execution_runs') WHERE name = 'created_at';")), // raw-sql: current schema creation, rejection or constraint is the subject under test
        StringComparison.Ordinal);
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'session_prepass_details';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal("INTEGER", database.Scalar("SELECT type FROM pragma_table_info('session_prepass_details') WHERE name = 'used_session_model';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('session_prepass_details') WHERE name = 'used_session_model';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
  }

  [Fact]
  public void CompactionTriggerPreferenceIsNullablePositiveInt32() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);

    Assert.Equal("INTEGER", database.Scalar("SELECT type FROM pragma_table_info('preferences') WHERE name = 'compaction_trigger_token_count';")); // raw-sql: schema constraint is the subject under test
    Assert.Equal(0L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('preferences') WHERE name = 'compaction_trigger_token_count';")); // raw-sql: schema constraint is the subject under test
    database.Execute("INSERT INTO preferences (singleton, compaction_trigger_token_count) VALUES (1, 2147483647);"); // raw-sql: schema constraint is the subject under test
    Assert.Throws<SqliteException>(() => database.Execute(
        "UPDATE preferences SET compaction_trigger_token_count = 0 WHERE singleton = 1;")); // raw-sql: schema constraint is the subject under test
    Assert.Throws<SqliteException>(() => database.Execute(
        "UPDATE preferences SET compaction_trigger_token_count = -1 WHERE singleton = 1;")); // raw-sql: schema constraint is the subject under test
    Assert.Throws<SqliteException>(() => database.Execute(
        "UPDATE preferences SET compaction_trigger_token_count = 2147483648 WHERE singleton = 1;")); // raw-sql: schema constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT compaction_trigger_token_count = 2147483647 FROM preferences WHERE singleton = 1;")); // raw-sql: schema constraint is the subject under test
    database.Execute("UPDATE preferences SET compaction_trigger_token_count = NULL WHERE singleton = 1;"); // raw-sql: schema constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT compaction_trigger_token_count IS NULL FROM preferences WHERE singleton = 1;")); // raw-sql: schema constraint is the subject under test
  }

  [Fact]
  public void OldDatabaseIsRejectedWithManualResetInstructions() {
    using var database = new TemporaryDatabase();
    database.Execute("PRAGMA user_version = 33;");

    NotSupportedException error = Assert.Throws<NotSupportedException>(() => UrdStore.Open(database.Path));

    Assert.Contains("stop Urd", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("delete", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("urd.sqlite", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("-wal", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("-shm", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("restart", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(File.Exists(database.Path));
    Assert.Equal(33L, database.Scalar("PRAGMA user_version;")); // raw-sql: current schema creation, rejection or constraint is the subject under test
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
    var identity = new ExecutionIdentity(session, run, 0);
    Guid input = Guid.NewGuid();
    string text = new('x', 17000);
    Assert.True(store.UserMessages.Submit(session, 0,
        new(input, 0, text, TestLlmSelection.Default), true, run, run).NewlyAccepted);
    Guid request = store.Executions.CreateNextRequest(identity)!.RequestId;
    Assert.Equal(1L, database.Scalar($"SELECT attempt FROM execution_requests WHERE request_id = '{request:D}';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Throws<SqliteException>(() => database.Execute($"UPDATE execution_requests SET attempt = 0 WHERE request_id = '{request:D}';"));
    Assert.Throws<SqliteException>(() => database.Execute($"UPDATE execution_requests SET attempt = 3 WHERE request_id = '{request:D}';"));
    Assert.True(store.Executions.StartRequest(identity, request));
    Assert.Equal(2L, database.Scalar($"SELECT attempt FROM execution_requests WHERE request_id = '{request:D}';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(17000L, database.Scalar($"SELECT length(original_text_utf8) FROM input_decisions WHERE input_id = '{input:D}';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('input_decisions') WHERE name = 'llm_selection_json';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('execution_tools') WHERE name = 'arguments_excerpt';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('execution_tools') WHERE name = 'interrupted';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT MAX(cid) = (SELECT COUNT(*) - 1 FROM pragma_table_info('execution_tools')) FROM pragma_table_info('execution_tools') WHERE name IN ('arguments_json', 'result_json', 'error');")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(3L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('execution_tools') WHERE cid >= (SELECT MIN(cid) FROM pragma_table_info('execution_tools') WHERE name IN ('arguments_json', 'result_json', 'error'));")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(0L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('execution_messages') WHERE name = 'request_id';")); // raw-sql: compaction markers have no ordinary request
    Assert.Throws<SqliteException>(() => database.Execute($"UPDATE execution_messages SET request_id = NULL WHERE run_id = '{run:D}' AND role = 0;"));
    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason,
              llm_selection_json)
      VALUES ('{session:D}', '{Guid.NewGuid():D}', 0, X'00', 1, '{Guid.NewGuid():D}', NULL, NULL);
      """));

    string largeArguments = new('a', 17000);
    string largeResult = new('b', 17000);
    database.Execute($"""
      INSERT INTO execution_tools (
          run_id, request_id, call_index, call_id, name, arguments_json, arguments_excerpt, result_json)
      VALUES ('{run:D}', '{request:D}', 0, 'large-call', 'large-tool', '{largeArguments}', 'excerpt', '{largeResult}');
      """);
    Assert.Equal(17000L, database.Scalar("SELECT length(CAST(arguments_json AS BLOB)) FROM execution_tools WHERE call_id = 'large-call';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(17000L, database.Scalar("SELECT length(CAST(result_json AS BLOB)) FROM execution_tools WHERE call_id = 'large-call';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(0L, database.Scalar("SELECT interrupted FROM execution_tools WHERE call_id = 'large-call';")); // raw-sql: new tool calls are not interrupted
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE execution_tools SET started = 2 WHERE call_id = 'large-call';"));
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE execution_tools SET interrupted = 2 WHERE call_id = 'large-call';"));
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
