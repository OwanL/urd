using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class AuxiliaryConversationStorageTests {
  [Fact]
  public void ParallelConversationsStayOutsideMainHistoryAndRetainTheirSnapshots() {
    using var database = new TemporaryDatabase();
    Guid session, run, firstConversation = Guid.NewGuid(), secondConversation = Guid.NewGuid();
    Guid firstRequest = Guid.NewGuid(), secondRequest = Guid.NewGuid(), pendingInput = Guid.NewGuid();
    var selection = TestLlmSelection.Default with { ModelId = "auxiliary-model" };
    var tool = Tool("inspect", "Initial auxiliary declaration.", "{\"type\":\"object\",\"properties\":{\"item\":{\"type\":\"string\"}}}");
    var initialInput = new[] { new ChatMessage(ChatRole.System, "Keep this prompt. 🌍"),
        new ChatMessage(new ChatRole("developer"), "Preserve this instruction."),
        new ChatMessage(ChatRole.User, "Inspect the request.") };
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var parent = BeginParent(store);
      session = parent.Session;
      run = parent.Run;
      FinishMainRequest(store, parent.Identity, parent.MainRequest);
      store.UserMessages.Submit(session, 0,
          new(pendingInput, 0, "still pending", TestLlmSelection.Default), true, run, run);
      SessionSnapshot before = store.Snapshots.ReadPersisted(session);
      TranscriptEntryRef[] historyBefore = store.History.Read(session, new(TranscriptHistoryMode.Latest, 0)).Entries.Select(entry => entry.Entry).ToArray();
      Assert.Equal(pendingInput, Assert.Single(before.PendingInputs).SubmissionId);

      Assert.True(store.AuxiliaryConversations.Create(parent.Identity, firstConversation, "first purpose", selection, initialInput, [tool]));
      Assert.True(store.AuxiliaryConversations.Create(parent.Identity, secondConversation, "second purpose", selection, initialInput, [tool]));
      Assert.True(store.AuxiliaryConversations.StartRequest(parent.Identity, firstConversation, firstRequest));
      Assert.True(store.AuxiliaryConversations.StartRequest(parent.Identity, secondConversation, secondRequest));
      Guid firstResponse = Guid.NewGuid(), secondResponse = Guid.NewGuid();
      Assert.True(store.AuxiliaryConversations.Append(parent.Identity, firstConversation, new(firstRequest, firstResponse, 0, "first ", false)));
      Assert.True(store.AuxiliaryConversations.Append(parent.Identity, secondConversation, new(secondRequest, secondResponse, 0, "second ", false)));
      Assert.True(store.AuxiliaryConversations.RecordToolCall(parent.Identity, firstConversation, firstRequest, "shared-call", "inspect"));
      Assert.True(store.AuxiliaryConversations.RecordToolCall(parent.Identity, secondConversation, secondRequest, "shared-call", "inspect"));
      Assert.True(store.AuxiliaryConversations.Append(parent.Identity, firstConversation, new(firstRequest, firstResponse, 1, "done", true)));
      Assert.True(store.AuxiliaryConversations.Append(parent.Identity, secondConversation, new(secondRequest, secondResponse, 1, "also done", true)));
      Assert.True(store.AuxiliaryConversations.CompleteRequest(parent.Identity, firstConversation, new(firstRequest, ExecutionOutcome.Completed, ToolCalls: [Call("shared-call", "inspect")])));
      Assert.True(store.AuxiliaryConversations.CompleteRequest(parent.Identity, secondConversation, new(secondRequest, ExecutionOutcome.Completed, ToolCalls: [Call("shared-call", "inspect")])));
      SessionSnapshot after = store.Snapshots.ReadPersisted(session);
      Assert.Equal(before.LatestLlmSelection, after.LatestLlmSelection);
      Assert.Equal(before.PendingInputs, after.PendingInputs);
      Assert.Equal(historyBefore, store.History.Read(session, new(TranscriptHistoryMode.Latest, 0)).Entries.Select(entry => entry.Entry));
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    var snapshots = database.ReadRows($"SELECT conversation_id, llm_selection_json, input_json FROM auxiliary_conversations WHERE conversation_id IN ('{firstConversation:D}', '{secondConversation:D}') ORDER BY conversation_id;",
        row => (Id: Guid.Parse(row.GetString(0)), Selection: row.GetString(1), Input: row.GetString(2))); // raw-sql: auxiliary snapshots have no production reader.
    var first = Assert.Single(snapshots, row => row.Id == firstConversation);
    Assert.Equal(selection, LlmSelectionJson.Read(first.Selection));
    using (JsonDocument input = JsonDocument.Parse(first.Input)) {
      JsonElement messages = input.RootElement.GetProperty("messages");
      Assert.Equal(new[] { "system", "developer", "user" }, messages.EnumerateArray().Select(message => message.GetProperty("role").GetString()));
      Assert.Equal("Keep this prompt. 🌍", messages[0].GetProperty("texts")[0].GetString());
      Assert.Equal("Preserve this instruction.", messages[1].GetProperty("texts")[0].GetString());
      JsonElement savedTool = Assert.Single(input.RootElement.GetProperty("tools").EnumerateArray());
      Assert.Equal("inspect", savedTool.GetProperty("name").GetString());
      Assert.Equal("Initial auxiliary declaration.", savedTool.GetProperty("description").GetString());
      Assert.Equal("object", savedTool.GetProperty("schema").GetProperty("type").GetString());
    }
    var output = database.ReadRows($"SELECT conversation_id, chunk_index, text_utf8, is_final FROM auxiliary_output WHERE request_id IN ('{firstRequest:D}', '{secondRequest:D}') ORDER BY conversation_id, chunk_index;",
        row => (Conversation: Guid.Parse(row.GetString(0)), Index: row.GetInt64(1), Text: Encoding.UTF8.GetString(row.GetFieldValue<byte[]>(2)), Final: row.GetInt64(3) != 0)); // raw-sql: auxiliary output has no production reader.
    Assert.Equal(new[] { "first ", "done" }, output.Where(row => row.Conversation == firstConversation).Select(row => row.Text));
    Assert.Equal(new[] { "second ", "also done" }, output.Where(row => row.Conversation == secondConversation).Select(row => row.Text));
    Assert.Equal(new long[] { 0, 1, 0, 1 }, output.Select(row => row.Index));
    Assert.Equal(new[] { false, true, false, true }, output.Select(row => row.Final));
    SessionSnapshot restored = reopened.Snapshots.ReadPersisted(session);
    Assert.Equal(pendingInput, Assert.Single(restored.PendingInputs).SubmissionId);
    LlmRequest next = reopened.Executions.CreateNextRequest(new(session, run, 0))!;
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User }, next.Messages.Select(message => message.Role));
    Assert.Equal(new[] { "first main input", "main answer", "still pending" }, next.Messages.Select(message => message.Text));
    Assert.Equal(2, StoredExecutions.ReadRequests(database).Count);
  }

  [Fact]
  public void ParentSettlementWaitsForFinalObservedAuxiliaryCallsAndTheirResults() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    (Guid session, Guid run, ExecutionIdentity identity, Guid mainRequest) = BeginParent(store);
    FinishMainRequest(store, identity, mainRequest);
    Guid conversation = Guid.NewGuid(), request = Guid.NewGuid();
    Assert.True(store.AuxiliaryConversations.Create(identity, conversation, "lookup", TestLlmSelection.Default, [], []));
    Assert.True(store.AuxiliaryConversations.StartRequest(identity, conversation, request));
    Assert.Null(store.Executions.TryFinish(session, run, ExecutionOutcome.Completed));
    Guid response = Guid.NewGuid();
    Assert.True(store.AuxiliaryConversations.Append(identity, conversation, new(request, response, 0, "checking", false)));
    Assert.True(store.AuxiliaryConversations.RecordToolCall(identity, conversation, request, "lookup-call", "inspect"));
    Assert.False(store.AuxiliaryConversations.CompleteRequest(identity, conversation, new(request, ExecutionOutcome.Completed, ToolCalls: [Call("lookup-call", "inspect")])));
    Assert.True(store.AuxiliaryConversations.Append(identity, conversation, new(request, response, 1, " result", true)));
    Assert.False(store.AuxiliaryConversations.CompleteRequest(identity, conversation, new(request, ExecutionOutcome.Completed, ToolCalls: [Call("lookup-call", "other-name")])));
    Assert.False(store.AuxiliaryConversations.CompleteRequest(identity, conversation, new(request, ExecutionOutcome.Completed, ToolCalls: [Call("different-call", "inspect")])));
    var completion = new LlmRequestResult(request, ExecutionOutcome.Completed, ToolCalls: [Call("lookup-call", "inspect")]);
    Assert.True(store.AuxiliaryConversations.CompleteRequest(identity, conversation, completion));
    Assert.Null(store.Executions.TryFinish(session, run, ExecutionOutcome.Completed));
    Assert.True(store.AuxiliaryConversations.TryStartTool(identity, conversation, request, "lookup-call"));
    Assert.False(store.AuxiliaryConversations.TryStartTool(identity, conversation, request, "lookup-call"));
    Assert.True(store.AuxiliaryConversations.CompleteTool(identity, conversation, request, new("lookup-call", ExecutionOutcome.Completed, new ToolOutput("actual result", []))));
    Assert.Equal(ExecutionOutcome.Completed, store.Executions.TryFinish(session, run, ExecutionOutcome.Completed)!.Outcome);
  }

  [Fact]
  public void FailuresAndStaleParentsAreFencedAndStopRetainsOnlyActualRunningResults() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var parent = BeginParent(store);
    Guid session = parent.Session, run = parent.Run;
    ExecutionIdentity identity = parent.Identity;
    Guid conversation = Guid.NewGuid(), request = Guid.NewGuid();
    Assert.True(store.AuxiliaryConversations.Create(identity, conversation, "atomic", TestLlmSelection.Default, [], []));
    Assert.True(store.AuxiliaryConversations.StartRequest(identity, conversation, request));
    database.ForceHistoryRevision(1);
    Assert.False(store.AuxiliaryConversations.Append(identity, conversation, new(request, Guid.NewGuid(), 0, "stale", false)));
    database.ForceHistoryRevision(0);
    Assert.False(store.AuxiliaryConversations.Append(identity with { SessionId = Guid.NewGuid() }, conversation, new(request, Guid.NewGuid(), 0, "stale", false)));
    Assert.False(store.AuxiliaryConversations.Append(identity with { ExecutionId = Guid.NewGuid() }, conversation, new(request, Guid.NewGuid(), 0, "stale", false)));
    database.Execute("CREATE TRIGGER fail_aux_output BEFORE INSERT ON auxiliary_output BEGIN SELECT RAISE(ABORT, 'append failed'); END;");
    Assert.Throws<SqliteException>(() => store.AuxiliaryConversations.Append(identity, conversation, new(request, Guid.NewGuid(), 0, "retry", false)));
    Assert.Equal(0L, database.Scalar($"SELECT next_chunk_index FROM auxiliary_requests WHERE request_id = '{request:D}';")); // raw-sql: failed append must roll back its cursor.
    database.Execute("DROP TRIGGER fail_aux_output;");
    Assert.True(store.AuxiliaryConversations.Append(identity, conversation, new(request, Guid.NewGuid(), 0, "running", false)));
    Assert.True(store.AuxiliaryConversations.RecordToolCall(identity, conversation, request, "running-call", "run"));
    Assert.True(store.AuxiliaryConversations.RecordToolCall(identity, conversation, request, "unstarted-call", "run"));
    Assert.True(store.AuxiliaryConversations.Append(identity, conversation, new(request, Guid.NewGuid(), 1, "", true)));
    var result = new LlmRequestResult(request, ExecutionOutcome.Completed, ToolCalls: [Call("running-call", "run"), Call("unstarted-call", "run")]);
    database.Execute("CREATE TRIGGER fail_aux_tool BEFORE INSERT ON auxiliary_tools BEGIN SELECT RAISE(ABORT, 'completion failed'); END;");
    Assert.Throws<SqliteException>(() => store.AuxiliaryConversations.CompleteRequest(identity, conversation, result));
    Assert.Equal(DBNull.Value, database.Scalar($"SELECT outcome FROM auxiliary_requests WHERE request_id = '{request:D}';")); // raw-sql: failed completion must roll back before a tool can be claimed.
    Assert.False(store.AuxiliaryConversations.TryStartTool(identity, conversation, request, "running-call"));
    database.Execute("DROP TRIGGER fail_aux_tool;");
    Assert.True(store.AuxiliaryConversations.CompleteRequest(identity, conversation, result));
    Assert.True(store.AuxiliaryConversations.TryStartTool(identity, conversation, request, "running-call"));
    Guid failedConversation = Guid.NewGuid(), failedRequest = Guid.NewGuid();
    Assert.True(store.AuxiliaryConversations.Create(identity, failedConversation, "failed request", TestLlmSelection.Default, [], []));
    Assert.True(store.AuxiliaryConversations.StartRequest(identity, failedConversation, failedRequest));
    store.Executions.RequestStop(session, run, ownsExecution: true);
    Assert.False(store.AuxiliaryConversations.TryStartTool(identity, conversation, request, "unstarted-call"));
    Assert.False(store.AuxiliaryConversations.StartRequest(identity, failedConversation, Guid.NewGuid()));
    Assert.False(store.AuxiliaryConversations.Append(identity, failedConversation, new(failedRequest, Guid.NewGuid(), 0, "late output", false)));
    Assert.False(store.AuxiliaryConversations.RecordToolCall(identity, failedConversation, failedRequest, "late-call", null));
    Assert.False(store.AuxiliaryConversations.CompleteRequest(identity, failedConversation, new(failedRequest, ExecutionOutcome.Completed)));
    Assert.True(store.AuxiliaryConversations.CompleteRequest(identity, failedConversation, new(failedRequest, ExecutionOutcome.Failed, "provider failed")));
    Assert.True(store.AuxiliaryConversations.CompleteTool(identity, conversation, request, new("running-call", ExecutionOutcome.Completed, new ToolOutput("saved after stop", []))));
    Assert.Equal(ExecutionOutcome.Cancelled, store.Executions.TryFinish(session, run, ExecutionOutcome.Cancelled)!.Outcome);
    Assert.False(store.AuxiliaryConversations.TryStartTool(identity, conversation, request, "running-call"));
    Assert.False(store.AuxiliaryConversations.Append(identity, conversation, new(request, Guid.NewGuid(), 2, "retired", false)));
    Assert.Contains("saved after stop", Assert.IsType<string>(database.Scalar($"SELECT result_json FROM auxiliary_tools WHERE request_id = '{request:D}';"))); // raw-sql: auxiliary results have no production reader.
    Assert.Equal((long)ExecutionOutcome.Failed, database.Scalar($"SELECT outcome FROM auxiliary_requests WHERE request_id = '{failedRequest:D}';")); // raw-sql: a failed auxiliary result stays failed after parent cancellation.
  }

  [Fact]
  public void HostRecoveryRetainsResultsAndRetiresUnresolvedAuxiliaryWorkWithoutRestartingIt() {
    using var database = new TemporaryDatabase();
    Guid session, run, conversation, pendingConversation, request, pendingRequest;
    ExecutionIdentity identity;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      (session, run, identity, _) = BeginParent(store);
      conversation = Guid.NewGuid();
      request = Guid.NewGuid();
      Assert.True(store.AuxiliaryConversations.Create(identity, conversation, "tools", TestLlmSelection.Default, [], []));
      Assert.True(store.AuxiliaryConversations.StartRequest(identity, conversation, request));
      Assert.True(store.AuxiliaryConversations.Append(identity, conversation, new(request, Guid.NewGuid(), 0, "calls", false)));
      foreach (string call in new[] { "unstarted", "running", "saved" })
        Assert.True(store.AuxiliaryConversations.RecordToolCall(identity, conversation, request, call, "tool"));
      Assert.True(store.AuxiliaryConversations.Append(identity, conversation, new(request, Guid.NewGuid(), 1, "", true)));
      Assert.True(store.AuxiliaryConversations.CompleteRequest(identity, conversation, new(request, ExecutionOutcome.Completed,
          ToolCalls: new[] { "unstarted", "running", "saved" }.Select(call => Call(call, "tool")).ToArray())));
      Assert.True(store.AuxiliaryConversations.TryStartTool(identity, conversation, request, "running"));
      Assert.True(store.AuxiliaryConversations.TryStartTool(identity, conversation, request, "saved"));
      Assert.True(store.AuxiliaryConversations.CompleteTool(identity, conversation, request, new("saved", ExecutionOutcome.Completed, new ToolOutput("retained actual result", []))));
      pendingConversation = Guid.NewGuid();
      pendingRequest = Guid.NewGuid();
      Assert.True(store.AuxiliaryConversations.Create(identity, pendingConversation, "pending", TestLlmSelection.Default, [], []));
      Assert.True(store.AuxiliaryConversations.StartRequest(identity, pendingConversation, pendingRequest));
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Null(reopened.Executions.ReadSettlement(session, run));
    reopened.RecoverInterruptedExecutions();
    Assert.Equal(ExecutionOutcome.Failed, reopened.Executions.ReadSettlement(session, run)!.Outcome);
    var tools = database.ReadRows($"SELECT call_id, started, interrupted, outcome, result_json FROM auxiliary_tools WHERE conversation_id = '{conversation:D}' ORDER BY call_index;",
        row => (Call: row.GetString(0), Started: row.GetInt64(1) != 0, Interrupted: row.GetInt64(2) != 0, Outcome: (ExecutionOutcome)row.GetInt64(3), Result: row.GetString(4))); // raw-sql: auxiliary tools have no production reader.
    var saved = Assert.Single(tools, tool => tool.Call == "saved");
    Assert.True(saved.Started && !saved.Interrupted);
    Assert.Equal(ExecutionOutcome.Completed, saved.Outcome);
    Assert.Contains("retained actual result", saved.Result);
    foreach (var call in tools.Where(tool => tool.Call != "saved")) {
      Assert.Equal(call.Call == "running", call.Started);
      Assert.True(call.Interrupted);
      Assert.Equal(ExecutionOutcome.Failed, call.Outcome);
      Assert.Contains(call.Started ? "effects may have occurred" : "not executed", call.Result);
      Assert.False(reopened.AuxiliaryConversations.TryStartTool(identity, conversation, request, call.Call));
    }
    Assert.False(reopened.AuxiliaryConversations.StartRequest(identity, pendingConversation, Guid.NewGuid()));
    Assert.False(reopened.AuxiliaryConversations.Append(identity, pendingConversation, new(pendingRequest, Guid.NewGuid(), 0, "no replay", false)));
    Assert.False(reopened.AuxiliaryConversations.TryStartTool(identity, conversation, request, "saved"));
    Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM auxiliary_requests;")); // raw-sql: recovery must not create a provider request.
  }

  private static (Guid Session, Guid Run, ExecutionIdentity Identity, Guid MainRequest) BeginParent(UrdStore store) {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    store.UserMessages.Submit(session, 0,
        new(Guid.NewGuid(), 0, "first main input", TestLlmSelection.Default), true, run, run);
    var identity = new ExecutionIdentity(session, run, 0);
    LlmRequest request = store.Executions.CreateNextRequest(identity)!;
    Assert.True(store.Executions.StartRequest(identity, request.RequestId));
    return (session, run, identity, request.RequestId);
  }

  private static void FinishMainRequest(UrdStore store, ExecutionIdentity identity, Guid request) {
    Assert.True(store.LlmResponses.Append(identity, new(request, Guid.NewGuid(), 0, "main answer", true)));
    Assert.True(store.LlmResponses.CompleteRequest(identity, new(request, ExecutionOutcome.Completed)));
  }

  private static AIFunctionDeclaration Tool(string name, string description, string schema) {
    using JsonDocument json = JsonDocument.Parse(schema);
    return AIFunctionFactory.CreateDeclaration(name, description, json.RootElement.Clone());
  }

  private static LlmToolCall Call(string callId, string name) =>
      new(callId, name, JsonSerializer.SerializeToElement(new { item = "value" }));
}
