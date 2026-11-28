using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class DatabaseSchemaTests {
  [Fact]
  public void FreshDatabaseCreatesAndReopensCurrentSchema() {
    using var database = new TemporaryDatabase();
    Guid primary = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: fresh open must initialize the current schema version
      store.Sessions.CreatePrimary(primary);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: reopening must retain the current schema version
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
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: successful migrations must advance to the current version
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
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: successful retry must advance the schema version
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
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: schema 38 upgrade must advance to the current schema
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
  public void Schema40UpgradeRecoversFromFailure() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path))
      store.Sessions.CreatePrimary(session);
    RecreateSchema42(database);
    database.Execute("ALTER TABLE input_decisions DROP COLUMN image_ids_json; DROP TABLE user_images; DROP TABLE mcp_servers; PRAGMA user_version = 40;");
    database.Execute("ALTER TABLE input_decisions ADD COLUMN image_ids_json TEXT;"); // raw-sql: fail after creating the image table
    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(40L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed upgrade retains its source version
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'user_images';")); // raw-sql: failed upgrade rolls back the image table
    database.Execute("ALTER TABLE input_decisions DROP COLUMN image_ids_json;");
    using UrdStore upgraded = UrdStore.Open(database.Path);
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: successful retry advances to the current version
    Assert.Equal(1L, database.Scalar($"SELECT COUNT(*) FROM sessions WHERE session_id = '{session:D}';"));
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'user_images';")); // raw-sql: successful retry creates the image table
  }

  [Fact]
  public void Schema41UpgradeRecoversFromFailureAndPreservesData() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path))
      store.Sessions.CreatePrimary(session);
    RecreateSchema42(database);
    database.Execute("DROP TABLE mcp_servers; PRAGMA user_version = 41;"); // raw-sql: recreate the schema 41 baseline
    database.Execute("CREATE VIEW mcp_servers AS SELECT 'preserved' AS value;"); // raw-sql: make the schema 41 migration fail

    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(41L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed upgrade retains its source version
    Assert.Equal("preserved", database.Scalar("SELECT value FROM mcp_servers;")); // raw-sql: failed upgrade preserves the conflicting object
    Assert.Equal(1L, database.Scalar($"SELECT COUNT(*) FROM sessions WHERE session_id = '{session:D}';")); // raw-sql: failed upgrade preserves application data
    database.Execute("DROP VIEW mcp_servers;");

    using UrdStore upgraded = UrdStore.Open(database.Path);
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: successful retry advances to the current version
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'mcp_servers';")); // raw-sql: successful retry creates the MCP server table
    Assert.Equal(1L, database.Scalar($"SELECT COUNT(*) FROM sessions WHERE session_id = '{session:D}';")); // raw-sql: upgrade preserves application data
  }

  [Fact]
  public void McpServerStoragePersistsOpaqueProtectedPayloads() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var bearer = new SavedMcpServer(Guid.NewGuid(), "Bearer", "https://bearer.example.invalid/mcp",
        "bearerToken", ["search"], null, "opaque-credential-ciphertext", null);
    var automatic = new SavedMcpServer(Guid.NewGuid(), "Automatic", "https://automatic.example.invalid/mcp",
        "automatic", [], null, null, "opaque-oauth-ciphertext");

    Assert.True(store.McpServers.Save(bearer, existing: false));
    Assert.True(store.McpServers.Save(automatic, existing: false));
    Assert.False(store.McpServers.Save(bearer, existing: false));
    SavedMcpServer storedBearer = Assert.Single(store.McpServers.List(), server => server.Id == bearer.Id);
    Assert.Equal("opaque-credential-ciphertext", storedBearer.Credential);
    Assert.Equal(new[] { "search" }, storedBearer.Tools);
    Assert.Equal("opaque-oauth-ciphertext", store.McpServers.Find(automatic.Id)!.OAuthTokens);

    SavedMcpServer updated = bearer with { Name = "Renamed", Tools = ["search", "read"] };
    Assert.True(store.McpServers.Save(updated, existing: true));
    Assert.False(store.McpServers.Save(updated with { Id = Guid.NewGuid() }, existing: true));
    Assert.Equal("Renamed", store.McpServers.Find(bearer.Id)!.Name);
    Assert.True(store.McpServers.Remove(bearer.Id));
    Assert.False(store.McpServers.Remove(bearer.Id));
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
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: retry upgrades the schema
    Assert.Equal(0L, database.Scalar("SELECT lan_hosting_enabled FROM preferences;")); // raw-sql: existing preferences default to disabled
    Assert.Equal(4096L, database.Scalar("SELECT compaction_trigger_token_count FROM preferences;")); // raw-sql: migration retains preferences
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE preferences SET lan_hosting_enabled = 2;")); // raw-sql: boolean constraint
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE preferences SET lan_hosting_enabled = NULL;")); // raw-sql: non-null constraint
  }

  [Fact]
  public void Schema42UpgradeDefaultsSourceIgnoresAndPreservesPreferencesAfterFailure() {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      preferences.SetLanHostingEnabled(true);
      preferences.SetCompactionTriggerTokenCount(4096);
    }
    RecreateSchema42(database);
    database.Execute("ALTER TABLE preferences ADD COLUMN source_ignores_json TEXT;"); // raw-sql: inject a migration conflict
    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(42L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed upgrade retains its source version
    database.Execute("ALTER TABLE preferences DROP COLUMN source_ignores_json;");

    using UrdStore upgraded = UrdStore.Open(database.Path);
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: successful retry advances to the current version
    Assert.Equal(1L, database.Scalar("SELECT source_ignores_json IS NULL FROM preferences;")); // raw-sql: existing rows get the nullable column without a data rewrite
    UserPreferences saved = new PreferenceManager(upgraded).Read();
    Assert.True(saved.LanHostingEnabled);
    Assert.Equal(4096, saved.CompactionTriggerTokenCount);
    Assert.Empty(saved.SourceIgnores.Instructions);
    Assert.Empty(saved.SourceIgnores.Skills);
    Assert.Equal(new[] { "CLAUDE.md", "GEMINI.md" }, saved.SourceIgnores.Filenames);
  }

  [Fact]
  public void SnoozeMigrationRejectsAConflictingColumnAndRetainsExistingSessions() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path))
      store.Sessions.CreatePrimary(sessionId);

    RecreateSchema43(database);
    database.Execute("ALTER TABLE sessions ADD COLUMN snoozed_until_ms TEXT;"); // raw-sql: inject an incompatible column
    Assert.Throws<SqliteException>(() => {
      using UrdStore attempted = UrdStore.Open(database.Path);
    });
    Assert.Equal(43L, database.Scalar("PRAGMA user_version;")); // raw-sql: migration failure preserves the source version
    Assert.Equal(sessionId.ToString("D"), database.Scalar("SELECT session_id FROM sessions;")); // raw-sql: failed migration preserves the existing session
    database.Execute("ALTER TABLE sessions DROP COLUMN snoozed_until_ms;");

    using UrdStore upgraded = UrdStore.Open(database.Path);
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: removing the conflict permits upgrade
    Assert.Null(upgraded.Sessions.Read(sessionId)!.SnoozedUntil);
    Assert.Equal("snoozed_until_ms", database.Scalar("SELECT name FROM pragma_index_info('sessions_snoozed_until');")); // raw-sql: the migration indexes the actual deadline
  }

  [Fact]
  public void Schema44DepthMigrationPreservesPreferencesAndRecoversFromFailure() {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path))
      new PreferenceManager(store).SetCompactionTriggerTokenCount(4096);
    RecreateSchema44(database);
    database.Execute("ALTER TABLE preferences ADD COLUMN max_subagent_depth TEXT;"); // raw-sql: inject migration conflict
    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(44L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed migration retains version
    Assert.Equal(4096L, database.Scalar("SELECT compaction_trigger_token_count FROM preferences;")); // raw-sql: failed migration retains data
    database.Execute("ALTER TABLE preferences DROP COLUMN max_subagent_depth;");
    using UrdStore upgraded = UrdStore.Open(database.Path);
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: retry succeeds
    Assert.Equal(4, new PreferenceManager(upgraded).Read().MaxSubagentDepth);
    Assert.Equal(4096, new PreferenceManager(upgraded).Read().CompactionTriggerTokenCount);
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE preferences SET max_subagent_depth = -1;"));
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE preferences SET max_subagent_depth = 2147483648;"));
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE preferences SET max_subagent_depth = NULL;"));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void QueuedExecutionConstraintsApplyToFreshAndMigratedSchemas(bool migrate) {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path))
      store.Sessions.CreatePrimary(session);
    database.Execute($"""
      INSERT INTO execution_runs (session_id, run_id, history_revision, kind, llm_selection_json)
      VALUES ('{session:D}', 'compaction', 0, 1, 'selection');
      INSERT INTO input_decisions (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, llm_selection_json)
      VALUES ('{session:D}', 'input1', 0, X'00', 1, 'queued1', 'selection'),
             ('{session:D}', 'input2', 0, X'00', 1, 'queued2', 'selection');
      """); // raw-sql: populate a schema 45-compatible active compaction and inputs

    if (migrate) {
      RecreateSchema45(database);
      Assert.Equal(45L, database.Scalar("PRAGMA user_version;")); // raw-sql: start with the previous schema
      database.Execute("DROP INDEX one_active_execution;"); // fail after the queued-run column is added
      Assert.Throws<SqliteException>(() => {
        using UrdStore attempted = UrdStore.Open(database.Path);
      });
      Assert.Equal(45L, database.Scalar("PRAGMA user_version;")); // raw-sql: a failed upgrade keeps its version
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('execution_runs') WHERE name = 'queued_after_compaction';")); // raw-sql: column addition rolled back
      Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_runs WHERE run_id = 'compaction';")); // raw-sql: failure preserves saved work
      database.Execute("CREATE UNIQUE INDEX one_active_execution ON execution_runs(session_id) WHERE outcome IS NULL;");
      using UrdStore upgraded = UrdStore.Open(database.Path);
      Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: retry upgrades the same profile
    }

    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_list('execution_runs') WHERE \"from\" = 'queued_after_compaction' AND \"table\" = 'execution_runs' AND \"to\" = 'run_id' AND on_delete = 'NO ACTION';")); // raw-sql: verify the queue relationship is enforced
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_runs WHERE run_id = 'compaction' AND queued_after_compaction IS NULL;")); // raw-sql: existing runs remain active, not queued
    database.Execute($"""
      PRAGMA foreign_keys = ON;
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, queued_after_compaction)
      VALUES ('{session:D}', 'queued1', 0, 'input1', 'compaction');
      """);
    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, queued_after_compaction)
      VALUES ('{session:D}', 'queued2', 0, 'input2', 'compaction');
      """));
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE execution_runs SET working_started_at = 'started' WHERE run_id = 'queued1';"));
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE execution_runs SET queued_after_compaction = 'compaction' WHERE run_id = 'compaction';"));
    Assert.Throws<SqliteException>(() => database.Execute("PRAGMA foreign_keys = ON; UPDATE execution_runs SET queued_after_compaction = 'missing' WHERE run_id = 'queued1';"));
    Assert.Throws<SqliteException>(() => database.Execute("PRAGMA foreign_keys = ON; DELETE FROM execution_runs WHERE run_id = 'compaction';"));
    Assert.Throws<SqliteException>(() => database.Execute($"""
      INSERT INTO execution_runs (session_id, run_id, history_revision, kind, llm_selection_json)
      VALUES ('{session:D}', 'other-active', 0, 1, 'selection');
      """));
    database.Execute($"""
      UPDATE execution_runs SET outcome = 0 WHERE run_id = 'queued1';
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, queued_after_compaction)
      VALUES ('{session:D}', 'queued2', 0, 'input2', 'compaction');
      PRAGMA foreign_keys = ON;
      DELETE FROM execution_runs WHERE run_id IN ('compaction', 'queued1', 'queued2');
      """);
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;")); // raw-sql: closure can remove the entire linked pair
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void ProviderRouteAndApiConstraintsApplyToFreshAndMigratedSchemas(bool migrate) {
    using var database = new TemporaryDatabase();
    UrdStore.Open(database.Path).Dispose();
    if (migrate) {
      RecreateSchema46(database);
      using UrdStore upgraded = UrdStore.Open(database.Path);
    }

    database.Execute("""
      INSERT INTO provider_connections
          (connection_id, name, route, api, endpoint, authentication, generation, credential_version, disconnecting)
      VALUES ('copilot', 'Copilot', 3, 0, 'https://example.invalid', 0, 0, 0, 0);
      """); // raw-sql: route 3 is accepted on fresh and upgraded databases
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE provider_connections SET route = 4 WHERE connection_id = 'copilot';"));
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE provider_connections SET api = 2 WHERE connection_id = 'copilot';"));
    database.Execute("UPDATE provider_connections SET api = 1 WHERE connection_id = 'copilot';");
    Assert.Equal(1L, database.Scalar("SELECT api FROM provider_connections WHERE connection_id = 'copilot';")); // raw-sql: verify the existing API constraint is unchanged
  }

  [Fact]
  public void Schema46UpgradePreservesProviderDataAndRecoversFromFailure() {
    using var database = new TemporaryDatabase();
    UrdStore.Open(database.Path).Dispose();
    RecreateSchema46(database);
    database.Execute("""
      INSERT INTO provider_connections VALUES
          ('connection', 'Provider', 2, 1, 'https://example.invalid', 1, 7, 8,
           'registration', 'credentials', 1, 'catalog');
      INSERT INTO model_settings VALUES ('connection', 'model', 3, 1, 12345);
      CREATE VIEW next_model_settings AS SELECT 'preserved' AS value;
      """); // raw-sql: seed every connection field and a dependent model, then fail after the copy begins

    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(46L, database.Scalar("PRAGMA user_version;")); // raw-sql: the failed upgrade retains the prior version
    Assert.Equal("preserved", database.Scalar("SELECT value FROM next_model_settings;")); // raw-sql: the preexisting obstruction survives rollback
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'next_provider_connections';")); // raw-sql: the replacement table rolls back
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM model_settings WHERE connection_id = 'connection' AND model_id = 'model';")); // raw-sql: a failed migration retains the seeded model row
    database.Execute("DROP VIEW next_model_settings;");

    using UrdStore upgraded = UrdStore.Open(database.Path);
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: retry advances the same profile
    const string retainedConnection = """
        SELECT COUNT(*) FROM provider_connections WHERE connection_id = 'connection' AND name = 'Provider'
            AND route = 2 AND api = 1 AND endpoint = 'https://example.invalid' AND authentication = 1
            AND generation = 7 AND credential_version = 8 AND registration_json = 'registration'
            AND credentials_json = 'credentials' AND disconnecting = 1 AND catalog_json = 'catalog';
        """;
    Assert.Equal(1L, database.Scalar(retainedConnection)); // raw-sql: compare every column of the deliberately opaque migration fixture
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM model_settings WHERE connection_id = 'connection' AND model_id = 'model' AND family = 3 AND supports_images = 1 AND context_window = 12345;")); // raw-sql: migration retains the dependent row and its values
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_list('model_settings') WHERE \"from\" = 'connection_id' AND \"table\" = 'provider_connections' AND \"to\" = 'connection_id' AND on_delete = 'CASCADE';")); // raw-sql: SQLite must bind the foreign key to the renamed table
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;")); // raw-sql: verify foreign-key integrity on the migrated profile
    Assert.Throws<SqliteException>(() => database.Execute("PRAGMA foreign_keys = ON; INSERT INTO model_settings VALUES ('missing', 'model', 0, 0, NULL);"));
    database.Execute("PRAGMA foreign_keys = ON; DELETE FROM provider_connections WHERE connection_id = 'connection';");
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM model_settings;")); // raw-sql: the migrated foreign key still cascades deletion
  }

  [Fact]
  public void Schema48ModelFamilyMigrationPreservesOverridesAndRecoversFromFailure() {
    using var database = new TemporaryDatabase();
    UrdStore.Open(database.Path).Dispose();
    database.Execute("""
      INSERT INTO provider_connections
          (connection_id, name, route, api, endpoint, authentication, generation, credential_version, disconnecting)
      VALUES ('provider', 'Provider', 2, 0, 'https://example.invalid', 0, 0, 0, 0);
      INSERT INTO model_settings
          (connection_id, model_id, family, supports_images, context_window, family_explicit)
      VALUES ('provider', 'model', 3, 0, NULL, 1);
      """);
    RecreateSchema48(database);
    database.Execute("ALTER TABLE model_settings ADD COLUMN family_explicit TEXT;"); // raw-sql: inject a conflicting column

    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(48L, database.Scalar("PRAGMA user_version;")); // raw-sql: failure retains the prior version
    Assert.Equal("TEXT", database.Scalar("SELECT type FROM pragma_table_info('model_settings') WHERE name = 'family_explicit';")); // raw-sql: failure preserves the conflict
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM model_settings WHERE model_id = 'model' AND family = 3;")); // raw-sql: failure preserves the explicit family
    database.Execute("ALTER TABLE model_settings DROP COLUMN family_explicit;");

    using UrdStore upgraded = UrdStore.Open(database.Path);
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: retry advances the same profile
    Assert.Equal(1L, database.Scalar("SELECT family_explicit FROM model_settings WHERE model_id = 'model';")); // raw-sql: existing settings remain explicit
  }

  [Fact]
  public void Schema47FavoritesMigrationPreservesPreferencesAndRecoversFromFailure() {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path))
      new PreferenceManager(store).SetCompactionTriggerTokenCount(4096);
    RecreateSchema47(database);
    database.Execute("ALTER TABLE preferences ADD COLUMN model_favorites_json INTEGER;"); // raw-sql: inject a conflicting column

    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(47L, database.Scalar("PRAGMA user_version;")); // raw-sql: failure retains the prior version
    Assert.Equal(4096L, database.Scalar("SELECT compaction_trigger_token_count FROM preferences;")); // raw-sql: failure preserves data
    Assert.Equal("INTEGER", database.Scalar("SELECT type FROM pragma_table_info('preferences') WHERE name = 'model_favorites_json';")); // raw-sql: failure preserves the conflict
    database.Execute("ALTER TABLE preferences DROP COLUMN model_favorites_json;");

    using UrdStore upgraded = UrdStore.Open(database.Path);
    Assert.Equal(49L, database.Scalar("PRAGMA user_version;")); // raw-sql: retry advances the same profile
    Assert.Equal(4096L, database.Scalar("SELECT compaction_trigger_token_count FROM preferences;")); // raw-sql: migration preserves data
    Assert.Equal(1L, database.Scalar("SELECT model_favorites_json IS NULL FROM preferences;")); // raw-sql: existing rows default to null
    Assert.Equal("TEXT", database.Scalar("SELECT type FROM pragma_table_info('preferences') WHERE name = 'model_favorites_json';")); // raw-sql: verify migrated column type
    Assert.Equal(0L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('preferences') WHERE name = 'model_favorites_json';")); // raw-sql: verify migrated column is nullable
    database.Execute("UPDATE preferences SET model_favorites_json = '[\"model\"]';");
    Assert.Equal("[\"model\"]", database.Scalar("SELECT model_favorites_json FROM preferences;")); // raw-sql: verify the column stores JSON
  }

  [Fact]
  public void FreshLanHostingColumnDefaultsToFalseAndEnforcesBoolean() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    database.Execute("INSERT INTO preferences (singleton) VALUES (1);"); // raw-sql: verify schema default
    Assert.Equal(0L, database.Scalar("SELECT lan_hosting_enabled FROM preferences;")); // raw-sql: default is disabled
    Assert.Equal(4L, database.Scalar("SELECT max_subagent_depth FROM preferences;")); // raw-sql: fresh default
    Assert.Equal("TEXT", database.Scalar("SELECT type FROM pragma_table_info('preferences') WHERE name = 'model_favorites_json';")); // raw-sql: verify fresh column type
    Assert.Equal(0L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('preferences') WHERE name = 'model_favorites_json';")); // raw-sql: verify fresh column is nullable
    Assert.Equal(1L, database.Scalar("SELECT model_favorites_json IS NULL FROM preferences;")); // raw-sql: verify the default is null
    Assert.Equal(1L, database.Scalar("SELECT \"notnull\" FROM pragma_table_info('model_settings') WHERE name = 'family_explicit';")); // raw-sql: family source is always known
    Assert.Equal("1", database.Scalar("SELECT dflt_value FROM pragma_table_info('model_settings') WHERE name = 'family_explicit';")); // raw-sql: manually saved settings default to explicit
    Assert.Contains("family_explicit IN (0, 1)", Assert.IsType<string>(database.Scalar(
        "SELECT sql FROM sqlite_schema WHERE name = 'model_settings';")), StringComparison.Ordinal); // raw-sql: boolean constraint
    database.Execute("UPDATE preferences SET max_subagent_depth = 0;"); // raw-sql: zero is valid
    database.Execute("UPDATE preferences SET max_subagent_depth = 2147483647;"); // raw-sql: maximum int is valid
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE preferences SET max_subagent_depth = -1;"));
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE preferences SET max_subagent_depth = 2147483648;"));
    Assert.Throws<SqliteException>(() => database.Execute("UPDATE preferences SET max_subagent_depth = NULL;"));
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

    DatabaseMigrations.Upgrade(connection, 38, new Dictionary<int, string[]> {
      [36] = ["ALTER TABLE migration_data ADD COLUMN migration_36 INTEGER NOT NULL DEFAULT 1;"],
      [37] = ["CREATE TABLE migration_marker (value TEXT NOT NULL);", "INSERT INTO migration_marker VALUES ('complete');"]
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

    Assert.Throws<SqliteException>(() => DatabaseMigrations.Upgrade(connection, 38, new Dictionary<int, string[]> {
      [36] = ["ALTER TABLE migration_data ADD COLUMN migrated TEXT; CREATE TABLE migration_marker (value TEXT NOT NULL); UPDATE migration_data SET value = 'changed';"],
      [37] = ["INSERT INTO missing_migration_table VALUES ('fail');"]
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
        new Dictionary<int, string[]> { [36] = ["UPDATE migration_data SET value = 'changed';"] }));

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
    database.Execute("CREATE TABLE retained (value TEXT NOT NULL); INSERT INTO retained VALUES ('preserved'); PRAGMA user_version = 50;");

    NotSupportedException error = Assert.Throws<NotSupportedException>(() => UrdStore.Open(database.Path));

    Assert.Contains("newer Urd build", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("delete", error.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(50L, database.Scalar("PRAGMA user_version;")); // raw-sql: newer schema must not be downgraded
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

  private static void RecreateSchema46(TemporaryDatabase database) {
    RecreateSchema47(database);
    string connections = Assert.IsType<string>(database.Scalar("SELECT sql FROM sqlite_schema WHERE name = 'provider_connections';")); // raw-sql: recover the DDL to reconstruct the version 46 route constraint
    string models = Assert.IsType<string>(database.Scalar("SELECT sql FROM sqlite_schema WHERE name = 'model_settings';")); // raw-sql: retain the original dependent-table DDL in the fixture
    string legacyConnections = connections.Replace("route IN (0, 1, 2, 3)", "route IN (0, 1, 2)", StringComparison.Ordinal);
    Assert.NotEqual(connections, legacyConnections);
    database.Execute($"""
      DROP TABLE model_settings;
      DROP TABLE provider_connections;
      {legacyConnections};
      {models};
      PRAGMA user_version = 46;
      """); // raw-sql: reconstruct the previous provider schema before seeding its data
  }

  private static void RecreateSchema48(TemporaryDatabase database) =>
      database.Execute("ALTER TABLE model_settings DROP COLUMN family_explicit; PRAGMA user_version = 48;"); // raw-sql: reconstruct schema 48

  private static void RecreateSchema47(TemporaryDatabase database) {
    RecreateSchema48(database);
    database.Execute("ALTER TABLE preferences DROP COLUMN model_favorites_json; PRAGMA user_version = 47;"); // raw-sql: reconstruct schema 47
  }

  private static void RecreateSchema45(TemporaryDatabase database) {
    RecreateSchema47(database);
    database.Execute("""
        DROP INDEX one_active_execution;
        DROP INDEX one_queued_execution_per_compaction;
        ALTER TABLE execution_runs DROP COLUMN queued_after_compaction;
        CREATE UNIQUE INDEX one_active_execution ON execution_runs(session_id) WHERE outcome IS NULL;
        PRAGMA user_version = 45;
        """); // raw-sql: reconstruct the schema 45 fixture
  }

  private static void RecreateSchema44(TemporaryDatabase database) {
    RecreateSchema45(database);
    database.Execute("ALTER TABLE preferences DROP COLUMN max_subagent_depth; PRAGMA user_version = 44;"); // raw-sql: reconstruct the schema 44 fixture
  }

  private static void RecreateSchema43(TemporaryDatabase database) {
    RecreateSchema44(database);
    database.Execute("DROP INDEX sessions_snoozed_until; ALTER TABLE sessions DROP COLUMN snoozed_until_ms; PRAGMA user_version = 43;"); // raw-sql: reconstruct the schema 43 fixture
  }

  private static void RecreateSchema42(TemporaryDatabase database) {
    RecreateSchema43(database);
    database.Execute("ALTER TABLE preferences DROP COLUMN source_ignores_json; PRAGMA user_version = 42;"); // raw-sql: reconstruct the schema 42 fixture
  }

  private static void RecreateSchema39(TemporaryDatabase database) {
    RecreateSchema42(database);
    database.Execute("ALTER TABLE input_decisions DROP COLUMN image_ids_json; DROP TABLE user_images; DROP TABLE mcp_servers; ALTER TABLE preferences DROP COLUMN lan_hosting_enabled; PRAGMA user_version = 39;"); // raw-sql: reconstruct the schema 39 fixture
  }

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
