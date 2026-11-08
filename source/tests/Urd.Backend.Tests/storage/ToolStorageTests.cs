using System.Text.Json;
using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class ToolStorageTests {
  [Fact]
  public void FailedClaimCannotInvokeAndFailedResultSaveKeepsEffectFenced() {
    using var database = new TemporaryDatabase();
    Guid session, execution, request;
    int effects = 0;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      (session, execution, request) = Begin(store);
      var call = new LlmToolCall("effect", "fake", JsonSerializer.SerializeToElement(new { }));
      Assert.True(store.LlmResponses.Append(session, execution, 0,
          new(request, Guid.NewGuid(), 0, "", true)));
      Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
          new(request, ExecutionOutcome.Completed, ToolCalls: [call])));
      database.Execute("""
        CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
        CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
        CREATE TRIGGER fail_claim AFTER UPDATE OF started ON execution_tools
        BEGIN INSERT INTO test_child VALUES (1); END;
        """);

      Assert.Throws<SqliteException>(() => {
        if (store.Tools.TryStart(session, execution, 0, request, call.CallId))
          effects++;
      });
      Assert.Equal(0, effects);
      Assert.False(Assert.Single(StoredTools.Read(database)).Started);
      database.Execute("DROP TRIGGER fail_claim;");
      if (store.Tools.TryStart(session, execution, 0, request, call.CallId))
        effects++;
      Assert.Equal(1, effects);

      database.Execute("""
        CREATE TRIGGER fail_result AFTER UPDATE OF result_json ON execution_tools
        BEGIN INSERT INTO test_child VALUES (1); END;
        """);
      var result = new LlmToolResult("effect", ExecutionOutcome.Completed,
          JsonSerializer.SerializeToElement(new { saved = true }));
      Assert.Throws<SqliteException>(() => store.Tools.Complete(session, execution, 0, request, result));
      StoredTool retained = Assert.Single(StoredTools.Read(database));
      Assert.True(retained.Started);
      Assert.Null(retained.ResultJson);
      Assert.Null(retained.Outcome);
      Assert.False(store.Tools.TryStart(session, execution, 0, request, call.CallId));
      Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.False(reopened.Tools.TryStart(session, execution, 0, request, "effect"));
    Assert.Equal(1, effects);
    Assert.Null(reopened.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Null(reopened.Executions.ReadSettlement(session, execution));
  }

  private static (Guid Session, Guid Execution, Guid Request) Begin(UrdStore store) {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "request", TestLlmSelection.Default), true, execution);
    return (session, execution, store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId);
  }
}
