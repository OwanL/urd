using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class LlmProviderContextMigrationTests {
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void SchemaSixMigrationPreservesGenericEvidenceAndRollsBackOnTableCollision(bool failMigration) {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), execution = Guid.NewGuid(), response = Guid.NewGuid(), request;
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "legacy input \0 🙂\r\n", TestLlmSelection.Default);
    const string answer = "legacy answer \0 🙂\r\n";
    var provider = new ProviderConnection(Guid.NewGuid(), "synthetic", ProviderRoute.Custom,
        LlmApi.OpenAIResponses, new Uri("https://provider.example.invalid/v1/"), AuthenticationMethod.ApiKey);
    string hostId;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      store.UserMessages.Admit(session, 0, submission, true, execution);
      request = store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId;
      Assert.True(store.LlmResponses.Append(session, execution, 0, new(request, response, 0, answer, true)));
      Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, new(request, ExecutionOutcome.Completed)));
      Assert.Equal(ExecutionOutcome.Completed, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
      ProviderState state = store.Providers.Add(provider);
      store.Providers.SetCredentials(provider.Id, state.Generation, null,
          new("synthetic-api-key", null, null, null, null, []));
      hostId = store.Providers.GetOrCreateHostId();
    }

    // Restore schema six without native provider context or persisted LLM selection.
    MakeSchemaSix(database);
    Assert.Equal(6L, database.Scalar("PRAGMA user_version;")); // raw-sql: fixture must have the actual previous schema version
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'execution_provider_context';")); // raw-sql: fixture must lack the new table
    var sessions = StoredSessions.Read(database);
    var creation = StoredSessions.ReadCreation(database);
    var submissions = StoredSubmissions.Read(database);
    var executions = StoredExecutions.Read(database);
    var requests = StoredExecutions.ReadRequests(database);
    var deliveries = StoredDeliveries.Read(database);
    var messages = StoredDeliveries.ReadMessages(database);
    var order = StoredDeliveries.ReadRequestOrder(database);
    var providers = StoredProviders.Read(database);
    if (failMigration) {
      database.Execute("CREATE TABLE execution_provider_context (witness TEXT); INSERT INTO execution_provider_context VALUES ('retained');");
      Assert.Throws<SqliteException>(() => {
        using UrdStore store = UrdStore.Open(database.Path);
      });
      Assert.Equal(6L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed migration must roll back its version
      Assert.Equal("retained", database.Scalar("SELECT witness FROM execution_provider_context;")); // raw-sql: fixture-owned collision must survive rollback
      AssertLegacyEvidence();
      AssertForeignKeysRemainEnforced(database);
      database.Execute("DROP TABLE execution_provider_context;");
    }

    using (UrdStore store = UrdStore.Open(database.Path)) {
      AssertUpgraded(store);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    AssertUpgraded(reopened);

    void AssertUpgraded(UrdStore store) {
      Assert.Equal(8L, database.Scalar("PRAGMA user_version;")); // raw-sql: successful migration version is under test
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_provider_context;")); // raw-sql: generic legacy requests must not acquire native context
      AssertLegacyEvidence();
      AssertForeignKeysRemainEnforced(database);
      Assert.Equal(new UserMessageSubmission(submission.SubmissionId, 0, submission.Text, null),
          store.UserMessages.ReadReceipt(session, submission.SubmissionId)!.Submission);
      Assert.Equal(execution, store.UserMessages.ReadReceipt(session, submission.SubmissionId)!.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed, store.Executions.ReadSettlement(session, execution)!.Outcome);
      Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
      Assert.Equal(provider, Assert.Single(store.Providers.List()));
      Assert.Equal(hostId, store.Providers.GetOrCreateHostId());
      SessionSnapshot snapshot = store.Snapshots.ReadStored(session);
      Assert.Equal(new[] { submission.Text, answer }, snapshot.Messages.Select(message => message.Preview));
      Assert.Equal(ExecutionOutcome.Completed, snapshot.LastSettlement!.Outcome);
    }

    void AssertLegacyEvidence() {
      Assert.Equal(sessions, StoredSessions.Read(database));
      Assert.Equal(creation, StoredSessions.ReadCreation(database));
      Assert.Equal(submissions, StoredSubmissions.Read(database));
      Assert.Equal(executions, StoredExecutions.Read(database));
      Assert.Equal(requests, StoredExecutions.ReadRequests(database));
      Assert.Equal(deliveries, StoredDeliveries.Read(database));
      Assert.Equal(messages, StoredDeliveries.ReadMessages(database));
      Assert.Equal(order, StoredDeliveries.ReadRequestOrder(database));
      Assert.Equal(providers, StoredProviders.Read(database));
      Assert.Equal(hostId, Assert.Single(StoredProviders.ReadHostIds(database)));
      var output = Assert.Single(StoredLlmResponses.Read(database));
      Assert.Equal(request, output.RequestId);
      Assert.Equal(response, output.ResponseId);
      Assert.Equal(0L, output.ChunkIndex);
      Assert.Equal(StorageText.StrictUtf8.GetBytes(answer), output.Bytes);
      Assert.True(output.Final);
      Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: migration and rollback must preserve referential integrity
    }
  }

  [Fact]
  public void SchemaEightRejectsMalformedProviderContextWithoutRewritingEvidence() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path))
      store.Sessions.CreatePrimary(session);
    database.Execute("DROP TABLE execution_provider_context; CREATE TABLE execution_provider_context (witness TEXT); INSERT INTO execution_provider_context VALUES ('retained');");

    Assert.Throws<SqliteException>(() => {
      using UrdStore store = UrdStore.Open(database.Path);
    });
    Assert.Equal(8L, database.Scalar("PRAGMA user_version;")); // raw-sql: rejecting malformed current schema must not rewrite its version
    Assert.Equal("retained", database.Scalar("SELECT witness FROM execution_provider_context;")); // raw-sql: malformed table must not be silently recreated
    Assert.Equal(session, Assert.Single(StoredSessions.Read(database)).Id);
    Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: rejection must preserve existing relationships
  }

  private static void MakeSchemaSix(TemporaryDatabase database) => database.Execute("""
    PRAGMA foreign_keys = OFF;
    BEGIN;
    DROP TABLE execution_provider_context;
    CREATE TABLE input_decisions_v6 (
        session_id TEXT NOT NULL REFERENCES sessions(session_id), input_id TEXT NOT NULL,
        expected_history_revision INTEGER NOT NULL CHECK (expected_history_revision >= 0),
        original_text_utf8 BLOB NOT NULL CHECK (length(original_text_utf8) <= 16384),
        decision INTEGER NOT NULL CHECK (decision IN (1, 2)), run_id TEXT,
        refusal_reason INTEGER CHECK (refusal_reason IN (1, 2, 3)), PRIMARY KEY (session_id, input_id),
        CHECK ((decision = 1 AND run_id IS NOT NULL AND run_id <> '00000000-0000-0000-0000-000000000000'
            AND refusal_reason IS NULL) OR (decision = 2 AND run_id IS NULL AND refusal_reason IS NOT NULL))
    ) STRICT;
    INSERT INTO input_decisions_v6
    SELECT session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason
    FROM input_decisions;
    DROP TABLE input_decisions;
    ALTER TABLE input_decisions_v6 RENAME TO input_decisions;
    PRAGMA user_version = 6;
    COMMIT;
    PRAGMA foreign_keys = ON;
    """);

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
  }
}
