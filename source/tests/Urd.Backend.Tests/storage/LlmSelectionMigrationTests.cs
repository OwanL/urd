using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class LlmSelectionMigrationTests {
  [Fact]
  public void SchemaSevenUpgradePreservesHistoryAndRollbackKeepsTheFrozenReceiptSchema() {
    using var database = new TemporaryDatabase();
    Guid session, execution, input, request;
    const string text = "legacy input \0 🙂";
    using (UrdStore store = UrdStore.Open(database.Path)) {
      session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      execution = Guid.NewGuid();
      input = Guid.NewGuid();
      Assert.True(store.UserMessages.Admit(session, 0,
          new(input, 0, text, TestLlmSelection.Default), true, execution).NewlyAccepted);
      request = store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId;
      Assert.True(store.LlmResponses.Append(session, execution, 0,
          new(request, Guid.NewGuid(), 0, "legacy answer 🙂", true)));
      Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
          new(request, ExecutionOutcome.Completed)));
      store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed);
    }

    // The retained support fixture downgrades only the schema-seven receipt shape.
    SchemaSevenDatabase.Downgrade(database);
    Assert.Equal(7L, database.Scalar("PRAGMA user_version;")); // raw-sql: verify the historical fixture version
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('input_decisions') WHERE name = 'llm_selection_json';")); // raw-sql: verify the historical receipt shape
    database.Execute("CREATE TABLE input_decisions_v8 (witness TEXT); INSERT INTO input_decisions_v8 VALUES ('retained');");
    Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => UrdStore.Open(database.Path));
    Assert.Equal(7L, database.Scalar("PRAGMA user_version;")); // raw-sql: failed migration must preserve its starting version
    Assert.Equal("retained", database.Scalar("SELECT witness FROM input_decisions_v8;")); // raw-sql: preserve the conflicting fixture table
    Assert.Equal(text, StorageText.StrictUtf8.GetString((byte[])database.Scalar(
        $"SELECT original_text_utf8 FROM input_decisions WHERE input_id = '{input:D}';")!)); // raw-sql: inspect historical text before the current reader can open it
    Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: verify rollback referential integrity

    database.Execute("DROP TABLE input_decisions_v8;");
    using (UrdStore migrated = UrdStore.Open(database.Path)) {
      Assert.Equal(8L, database.Scalar("PRAGMA user_version;")); // raw-sql: verify the committed migration version
      UserMessageReceipt receipt = migrated.UserMessages.ReadReceipt(session, input)!;
      Assert.Null(receipt.Submission.LlmSelection);
      Assert.Equal(text, receipt.Submission.Text);
      SessionSnapshot snapshot = migrated.Snapshots.ReadStored(session);
      Assert.Null(snapshot.LatestLlmSelection);
      Assert.Equal(new[] { text, "legacy answer 🙂" }, snapshot.Messages.Select(message => message.Preview));
      Assert.Equal(new SessionExecutionResult(execution, ExecutionOutcome.Completed), snapshot.LastSettlement);
      Assert.Equal(request, Assert.Single(StoredExecutions.ReadRequests(database)).RequestId);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Null(reopened.Snapshots.ReadStored(session).LatestLlmSelection);
    Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: verify reopened referential integrity
  }
}
