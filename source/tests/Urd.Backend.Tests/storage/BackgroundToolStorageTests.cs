using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class BackgroundToolStorageTests {
  [Fact]
  public void OnlyBackgroundResultsCanArriveAfterANewerRequestAndStop() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, request) = Begin(store, "background", "foreground", "completed-foreground");
    string rawArguments = StoredTools.Read(database)[0].ArgumentsJson;
    Assert.True(store.Tools.TryStart(session, execution, 0, request, "background", background: true));
    Assert.True(store.Tools.TryStart(session, execution, 0, request, "foreground"));
    Assert.True(store.Tools.TryStart(session, execution, 0, request, "completed-foreground"));
    Assert.True(store.Tools.Complete(session, execution, 0, request, Result("completed-foreground")));
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_tool_completions;")); // raw-sql: Foreground result saves must not create background completion events.
    Guid later = Guid.NewGuid();
    database.Execute($"""
      INSERT INTO execution_requests (request_id, run_id, request_index)
      VALUES ('{later:D}', '{execution:D}', 1);
      """);
    Assert.True(store.Executions.StartRequest(session, execution, 0, later));
    Assert.True(store.LlmResponses.Append(session, execution, 0, new(later, Guid.NewGuid(), 0, "streaming", false)));
    store.Executions.RequestStop(session, execution, ownsExecution: true);

    Assert.True(store.Tools.Complete(session, execution, 0, request, Result("background")));
    Assert.False(store.Tools.Complete(session, execution, 0, request, Result("foreground")));
    Assert.Equal(rawArguments, StoredTools.Read(database)[0].ArgumentsJson);
    Assert.NotNull(store.Tools.ReadOutput(session, request, "background"));
    Assert.Null(store.Tools.ReadOutput(session, request, "foreground"));
    Assert.Equal(1L, database.Scalar($"""SELECT COUNT(*) FROM execution_tool_completions WHERE session_id = '{session:D}' AND history_revision = 0 AND request_id = '{request:D}' AND call_id = 'background' AND claimed_request_id IS NULL AND published = 0;""")); // raw-sql: No completion reader exists yet; verify event identity and initial delivery state.
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_messages WHERE role = 3;")); // raw-sql: Completion saves must not append transcript rows during a newer streaming request.
  }

  [Fact]
  public void ExactRepeatRetainsOneCompletionIdentityAndOriginalImages() {
    using var database = new TemporaryDatabase();
    Guid session, execution, request;
    var result = new LlmToolResult("background", ExecutionOutcome.Completed,
        new ToolOutput("saved", [new DataContent(new byte[] { 1, 2, 3 }, "image/png")],
            JsonSerializer.SerializeToElement(new { saved = true })));
    string completionId;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      (session, execution, request) = Begin(store, "background");
      Assert.True(store.Tools.TryStart(session, execution, 0, request, result.CallId, background: true));
      Assert.True(store.Tools.Complete(session, execution, 0, request, result));
      completionId = Assert.IsType<string>(database.Scalar("SELECT completion_id FROM execution_tool_completions;")); // raw-sql: No reader exposes the durable future transcript message identity.
      Assert.NotEqual(Guid.Empty, Guid.Parse(completionId));
      Assert.True(store.Tools.Complete(session, execution, 0, request, result));
      Assert.False(store.Tools.Complete(session, execution, 0, request,
          result with { Output = result.Output with { Text = "changed" } }));
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.True(reopened.Tools.Complete(session, execution, 0, request, result));
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_tool_completions;")); // raw-sql: No reader exposes completion count; exact repeats must not create delivery duplicates.
    Assert.Equal(completionId, database.Scalar("SELECT completion_id FROM execution_tool_completions;")); // raw-sql: Exact repeats must preserve the future transcript message identity across reopen.
    ToolOutput saved = Assert.IsType<ToolOutput>(reopened.Tools.ReadOutput(session, request, result.CallId));
    Assert.Equal(result.Output.Text, saved.Text);
    Assert.True(JsonElement.DeepEquals(result.Output.Details!.Value, saved.Details!.Value));
    DataContent image = Assert.Single(saved.Images);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(new byte[] { 1, 2, 3 }, image.Data.ToArray());
  }

  [Fact]
  public void BackgroundResultsKeepIdentityRevisionStartedOriginAndSettlementFences() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, request) = Begin(store, "background", "unstarted");
    Assert.True(store.Tools.TryStart(session, execution, 0, request, "background", background: true));
    LlmToolResult result = Result("background");
    Assert.False(store.Tools.Complete(Guid.NewGuid(), execution, 0, request, result));
    Assert.False(store.Tools.Complete(session, Guid.NewGuid(), 0, request, result));
    Assert.False(store.Tools.Complete(session, execution, 1, request, result));
    Assert.False(store.Tools.Complete(session, execution, 0, Guid.NewGuid(), result));
    Assert.False(store.Tools.Complete(session, execution, 0, request, Result("missing")));
    Assert.False(store.Tools.Complete(session, execution, 0, request, Result("unstarted")));
    database.ForceHistoryRevision(1);
    Assert.False(store.Tools.Complete(session, execution, 0, request, result));
    database.ForceHistoryRevision(0);
    database.Execute("UPDATE execution_requests SET outcome = 1;");
    Assert.False(store.Tools.Complete(session, execution, 0, request, result));
    database.Execute("UPDATE execution_requests SET outcome = 0; UPDATE execution_runs SET outcome = 0;");
    Assert.False(store.Tools.Complete(session, execution, 0, request, result));
    Assert.All(StoredTools.Read(database), tool => {
      Assert.Null(tool.ResultJson);
      Assert.Null(tool.Outcome);
    });
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_tool_completions;")); // raw-sql: No completion reader exists; fenced writes must leave no deliverable event.
  }

  [Fact]
  public void FailedCompletionEventCommitRollsBackResultAndEventTogether() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, request) = Begin(store, "background");
    Assert.True(store.Tools.TryStart(session, execution, 0, request, "background", background: true));
    database.Execute("""
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_completion AFTER INSERT ON execution_tool_completions
      BEGIN INSERT INTO test_child VALUES (1); END;
      """);
    Assert.Throws<SqliteException>(() => store.Tools.Complete(session, execution, 0, request, Result("background")));
    StoredTool tool = Assert.Single(StoredTools.Read(database));
    Assert.True(tool.Started);
    Assert.Null(tool.Outcome);
    Assert.Null(tool.ResultJson);
    Assert.Null(store.Tools.ReadOutput(session, request, tool.CallId));
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_tool_completions;")); // raw-sql: No completion reader exists; verify a deferred event failure rolls back the entire transaction.
    database.Execute("DROP TRIGGER fail_completion;");
    Assert.True(store.Tools.Complete(session, execution, 0, request, Result("background")));
    Assert.NotNull(store.Tools.ReadOutput(session, request, tool.CallId));
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_tool_completions;")); // raw-sql: Retrying the rolled-back save must create exactly one deliverable event.
  }

  private static LlmToolResult Result(string callId) =>
      new(callId, ExecutionOutcome.Completed, new ToolOutput("saved", []));

  private static (Guid Session, Guid Execution, Guid Request) Begin(UrdStore store, params string[] calls) {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "request", TestLlmSelection.Default), true, execution);
    Guid request = store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId;
    Assert.True(store.Executions.StartRequest(session, execution, 0, request));
    Assert.True(store.LlmResponses.Append(session, execution, 0, new(request, Guid.NewGuid(), 0, "", true)));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
        new(request, ExecutionOutcome.Completed, ToolCalls: calls.Select(callId =>
            new LlmToolCall(callId, "fake", JsonSerializer.SerializeToElement(new { background = true }))).ToArray())));
    return (session, execution, request);
  }
}
