using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class ExecutionStorageTests {
  [Fact]
  public void AdmissionRollsBackReceiptAndExecutionTogetherOnCommitFailure() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "atomic input", TestLlmSelection.Default);
    Guid execution = Guid.NewGuid();
    database.Execute("""
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_run AFTER INSERT ON execution_runs
      BEGIN INSERT INTO test_child VALUES (1); END;
      """);

    Assert.Throws<SqliteException>(() => store.UserMessages.Admit(session, 0, submission, true, execution));
    Assert.Null(store.UserMessages.ReadReceipt(session, submission.SubmissionId));
    Assert.Empty(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));

    database.Execute("DROP TRIGGER fail_run;");
    var admitted = store.UserMessages.Admit(session, 0, submission, true, execution);
    Assert.True(admitted.NewlyAccepted);
    Assert.Equal(execution, admitted.Receipt.ExecutionId);
    Assert.Equal(execution, Assert.Single(StoredExecutions.Read(database)).ExecutionId);
  }

  [Fact]
  public void ToolObservationRetriesRemainScopedAndCannotChangeFactsAfterStop() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid otherSession = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "input", TestLlmSelection.Default), true, execution);
    Guid request = store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId;
    Assert.True(store.LlmResponses.ObserveTool(session, execution, 0, request, "call", null));
    Assert.True(store.LlmResponses.ObserveTool(session, execution, 0, request, "call", null));
    Assert.False(store.LlmResponses.ObserveTool(otherSession, execution, 0, request, "call", "foreign"));
    Assert.False(store.LlmResponses.ObserveTool(session, Guid.NewGuid(), 0, request, "call", "foreign"));
    Assert.False(store.LlmResponses.ObserveTool(session, execution, 1, request, "call", "stale"));
    store.Executions.RequestStop(session, execution, ownsExecution: true);
    Assert.False(store.LlmResponses.ObserveTool(session, execution, 0, request, "call", "late"));
    TranscriptEntryDescriptor reserved = Assert.Single(store.History.Read(session,
        new(TranscriptHistoryMode.Latest, 0)).Entries, entry => entry.Entry.Kind == TranscriptEntryKind.Tool);
    Assert.Null(reserved.Tool!.Name);
    Assert.False(reserved.Tool.Started);
    Assert.True(reserved.Tool.IsClosed);
    Assert.Empty(StoredTools.Read(database));
  }

  [Fact]
  public void OutputIdentityAndOrderedContextSurviveReopen() {
    using var database = new TemporaryDatabase();
    Guid session = Guid.NewGuid(), execution = Guid.NewGuid(), output = Guid.NewGuid();
    Guid requestId;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      store.UserMessages.Admit(session, 0,
          new(Guid.NewGuid(), 0, "first input", TestLlmSelection.Default), true, execution);
      LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
      requestId = request.RequestId;
      Assert.True(store.Executions.StartRequest(session, execution, 0, requestId));
      Assert.True(store.LlmResponses.Append(session, execution, 0,
          new(requestId, output, 0, "part", false)));
      Assert.True(store.LlmResponses.Append(session, execution, 0,
          new(requestId, output, 1, "🙂 final", true)));
      Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
          new(requestId, ExecutionOutcome.Completed)));
      Assert.Equal(ExecutionOutcome.Completed,
          store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
    }

    var chunks = StoredLlmResponses.Read(database);
    Assert.Equal(new long[] { 0, 1 }, chunks.Select(chunk => chunk.ChunkIndex));
    Assert.All(chunks, chunk => {
      Assert.Equal(requestId, chunk.RequestId);
      Assert.Equal(output, chunk.ResponseId);
    });
    Assert.Equal(new[] { "part", "🙂 final" }, chunks.Select(chunk => System.Text.Encoding.UTF8.GetString(chunk.Bytes)));
    Assert.Equal(new[] { false, true }, chunks.Select(chunk => chunk.Final));

    using UrdStore reopened = UrdStore.Open(database.Path);
    Guid nextExecution = Guid.NewGuid();
    reopened.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "second input", TestLlmSelection.Default), true, nextExecution);
    LlmRequest next = reopened.Executions.ClaimNextRequest(session, nextExecution, 0)!;
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User },
        next.Messages.Select(message => message.Role));
    Assert.Equal(new[] { "first input", "part🙂 final", "second input" },
        next.Messages.Select(message => message.Text));
  }
}
