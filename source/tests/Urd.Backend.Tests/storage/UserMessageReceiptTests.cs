using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class UserMessageReceiptTests {
  [Fact]
  public void VersionOneMigrationRetainsReceiptsCreatedAfterUpgrade() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid();
    database.Execute($"""
      CREATE TABLE sessions (
          session_id TEXT NOT NULL PRIMARY KEY,
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
      ) STRICT;
      INSERT INTO sessions VALUES ('{session:D}', 7);
      PRAGMA user_version = 1;
      """);

    UserMessageReceipt receipt;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Equal(7L, store.Sessions.Read(session)!.HistoryRevision);
      receipt = store.UserMessages.Admit(session, 7,
          new(Guid.NewGuid(), 7, "retained after migration", TestLlmSelection.Default),
          accepting: false, proposedExecutionId: Guid.NewGuid()).Receipt;
      Assert.Equal(10L, database.Scalar("PRAGMA user_version;")); // raw-sql: the migrated schema version is the subject under test
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(7L, reopened.Sessions.Read(session)!.HistoryRevision);
    Assert.Equal(receipt, reopened.UserMessages.ReadReceipt(session, receipt.Submission.SubmissionId));
    Assert.Empty(StoredExecutions.Read(database));
  }

  [Fact]
  public void FailedVersionOneMigrationRollsBackSchemaAndKeepsIdentity() {
    using var database = new TemporaryDatabase();
    database.Execute("""
      CREATE TABLE sessions (
          session_id TEXT NOT NULL PRIMARY KEY,
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
      ) STRICT;
      INSERT INTO sessions VALUES ('00000000-0000-0000-0000-000000000001', 7);
      CREATE TABLE execution_runs (unrelated TEXT);
      INSERT INTO execution_runs VALUES ('retained');
      PRAGMA user_version = 1;
      """);

    Assert.Throws<SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(1L, database.Scalar("PRAGMA user_version;")); // raw-sql: rolled-back schema version is the subject under test
    Assert.Equal(7L, database.Scalar("SELECT history_revision FROM sessions;")); // raw-sql: pre-migration identity must survive the rollback
    Assert.Equal("retained", database.Scalar("SELECT unrelated FROM execution_runs;")); // raw-sql: fixture witness row must survive the rollback
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'input_decisions';")); // raw-sql: rolled-back schema must not expose the new table
  }
}
