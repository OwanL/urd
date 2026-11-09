using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class ProviderMigrationTests {
  [Fact]
  public void FrozenSchemaFiveUpgradesAndRollsBackProviderCreationOnCollision() {
    using var database = new TemporaryDatabase();
    SchemaFiveDatabase.Create(database);
    Assert.Equal(5L, database.Scalar("PRAGMA user_version;")); // raw-sql: verify the frozen fixture version

    // Fail after migration starts creating provider tables, then retry the unchanged old database.
    database.Execute("CREATE TABLE provider_host (witness TEXT); INSERT INTO provider_host VALUES ('retained');");
    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(5L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed migration preserves its starting version
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'provider_connections';")); // raw-sql: rolled-back migration must not leave its table
    Assert.Equal("retained", database.Scalar("SELECT witness FROM provider_host;")); // raw-sql: preserve the conflicting fixture table
    Assert.Equal(SchemaFiveDatabase.SessionId, Assert.Single(StoredSessions.Read(database)).Id);
    Assert.Equal("\0🙂", StoredLlmResponses.ReadText(database));
    database.Execute("DROP TABLE provider_host;");

    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Equal(10L, database.Scalar("PRAGMA user_version;")); // raw-sql: verify the committed migration version
      Assert.Empty(store.Providers.List());
      Assert.Equal(new[] { SchemaFiveDatabase.SessionId }, store.Sessions.ListPrimaryIds(null).SessionIds);
      Assert.Equal(SchemaFiveDatabase.Original,
          store.UserMessages.ReadReceipt(SchemaFiveDatabase.SessionId, SchemaFiveDatabase.InputId)!.Submission.Text);
      SessionSnapshot snapshot = store.Snapshots.ReadStored(SchemaFiveDatabase.SessionId);
      Assert.Equal(SchemaFiveDatabase.Original, snapshot.FirstUserPreview);
      Assert.Equal(3, store.History.Read(SchemaFiveDatabase.SessionId,
          new(TranscriptHistoryMode.Latest, snapshot.HistoryRevision)).Entries.Count);
      Assert.Equal(3L, database.Scalar("SELECT COUNT(*) FROM execution_messages;")); // raw-sql: migration adds the saved tool's entry without changing legacy previews
      Assert.Equal(ExecutionOutcome.Failed, store.Executions.ReadSettlement(
          SchemaFiveDatabase.SessionId, SchemaFiveDatabase.RunId)!.Outcome);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Empty(reopened.Providers.List());
    Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: verify reopened referential integrity
  }
}
