using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class ProviderMigrationTests {
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void GenuineSchemaFiveMigrationPreservesSessionsAndRollsBackNewTablesOnFailure(bool failMigration) {
    using var database = new TemporaryDatabase();
    SchemaFiveDatabase.Create(database);
    Assert.Equal(5L, database.Scalar("PRAGMA user_version;")); // raw-sql: frozen fixture is genuinely schema five
    object? root = database.Scalar("SELECT rootpage FROM sqlite_schema WHERE name = 'sessions';"); // raw-sql: migration must not rebuild the session root
    var sessions = StoredSessions.Read(database);
    var creation = StoredSessions.ReadCreation(database);
    var submissions = StoredSubmissions.Read(database);
    var executions = StoredExecutions.Read(database);
    var requests = StoredExecutions.ReadRequests(database);
    var deliveries = StoredDeliveries.Read(database);
    var messages = StoredDeliveries.ReadMessages(database);
    var tools = StoredTools.Read(database);
    if (failMigration) {
      // Fail after the connection table has been created, not before migration starts.
      database.Execute("CREATE TABLE provider_host (witness TEXT); INSERT INTO provider_host VALUES ('fixture');");
      Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
      Assert.Equal(5L, database.Scalar("PRAGMA user_version;")); // raw-sql: migration must roll back the schema version
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'provider_connections';")); // raw-sql: migration DDL must roll back
      Assert.Equal("fixture", database.Scalar("SELECT witness FROM provider_host;")); // raw-sql: fixture-owned fault must survive unchanged
      AssertLegacyEvidence();
      database.Execute("DROP TABLE provider_host;");
    }

    ProviderConnection connection = new(Guid.NewGuid(), "synthetic", ProviderRoute.OpenAI,
        LlmApi.OpenAIResponses, new Uri("https://api.example.invalid/v1/"), AuthenticationMethod.ApiKey);
    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Equal(8L, database.Scalar("PRAGMA user_version;")); // raw-sql: successful upgrade version is the subject under test
      AssertLegacyEvidence();
      Assert.Empty(store.Providers.List());
      Assert.Empty(StoredProviders.ReadHostIds(database));
      Assert.Equal(SchemaFiveDatabase.SessionId, Assert.Single(store.Sessions.ListPrimaryIds(null).SessionIds));
      Assert.Equal(new UserMessageSubmission(SchemaFiveDatabase.InputId, 7, SchemaFiveDatabase.Original, null),
          store.UserMessages.ReadReceipt(SchemaFiveDatabase.SessionId, SchemaFiveDatabase.InputId)!.Submission);
      SessionSnapshot snapshot = store.Snapshots.ReadStored(SchemaFiveDatabase.SessionId);
      Assert.Equal(new[] { SchemaFiveDatabase.Original, "\0🙂" }, snapshot.Messages.Select(message => message.Preview));
      Assert.Equal(ExecutionOutcome.Failed, snapshot.LastSettlement!.Outcome);
      store.Providers.Add(connection);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(connection, Assert.Single(reopened.Providers.List()));
    AssertLegacyEvidence();

    void AssertLegacyEvidence() {
      Assert.Equal(sessions, StoredSessions.Read(database));
      Assert.Equal(creation, StoredSessions.ReadCreation(database));
      Assert.Equal(submissions, StoredSubmissions.Read(database));
      Assert.Equal(executions, StoredExecutions.Read(database));
      Assert.Equal(requests, StoredExecutions.ReadRequests(database));
      Assert.Equal(deliveries, StoredDeliveries.Read(database));
      Assert.Equal(messages, StoredDeliveries.ReadMessages(database));
      Assert.Equal(tools, StoredTools.Read(database));
      Assert.Equal("\0🙂", StoredLlmResponses.ReadText(database));
      Assert.Equal(root, database.Scalar("SELECT rootpage FROM sqlite_schema WHERE name = 'sessions';")); // raw-sql: existing root table was not rebuilt
      Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: migrated and original relationships must remain valid
    }
  }

  [Theory]
  [InlineData("provider_connections")]
  [InlineData("provider_host")]
  public void SchemaEightVerifiesProviderTableShapeRatherThanSilentlyRecreatingIt(string table) {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path))
      store.Sessions.CreatePrimary(SchemaFiveDatabase.SessionId);
    database.Execute($"DROP TABLE {table}; CREATE TABLE {table} (unrelated TEXT);");
    Assert.Throws<SqliteException>(() => {
      using UrdStore store = UrdStore.Open(database.Path);
    });
    Assert.Equal(8L, database.Scalar("PRAGMA user_version;")); // raw-sql: rejecting malformed current schema must not rewrite its version
    Assert.Equal(SchemaFiveDatabase.SessionId, Assert.Single(StoredSessions.Read(database)).Id);
  }
}
