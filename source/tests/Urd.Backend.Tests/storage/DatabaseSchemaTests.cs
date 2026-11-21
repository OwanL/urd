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
    Guid primary = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Equal(40L, database.Scalar("PRAGMA user_version;")); // raw-sql: fresh open must initialize the current schema version
      store.Sessions.CreatePrimary(primary);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(40L, database.Scalar("PRAGMA user_version;")); // raw-sql: reopening must retain the current schema version
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sessions;")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('execution_runs') WHERE name = 'created_at';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Contains("strftime('%Y-%m-%dT%H:%M:%fZ', 'now')",
        Assert.IsType<string>(database.Scalar("SELECT dflt_value FROM pragma_table_info('execution_runs') WHERE name = 'created_at';")), // raw-sql: current schema creation, rejection or constraint is the subject under test
        StringComparison.Ordinal);
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'session_prepass_details';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal("INTEGER", database.Scalar("SELECT type FROM pragma_table_info('session_prepass_details') WHERE name = 'used_session_model';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('session_prepass_details') WHERE name = 'used_session_model';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal("TEXT", database.Scalar("SELECT type FROM pragma_table_info('sessions') WHERE name = 'owning_request_id';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(0L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('sessions') WHERE name = 'owning_request_id';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'sessions_owning_request' AND sql LIKE '%WHERE owning_call_id IS NOT NULL%';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'execution_tools_subagent_started' AND sql LIKE '%WHERE name = ''subagent'' AND started = 1%';")); // raw-sql: current schema creation, rejection or constraint is the subject under test
    Assert.Throws<SqliteException>(() => database.Execute($"UPDATE sessions SET owning_request_id = 'primary-request' WHERE session_id = '{primary:D}';")); // raw-sql: current schema must reject request identity on a primary session
    Guid subagent = Guid.NewGuid();
    database.Execute($"""
      INSERT INTO sessions (session_id, history_revision, kind, parent_session_id, owning_call_id, owning_request_id)
      VALUES ('{subagent:D}', 0, 1, '{primary:D}', 'fresh-call', 'fresh-request');
      """); // raw-sql: current schema must allow a valid exact subagent association
    Assert.Equal("fresh-request", database.Scalar($"SELECT owning_request_id FROM sessions WHERE session_id = '{subagent:D}';")); // raw-sql: current schema must retain the exact request identity
    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO sessions (session_id, history_revision, kind, parent_session_id, owning_call_id, owning_request_id)
      VALUES ('{Guid.NewGuid():D}', 0, 1, '{primary:D}', 'fresh-call', 'fresh-request');
      """)); // raw-sql: current schema must enforce exact association uniqueness
    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO sessions (session_id, history_revision, kind, parent_session_id, owning_call_id, owning_request_id)
      VALUES ('{Guid.NewGuid():D}', 0, 1, '{primary:D}', 'empty-call', '');
      """)); // raw-sql: current schema must reject an empty request identity
  }

  [Fact]
  public void Schema36UpgradeMarksSetupCompleteAndPreservesData() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path))
      store.Sessions.CreatePrimary(session);
    RecreateSchema37(database);
    database.Execute("DROP TABLE setup_completion; PRAGMA user_version = 36;"); // raw-sql: recreate the schema 36 baseline

    using UrdStore upgraded = UrdStore.Open(database.Path);
    Assert.True(upgraded.IsSetupComplete());
    Assert.Equal(40L, database.Scalar("PRAGMA user_version;")); // raw-sql: successful migrations must advance to the current version
    Assert.Equal(1L, database.Scalar($"SELECT COUNT(*) FROM sessions WHERE session_id = '{session:D}';")); // raw-sql: existing data survives
  }

  [Fact]
  public void FailedSchema36UpgradePreservesDataAndVersion() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path))
      store.Sessions.CreatePrimary(session);
    RecreateSchema37(database);
    // Introduce a conflicting object into the otherwise populated v36 baseline.
    database.Execute("DROP TABLE setup_completion; CREATE VIEW setup_completion AS SELECT 'preserved' AS value; PRAGMA user_version = 36;");

    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(36L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed upgrade retains version
    Assert.Equal("preserved", database.Scalar("SELECT value FROM setup_completion;")); // raw-sql: failed upgrade retains data
    Assert.Equal(1L, database.Scalar($"SELECT COUNT(*) FROM sessions WHERE session_id = '{session:D}';")); // raw-sql: existing session survives
  }

  [Fact]
  public void Schema37UpgradePreservesSenderAttributionAndInputOrdinalHighWater() {
    using var database = new TemporaryDatabase();
    Guid sender = Guid.NewGuid(), recipient = Guid.NewGuid(), input = Guid.NewGuid(), run = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(sender);
      store.Sessions.CreatePrimary(recipient);
      Assert.True(store.UserMessages.Submit(recipient, 0, new(input, 0, "retained question", TestLlmSelection.Default),
          true, run, provenance: InputProvenance.AgentQuestion, senderSessionId: sender).NewlyAccepted);
    }
    RecreateSchema37(database);
    database.Execute("""
      UPDATE sqlite_sequence SET seq = 99 WHERE name = 'execution_inputs';
      CREATE VIEW closed_session_usage AS SELECT 'conflict' AS value;
      """);
    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(37L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed upgrade must retain the schema version
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_list('execution_inputs') WHERE \"from\" = 'sender_session_id';")); // raw-sql: rollback must restore the old sender foreign key
    Assert.Equal(99L, database.Scalar("SELECT seq FROM sqlite_sequence WHERE name = 'execution_inputs';")); // raw-sql: rollback must preserve the input ordinal high-water mark
    database.Execute("DROP VIEW closed_session_usage;");

    using UrdStore upgraded = UrdStore.Open(database.Path);
    Assert.Equal(40L, database.Scalar("PRAGMA user_version;")); // raw-sql: successful retry must advance the schema version
    Assert.False(upgraded.IsSetupComplete());
    Assert.True(upgraded.DeletePrimarySession(sender));
    Assert.Equal(sender.ToString("D"), database.Scalar($"SELECT sender_session_id FROM execution_inputs WHERE input_id = '{input:D}';")); // raw-sql: sender attribution must survive deletion across the migrated foreign key
    Assert.Equal(99L, database.Scalar("SELECT seq FROM sqlite_sequence WHERE name = 'execution_inputs';")); // raw-sql: migration must preserve the input ordinal high-water mark
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;")); // raw-sql: rebuilt tables must satisfy all foreign keys
  }

  [Fact]
  public void Schema38UpgradeLeavesLegacyRequestIdentityNullAndConstrainsExactAssociations() {
    using var database = new TemporaryDatabase();
    Guid parent = Guid.NewGuid();
    Guid legacyOne = Guid.NewGuid();
    Guid legacyTwo = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path))
      store.Sessions.CreatePrimary(parent);

    database.Execute($"""
      INSERT INTO sessions (session_id, history_revision, kind, parent_session_id, owning_call_id)
      VALUES ('{legacyOne:D}', 0, 1, '{parent:D}', 'shared-call'),
             ('{legacyTwo:D}', 0, 1, '{parent:D}', 'shared-call');
      """); // raw-sql: seed legacy subagent associations before migration
    RecreateSchema38(database);

    using UrdStore upgraded = UrdStore.Open(database.Path);
    Assert.False(upgraded.IsSetupComplete());
    Assert.Equal(40L, database.Scalar("PRAGMA user_version;")); // raw-sql: schema 38 upgrade must advance to the current schema
    Assert.Equal(2L, database.Scalar($"SELECT COUNT(*) FROM sessions WHERE session_id IN ('{legacyOne:D}', '{legacyTwo:D}') AND owning_request_id IS NULL;")); // raw-sql: migration must not infer legacy request identities
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_list('sessions') WHERE \"from\" = 'owning_request_id';")); // raw-sql: request identity must not depend on transcript/request retention
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'sessions_owning_request';")); // raw-sql: migration must create the exact-association index
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'execution_tools_subagent_started';")); // raw-sql: migration must create the legacy lookup index

    Guid exactOne = Guid.NewGuid();
    Guid exactTwo = Guid.NewGuid();
    database.Execute($"""
      INSERT INTO sessions (session_id, history_revision, kind, parent_session_id, owning_call_id, owning_request_id)
      VALUES ('{exactOne:D}', 0, 1, '{parent:D}', 'shared-call', 'request-A'),
             ('{exactTwo:D}', 0, 1, '{parent:D}', 'shared-call', 'request-B');
      """); // raw-sql: exact request associations may share a parent and call when request identity differs
    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO sessions (session_id, history_revision, kind, parent_session_id, owning_call_id, owning_request_id)
      VALUES ('{Guid.NewGuid():D}', 0, 1, '{parent:D}', 'shared-call', 'request-A');
      """)); // raw-sql: exact parent/call/request association must be unique
    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO sessions (session_id, history_revision, kind, parent_session_id, owning_call_id, owning_request_id)
      VALUES ('{Guid.NewGuid():D}', 0, 1, '{parent:D}', 'shared-call', '');
      """)); // raw-sql: a supplied request identity must be nonempty
    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO sessions (session_id, history_revision, kind, owning_request_id)
      VALUES ('{Guid.NewGuid():D}', 0, 0, 'request-primary');
      """)); // raw-sql: primary sessions cannot have an owning request identity
    Assert.Equal(2L, database.Scalar($"SELECT COUNT(*) FROM sessions WHERE session_id IN ('{exactOne:D}', '{exactTwo:D}') AND owning_request_id IS NOT NULL;")); // raw-sql: distinct exact request associations must persist
  }

  [Fact]
  public void Schema39UpgradeDefaultsLanHostingAndPreservesPreferencesAfterFailure() {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path))
      database.Execute("INSERT INTO preferences (singleton, compaction_trigger_token_count) VALUES (1, 4096);"); // raw-sql: seed existing preferences
    RecreateSchema39(database);
    database.Execute("ALTER TABLE preferences ADD COLUMN lan_hosting_enabled INTEGER NOT NULL DEFAULT 0;"); // raw-sql: inject migration conflict

    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(39L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed upgrade retains the old version
    Assert.Equal(4096L, database.Scalar("SELECT compaction_trigger_token_count FROM preferences;")); // raw-sql: failed upgrade retains preferences
    database.Execute("ALTER TABLE preferences DROP COLUMN lan_hosting_enabled;");

    using UrdStore upgraded = UrdStore.Open(database.Path);
    Assert.Equal(40L, database.Scalar("PRAGMA user_version;")); // raw-sql: retry upgrades the schema
    Assert.Equal(0L, database.Scalar("SELECT lan_hosting_enabled FROM preferences;")); // raw-sql: existing preferences default to disabled
    Assert.Equal(4096L, database.Scalar("SELECT compaction_trigger_token_count FROM preferences;")); // raw-sql: migration retains preferences
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE preferences SET lan_hosting_enabled = 2;")); // raw-sql: boolean constraint
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE preferences SET lan_hosting_enabled = NULL;")); // raw-sql: non-null constraint
  }

  [Fact]
  public void FreshLanHostingColumnDefaultsToFalseAndEnforcesBoolean() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    database.Execute("INSERT INTO preferences (singleton) VALUES (1);"); // raw-sql: verify schema default
    Assert.Equal(0L, database.Scalar("SELECT lan_hosting_enabled FROM preferences;")); // raw-sql: default is disabled
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE preferences SET lan_hosting_enabled = 2;")); // raw-sql: boolean constraint
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE preferences SET lan_hosting_enabled = NULL;")); // raw-sql: non-null constraint
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
  public void ConsecutiveMigrationsUpgradeTheDatabase() {
    using var database = new TemporaryDatabase();
    database.Execute("CREATE TABLE migration_data (value TEXT NOT NULL); INSERT INTO migration_data VALUES ('before'); PRAGMA user_version = 36;");
    using var connection = OpenConnection(database);

    DatabaseMigrations.Upgrade(connection, 38, new Dictionary<int, string> {
      [36] = "ALTER TABLE migration_data ADD COLUMN migration_36 INTEGER NOT NULL DEFAULT 1;",
      [37] = "CREATE TABLE migration_marker (value TEXT NOT NULL); INSERT INTO migration_marker VALUES ('complete');"
    });

    Assert.Equal(38L, database.Scalar("PRAGMA user_version;")); // raw-sql: migration version is the subject under test
    Assert.Equal(1L, database.Scalar("SELECT migration_36 FROM migration_data;")); // raw-sql: migration result is the subject under test
    Assert.Equal("complete", database.Scalar("SELECT value FROM migration_marker;")); // raw-sql: migration result is the subject under test
  }

  [Fact]
  public void FailedMigrationRollsBackSchemaDataAndVersion() {
    using var database = new TemporaryDatabase();
    database.Execute("CREATE TABLE migration_data (value TEXT NOT NULL); INSERT INTO migration_data VALUES ('before'); PRAGMA user_version = 36;");
    using var connection = OpenConnection(database);

    Assert.Throws<SqliteException>(() => DatabaseMigrations.Upgrade(connection, 38, new Dictionary<int, string> {
      [36] = "ALTER TABLE migration_data ADD COLUMN migrated TEXT; CREATE TABLE migration_marker (value TEXT NOT NULL); UPDATE migration_data SET value = 'changed';",
      [37] = "INSERT INTO missing_migration_table VALUES ('fail');"
    }));

    Assert.Equal(36L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed migration must roll back the version
    Assert.Equal("before", database.Scalar("SELECT value FROM migration_data;")); // raw-sql: failed migration must preserve data
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('migration_data') WHERE name = 'migrated';")); // raw-sql: failed migration must roll back schema
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'migration_marker';")); // raw-sql: failed migration must roll back schema
  }

  [Fact]
  public void MissingMigrationFailsBeforeChangingDatabase() {
    using var database = new TemporaryDatabase();
    database.Execute("CREATE TABLE migration_data (value TEXT NOT NULL); INSERT INTO migration_data VALUES ('before'); PRAGMA user_version = 36;");
    using var connection = OpenConnection(database);

    Assert.Throws<InvalidOperationException>(() => DatabaseMigrations.Upgrade(connection, 38,
        new Dictionary<int, string> { [36] = "UPDATE migration_data SET value = 'changed';" }));

    Assert.Equal(36L, database.Scalar("PRAGMA user_version;")); // raw-sql: missing migration must preserve version
    Assert.Equal("before", database.Scalar("SELECT value FROM migration_data;")); // raw-sql: missing migration must not partially apply earlier steps
  }

  [Fact]
  public void OldDatabaseIsRejectedWithMatchingVersionGuidanceAndPreserved() {
    using var database = new TemporaryDatabase();
    database.Execute("PRAGMA user_version = 35;");

    NotSupportedException error = Assert.Throws<NotSupportedException>(() => UrdStore.Open(database.Path));

    Assert.Contains("Urd build that created it", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("different data folder", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("delete", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(File.Exists(database.Path));
    Assert.Equal(35L, database.Scalar("PRAGMA user_version;")); // raw-sql: rejection must preserve the old database version
  }

  [Fact]
  public void NewerDatabaseIsRejectedWithoutDowngrade() {
    using var database = new TemporaryDatabase();
    database.Execute("CREATE TABLE retained (value TEXT NOT NULL); INSERT INTO retained VALUES ('preserved'); PRAGMA user_version = 41;");

    NotSupportedException error = Assert.Throws<NotSupportedException>(() => UrdStore.Open(database.Path));

    Assert.Contains("newer Urd build", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("delete", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(41L, database.Scalar("PRAGMA user_version;")); // raw-sql: newer schema must not be downgraded
    Assert.Equal("preserved", database.Scalar("SELECT value FROM retained;")); // raw-sql: rejection must preserve newer database contents
  }

  [Fact]
  public void NonemptyUnversionedDatabaseAdvisesAnotherDataFolderAndPreservesContents() {
    using var database = new TemporaryDatabase();
    database.Execute("CREATE TABLE unrelated (value TEXT); INSERT INTO unrelated VALUES ('retained');");

    NotSupportedException error = Assert.Throws<NotSupportedException>(() => UrdStore.Open(database.Path));

    Assert.Contains("different data folder", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("delete", error.Message, StringComparison.OrdinalIgnoreCase);
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

  // Reverse newer schemas to exercise older shapes with normal seeded data.
  private static void RecreateSchema37(TemporaryDatabase database) {
    RecreateSchema38(database);
    string inputs = Assert.IsType<string>(database.Scalar("SELECT sql FROM sqlite_schema WHERE name = 'execution_inputs';")); // raw-sql: reconstruct the previous schema for the migration fixture
    var indexes = database.ReadRows("SELECT sql FROM sqlite_schema WHERE type = 'index' AND tbl_name = 'execution_inputs' AND sql IS NOT NULL;",
        row => row.GetString(0));
    database.Execute($"""
      PRAGMA foreign_keys = ON;
      ALTER TABLE execution_inputs RENAME TO newer_inputs;
      {inputs.Replace("sender_session_id TEXT,", "sender_session_id TEXT REFERENCES sessions(session_id),", StringComparison.Ordinal)};
      INSERT INTO execution_inputs SELECT * FROM newer_inputs;
      DROP TABLE newer_inputs;
      {string.Join(";\n", indexes)};
      DROP INDEX sessions_children;
      DROP VIEW global_usage_totals;
      DROP VIEW open_usage_totals;
      DROP TABLE closed_usage_totals;
      DROP TABLE closed_session_usage;
      PRAGMA user_version = 37;
      """);
  }

  private static void RecreateSchema39(TemporaryDatabase database) =>
      database.Execute("ALTER TABLE preferences DROP COLUMN lan_hosting_enabled; PRAGMA user_version = 39;"); // raw-sql: reconstruct the schema 39 fixture

  private static void RecreateSchema38(TemporaryDatabase database) {
    RecreateSchema39(database);
    database.Execute("DROP INDEX sessions_owning_request; DROP INDEX execution_tools_subagent_started; ALTER TABLE sessions DROP COLUMN owning_request_id; PRAGMA user_version = 38;"); // raw-sql: reconstruct the schema 38 fixture
  }

  private static SqliteConnection OpenConnection(TemporaryDatabase database) {
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
      DataSource = database.Path,
      Pooling = false
    }.ToString());
    connection.Open();
    return connection;
  }
}
