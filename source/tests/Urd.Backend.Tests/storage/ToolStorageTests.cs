using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class ToolStorageTests {
  [Fact]
  public void CallsStartIndependentlyWhileFailedClaimsAndResultsRemainFenced() {
    using var database = new TemporaryDatabase();
    Guid session, execution, request;
    int effects = 0;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      (session, execution, request) = Begin(store);
      var call = new LlmToolCall("effect", "fake", JsonSerializer.SerializeToElement(new { }));
      var later = call with { CallId = "later" };
      var unstarted = call with { CallId = "unstarted" };
      Assert.False(store.Tools.TryStart(session, execution, 0, request, later.CallId));
      Assert.True(store.LlmResponses.Append(session, execution, 0,
          new(request, Guid.NewGuid(), 0, "", true)));
      Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
          new(request, ExecutionOutcome.Completed, ToolCalls: [call, later, unstarted])));
      var laterResult = new LlmToolResult(later.CallId, ExecutionOutcome.Completed,
          new ToolOutput("later", [], JsonSerializer.SerializeToElement(new { saved = "later" })));
      Assert.False(store.Tools.Complete(session, execution, 0, request, laterResult));
      Assert.False(store.Tools.TryStart(Guid.NewGuid(), execution, 0, request, later.CallId));
      Assert.False(store.Tools.TryStart(session, Guid.NewGuid(), 0, request, later.CallId));
      Assert.False(store.Tools.TryStart(session, execution, 1, request, later.CallId));
      Assert.False(store.Tools.TryStart(session, execution, 0, Guid.NewGuid(), later.CallId));
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
      Assert.All(StoredTools.Read(database), tool => Assert.False(tool.Started));
      database.Execute("DROP TRIGGER fail_claim;");
      if (store.Tools.TryStart(session, execution, 0, request, call.CallId))
        effects++;
      Assert.Equal(1, effects);

      database.Execute("""
        CREATE TRIGGER fail_result AFTER UPDATE OF result_json ON execution_tools
        BEGIN INSERT INTO test_child VALUES (1); END;
        """);
      var result = new LlmToolResult("effect", ExecutionOutcome.Completed,
          new ToolOutput("saved result", [new DataContent(new byte[] { 1, 2, 3 }, "image/png")],
              JsonSerializer.SerializeToElement(new { saved = true })));
      Assert.Throws<SqliteException>(() => store.Tools.Complete(session, execution, 0, request, result));
      // Bytes published before an uncertain commit are retained, not deleted as rollback.
      Assert.Single(Directory.GetFiles(database.Path + ".tool-images"));
      StoredTool retained = Assert.Single(StoredTools.Read(database), tool => tool.CallId == call.CallId);
      Assert.True(retained.Started);
      Assert.Null(retained.ResultJson);
      Assert.Null(retained.Outcome);
      Assert.False(store.Tools.TryStart(session, execution, 0, request, call.CallId));
      Assert.True(store.Tools.TryStart(session, execution, 0, request, later.CallId));
      Assert.False(store.Tools.TryStart(session, execution, 0, request, later.CallId));
      database.Execute("DROP TRIGGER fail_result;");
      Assert.True(store.Tools.Complete(session, execution, 0, request, laterResult));
      Assert.True(store.Tools.Complete(session, execution, 0, request, laterResult));
      Assert.False(store.Tools.Complete(session, execution, 0, request,
          laterResult with { Output = laterResult.Output with { Text = "changed" } }));
      Assert.Null(Assert.Single(StoredTools.Read(database), tool => tool.CallId == call.CallId).ResultJson);
      ToolOutput savedLater = Assert.IsType<ToolOutput>(store.Tools.ReadOutput(session, request, later.CallId));
      Assert.Equal(laterResult.Output.Text, savedLater.Text);
      Assert.True(JsonElement.DeepEquals(laterResult.Output.Details!.Value, savedLater.Details!.Value));
      Assert.Null(store.Executions.ClaimNextRequest(session, execution, 0));
      Assert.Null(store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed));
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.False(reopened.Tools.TryStart(session, execution, 0, request, "effect"));
    Assert.Equal(1, effects);
    Assert.Null(reopened.Executions.ClaimNextRequest(session, execution, 0));
    Assert.Null(reopened.Executions.ReadSettlement(session, execution));
    reopened.Executions.RequestStop(session, execution, ownsExecution: true);
    Assert.False(reopened.Tools.TryStart(session, execution, 0, request, "unstarted"));
    var recovered = new LlmToolResult("effect", ExecutionOutcome.Completed,
        new ToolOutput("saved result", [new DataContent(new byte[] { 1, 2, 3 }, "image/png")],
            JsonSerializer.SerializeToElement(new { saved = true })));
    Assert.True(reopened.Tools.Complete(session, execution, 0, request, recovered));
    Assert.Equal(ExecutionOutcome.Cancelled,
        reopened.Executions.TrySettle(session, execution, ExecutionOutcome.Cancelled)!.Outcome);
    Assert.False(reopened.Tools.TryStart(session, execution, 0, request, "unstarted"));
    Assert.True(reopened.Tools.Complete(session, execution, 0, request, recovered));
    Assert.Single(Directory.GetFiles(database.Path + ".tool-images"));
    ToolOutput original = Assert.IsType<ToolOutput>(reopened.Tools.ReadOutput(session, request, "effect"));
    Assert.Equal("saved result", original.Text);
    Assert.True(original.Details!.Value.GetProperty("saved").GetBoolean());
    Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(original.Images).Data.ToArray());
    ToolOutput unexecuted = Assert.IsType<ToolOutput>(reopened.Tools.ReadOutput(session, request, "unstarted"));
    Assert.True(unexecuted.IsError);
    Assert.Equal("Tool was not executed.", unexecuted.Text);
  }

  [Fact]
  public void ImagesPublishOnceAndRefuseCorruptionWithoutOverwritingIt() {
    using var database = new TemporaryDatabase();
    string directory = database.Path + ".tool-images";
    var images = new ToolImageStore(directory);
    byte[] bytes = [1, 2, 3];
    var image = new DataContent(bytes, "image/png");
    ToolImageReference[] references = Enumerable.Range(0, 8).AsParallel().Select(_ => images.Save(image)).ToArray();
    Assert.All(references, reference => Assert.Equal(references[0], reference));
    string path = Assert.Single(Directory.GetFiles(directory));
    bytes[0] = 9;
    Assert.Equal(new byte[] { 1, 2, 3 }, images.Read(references[0]).Data.ToArray());
    Assert.Equal("image/png", images.Read(references[0]).MediaType);
    File.WriteAllBytes(path, [9, 9, 9]);
    Assert.Throws<InvalidDataException>(() => images.Read(references[0]));
    Assert.Throws<InvalidDataException>(() => images.Save(new DataContent(new byte[] { 1, 2, 3 }, "image/png")));
    Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(path));
    Assert.Single(Directory.GetFiles(directory));
    Assert.Throws<ArgumentException>(() => images.Read(references[0] with { Hash = "../escape" }));
  }

  private static (Guid Session, Guid Execution, Guid Request) Begin(UrdStore store) {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "request", TestLlmSelection.Default), true, execution);
    Guid request = store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId;
    Assert.True(store.Executions.StartRequest(session, execution, 0, request));
    return (session, execution, request);
  }
}
