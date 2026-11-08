using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class StopStorageTests {
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void StopFlagAndPendingRetirementRollbackTogether(bool failAtCommit) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "claimed", TestLlmSelection.Default), true, execution);
    store.Executions.ClaimNextRequest(session, execution, 0);
    var pending = new UserMessageSubmission(Guid.NewGuid(), 0, "pending", TestLlmSelection.Default);
    UserMessageReceipt receipt = store.UserMessages.Admit(
        session, 0, pending, true, Guid.NewGuid(), execution).Receipt;
    StoredExecution savedExecution = Assert.Single(StoredExecutions.Read(database));
    StoredDelivery[] deliveries = StoredDeliveries.Read(database).ToArray();
    database.Execute(failAtCommit ? """
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_retirement AFTER UPDATE OF outcome ON execution_inputs
      BEGIN INSERT INTO test_child VALUES (1); END;
      """ : """
      CREATE TRIGGER fail_retirement BEFORE UPDATE OF outcome ON execution_inputs
      BEGIN SELECT RAISE(ABORT, 'Retirement unavailable'); END;
      """);

    Assert.Throws<SqliteException>(() => store.Executions.RequestStop(session, execution, ownsExecution: true));
    Assert.Equal(savedExecution, Assert.Single(StoredExecutions.Read(database)));
    Assert.Equal(deliveries, StoredDeliveries.Read(database).ToArray());
    Assert.Equal(receipt, store.UserMessages.ReadReceipt(session, pending.SubmissionId));

    database.Execute("DROP TRIGGER fail_retirement;");
    Assert.Equal(new StopReceipt(execution, StopStatus.Requested),
        store.Executions.RequestStop(session, execution, ownsExecution: true));
    Assert.True(Assert.Single(StoredExecutions.Read(database)).Stopping);
    Assert.Equal(ExecutionOutcome.Cancelled,
        Assert.Single(StoredDeliveries.Read(database), delivery => delivery.SubmissionId == pending.SubmissionId).Outcome);
    Assert.Null(Assert.Single(StoredDeliveries.Read(database), delivery => delivery.RequestId is not null).Outcome);
  }
}
