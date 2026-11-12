using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class BackgroundExecutionStorageTests {
  [Fact]
  public void CompletionPublicationWaitsForTheStreamingTailAndAnAuthorizedClaim() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, origin) = Start(store);
    const string callId = "background\"\n]";
    CompleteReply(store, session, execution, origin, callId);
    Assert.True(store.Tools.TryStart(session, execution, 0, origin, callId, background: true));
    LlmRequest streaming = store.Executions.ClaimNextRequest(session, execution, 0)!;
    Assert.True(store.Executions.StartRequest(session, execution, 0, streaming.RequestId));
    Guid output = Guid.NewGuid();
    Assert.True(store.LlmResponses.Append(session, execution, 0, new(streaming.RequestId, output, 0, "part", false)));
    Assert.True(store.Tools.Complete(session, execution, 0, origin, Result(callId, "actual output")));
    Assert.Empty(Completions(store, session));
    Assert.DoesNotContain(Read(database, session, execution, streaming.RequestId), IsCompletion);
    Assert.True(store.LlmResponses.Append(session, execution, 0, new(streaming.RequestId, output, 1, " done", true)));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, new(streaming.RequestId, ExecutionOutcome.Completed)));
    TranscriptPositionState before = store.Positions.Summary(session, 0);
    Assert.Null(store.Executions.ClaimNextRequest(session, Guid.NewGuid(), 0));
    Assert.Null(store.Executions.ClaimNextRequest(session, execution, 1));
    Assert.Equal(before, store.Positions.Summary(session, 0));
    Assert.Empty(Completions(store, session));
    store.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "steering", TestLlmSelection.Default), true, execution, execution);

    LlmRequest next = store.Executions.ClaimNextRequest(session, execution, 0)!;
    TranscriptEntryDescriptor completion = Assert.Single(Completions(store, session));
    Assert.Equal(execution, completion.Entry.ExecutionId);
    Assert.Equal(origin, completion.Tool!.RequestId);
    Assert.Equal(callId, completion.Tool.CallId);
    Assert.Equal("fake", completion.Tool.Name);
    Assert.Equal(ExecutionOutcome.Completed, completion.Tool.Outcome);
    Assert.True(completion.Tool.IsClosed);
    Assert.NotNull(completion.CreatedAt);
    TranscriptPosition position = store.Positions.LocateEntry(session, 0, completion.Entry).Target!;
    Assert.Equal(before.EntryCount, position.Rank);
    Assert.Equal(before.TotalWeight, position.PrefixWeight);
    Assert.Equal(256, position.Weight);
    Assert.Equal("part done", next.Messages[^3].Text);
    Assert.True(IsCompletion(next.Messages[^2]));
    using JsonDocument metadata = JsonDocument.Parse(next.Messages[^2].Contents.OfType<TextContent>().First().Text.Split('\n')[1]);
    Assert.Equal(callId, metadata.RootElement.GetProperty("call_id").GetString());
    Assert.Equal("steering", next.Messages[^1].Text);
    Assert.DoesNotContain(Read(database, session, execution, streaming.RequestId), IsCompletion);
    Assert.True(store.Executions.StartRequest(session, execution, 0, next.RequestId));
    Assert.True(store.Executions.StartRequest(session, execution, 0, next.RequestId));
    TranscriptHistoryPage history = store.History.Read(session, new(TranscriptHistoryMode.Latest, 0));
    Assert.Equal(completion.Entry, history.Entries[^2].Entry);
    Assert.Equal(TranscriptEntryKind.User, history.Entries[^1].Entry.Kind);
    Assert.Single(Completions(store, session));
  }

  [Theory]
  [InlineData(false, false)]
  [InlineData(true, false)]
  [InlineData(false, true)]
  [InlineData(true, true)]
  public void SettlementOrRecoveryReleasesOnlyNeverAttemptedCompletionRecipients(bool attempted, bool recovery) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, origin) = Start(store);
    CompleteReply(store, session, execution, origin, "background");
    Assert.True(store.Tools.TryStart(session, execution, 0, origin, "background", background: true));
    Assert.True(store.Tools.Complete(session, execution, 0, origin, Result("background", "saved")));
    LlmRequest recipient = store.Executions.ClaimNextRequest(session, execution, 0)!;
    TranscriptEntryRef identity = Assert.Single(Completions(store, session)).Entry;
    if (attempted)
      Assert.True(store.Executions.StartRequest(session, execution, 0, recipient.RequestId));
    if (recovery) {
      store.RecoverInterruptedExecutions();
    } else {
      store.Executions.RequestStop(session, execution, ownsExecution: true);
      Assert.Equal(ExecutionOutcome.Cancelled, store.Executions.TrySettle(session, execution, ExecutionOutcome.Cancelled)!.Outcome);
    }
    object? owner = database.Scalar("SELECT claimed_request_id FROM execution_tool_completions;"); // raw-sql: No reader exposes recipient ownership; settlement must atomically release only never-attempted claims.
    Assert.Equal(attempted ? recipient.RequestId.ToString("D") : null, owner as string);
    Guid nextExecution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "deliberate continuation", TestLlmSelection.Default), true, nextExecution);
    LlmRequest next = store.Executions.ClaimNextRequest(session, nextExecution, 0)!;
    Assert.Equal((attempted ? recipient.RequestId : next.RequestId).ToString("D"), database.Scalar("SELECT claimed_request_id FROM execution_tool_completions;")); // raw-sql: A new execution must reclaim the original event only when its earlier recipient was never attempted.
    Assert.Equal(identity, Assert.Single(Completions(store, session)).Entry);
    Assert.Single(next.Messages, IsCompletion);
    Assert.Equal("deliberate continuation", next.Messages[^1].Text);
  }

  [Fact]
  public void SettlementMarksSyntheticToolResultsAsInterrupted() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, origin) = Start(store);
    CompleteReply(store, session, execution, origin, "unstarted", "started");
    Assert.True(store.Tools.TryStart(session, execution, 0, origin, "started"));
    store.Executions.RequestStop(session, execution, ownsExecution: true);

    Assert.Equal(ExecutionOutcome.Cancelled,
        store.Executions.TrySettle(session, execution, ExecutionOutcome.Cancelled)!.Outcome);
    TranscriptEntryDescriptor[] tools = store.History.Read(session,
        new(TranscriptHistoryMode.Latest, 0)).Entries
        .Where(entry => entry.Entry.Kind == TranscriptEntryKind.Tool).ToArray();
    Assert.All(tools, entry => {
      Assert.True(entry.Tool!.Interrupted);
      Assert.Equal(ExecutionOutcome.Cancelled, entry.Tool.Outcome);
    });
    Assert.False(Assert.Single(tools, entry => entry.Tool!.CallId == "unstarted").Tool!.Started);
    Assert.True(Assert.Single(tools, entry => entry.Tool!.CallId == "started").Tool!.Started);
  }

  [Fact]
  public void GenuineToolFailureIsNotMarkedInterrupted() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var (session, execution, origin) = Start(store);
    CompleteReply(store, session, execution, origin, "failed");
    Assert.True(store.Tools.TryStart(session, execution, 0, origin, "failed"));
    var failure = new LlmToolResult("failed", ExecutionOutcome.Failed,
        new ToolOutput("tool returned an error", [], IsError: true));

    Assert.True(store.Tools.Complete(session, execution, 0, origin, failure));
    TranscriptToolFacts tool = Assert.Single(store.History.Read(session,
        new(TranscriptHistoryMode.Latest, 0)).Entries,
        entry => entry.Tool?.CallId == "failed").Tool!;
    Assert.True(tool.Started);
    Assert.Equal(ExecutionOutcome.Failed, tool.Outcome);
    Assert.False(tool.Interrupted);
  }

  [Fact]
  public void ExplicitRecoveryClosesInterruptedWorkWithoutReplayingOrDuplicatingResults() {
    using var database = new TemporaryDatabase();
    Guid session, execution, origin, streamingSession, streamingExecution, streamingRequest, pendingInput = Guid.NewGuid();
    string savedJson;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      (session, execution, origin) = Start(store);
      CompleteReply(store, session, execution, origin, "unstarted", "foreground", "background", "saved");
      Assert.True(store.Tools.TryStart(session, execution, 0, origin, "foreground"));
      Assert.True(store.Tools.TryStart(session, execution, 0, origin, "background", background: true));
      Assert.True(store.Tools.TryStart(session, execution, 0, origin, "saved", background: true));
      Assert.True(store.Tools.Complete(session, execution, 0, origin, Result("saved", "actual saved result")));
      savedJson = Assert.Single(StoredTools.Read(database), tool => tool.CallId == "saved").ResultJson!;
      (streamingSession, streamingExecution, streamingRequest) = Start(store);
      Assert.True(store.LlmResponses.Append(streamingSession, streamingExecution, 0,
          new(streamingRequest, Guid.NewGuid(), 0, "interrupted text", false)));
      store.UserMessages.Admit(streamingSession, 0,
          new(pendingInput, 0, "not yet sent", TestLlmSelection.Default), true, streamingExecution, streamingExecution);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Null(reopened.Executions.ReadSettlement(session, execution));
    Assert.Empty(Completions(reopened, session));
    reopened.RecoverInterruptedExecutions();
    TranscriptEntryDescriptor[] recoveredTools = reopened.History.Read(session,
        new(TranscriptHistoryMode.Latest, 0)).Entries
        .Where(entry => entry.Entry.Kind == TranscriptEntryKind.Tool).ToArray();
    TranscriptToolFacts recoveredUnstarted = Assert.Single(recoveredTools,
        entry => entry.Tool?.CallId == "unstarted").Tool!;
    Assert.False(recoveredUnstarted.Started);
    Assert.True(recoveredUnstarted.Interrupted);
    Assert.Equal(ExecutionOutcome.Failed, recoveredUnstarted.Outcome);
    foreach (string call in new[] { "foreground", "background" }) {
      TranscriptToolFacts recoveredStarted = Assert.Single(recoveredTools,
          entry => entry.Tool?.CallId == call).Tool!;
      Assert.True(recoveredStarted.Started);
      Assert.True(recoveredStarted.Interrupted);
      Assert.Equal(ExecutionOutcome.Failed, recoveredStarted.Outcome);
    }
    TranscriptToolFacts savedToolFacts = Assert.Single(recoveredTools, entry => entry.Tool?.CallId == "saved").Tool!;
    Assert.False(savedToolFacts.Interrupted);
    Assert.All(StoredExecutions.Read(database), run => Assert.Equal(ExecutionOutcome.Failed, run.Outcome));
    Assert.Equal(ExecutionOutcome.Failed, Assert.Single(StoredExecutions.ReadRequests(database), request => request.RequestId == streamingRequest).Outcome);
    Assert.All(StoredDeliveries.Read(database), input => Assert.NotNull(input.Outcome));
    Assert.Equal(pendingInput, Assert.Single(reopened.NotSentInputs.Read(streamingSession, null).Inputs).SubmissionId);
    Assert.Null(reopened.Snapshots.ReadStored(session).UnsettledExecutionId);
    Assert.Empty(reopened.Snapshots.ReadStored(streamingSession).PendingInputs);
    TranscriptTextFacts text = Assert.Single(reopened.History.Read(streamingSession,
        new(TranscriptHistoryMode.Latest, 0)).Entries, entry => entry.Entry.Kind == TranscriptEntryKind.Text).Text!;
    Assert.True(text.IsClosed);
    Assert.False(text.FinalOutputCommitted);
    Assert.Equal(1, text.AvailableThrough.SegmentIndex);
    ToolOutput unstarted = reopened.Tools.ReadOutput(session, origin, "unstarted")!;
    Assert.Contains("not executed", unstarted.Text);
    Assert.False(Assert.Single(StoredTools.Read(database), tool => tool.CallId == "unstarted").Started);
    foreach (string call in new[] { "foreground", "background" }) {
      ToolOutput unknown = reopened.Tools.ReadOutput(session, origin, call)!;
      Assert.True(unknown.IsError);
      Assert.Contains("effects may have occurred", unknown.Text);
      Assert.False(reopened.Tools.TryStart(session, execution, 0, origin, call, background: call == "background"));
    }
    Assert.Equal(savedJson, Assert.Single(StoredTools.Read(database), tool => tool.CallId == "saved").ResultJson);
    Assert.Equal("actual saved result", reopened.Tools.ReadOutput(session, origin, "saved")!.Text);
    TranscriptEntryRef[] identities = Completions(reopened, session).Select(entry => entry.Entry).ToArray();
    Assert.Equal(2, identities.Length);
    TranscriptPositionState positions = reopened.Positions.Summary(session, 0);
    reopened.RecoverInterruptedExecutions();
    Assert.Equal(identities, Completions(reopened, session).Select(entry => entry.Entry));
    Assert.Equal(positions, reopened.Positions.Summary(session, 0));
    Guid nextExecution = Guid.NewGuid();
    reopened.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "continue deliberately", TestLlmSelection.Default), true, nextExecution);
    LlmRequest next = reopened.Executions.ClaimNextRequest(session, nextExecution, 0)!;
    Assert.Equal(2, next.Messages.Count(IsCompletion));
    ChatMessage saved = Assert.Single(next.Messages.Where(IsCompletion),
        message => message.Text.EndsWith("actual saved result", StringComparison.Ordinal));
    Assert.Same(saved, next.Messages.First(IsCompletion)); // Saved completion predates the recovery event, despite call order.
    using JsonDocument metadata = JsonDocument.Parse(saved.Contents.OfType<TextContent>().First().Text.Split('\n')[1]);
    Assert.Equal("saved", metadata.RootElement.GetProperty("call_id").GetString());
    Assert.Equal("continue deliberately", next.Messages[^1].Text);
    Assert.Equal(identities, Completions(reopened, session).Select(entry => entry.Entry));
  }

  private static bool IsCompletion(ChatMessage message) =>
      message.Role == ChatRole.User && message.Text.StartsWith("[TOOL-ORIGIN: background tool completion;", StringComparison.Ordinal);

  private static TranscriptEntryDescriptor[] Completions(UrdStore store, Guid session) =>
      store.History.Read(session, new(TranscriptHistoryMode.Latest, 0)).Entries
          .Where(entry => entry.Entry.Kind == TranscriptEntryKind.ToolCompletion).ToArray();

  private static LlmToolResult Result(string call, string text) =>
      new(call, ExecutionOutcome.Completed, new ToolOutput(text, []));

  private static ReadOnlyCollection<ChatMessage> Read(TemporaryDatabase fixture, Guid session, Guid execution, Guid request) {
    using var database = new Database(fixture.OpenReadOnlyConnection());
    var results = new ToolResultCodec(new ToolImageStore(fixture.Path + ".tool-images"));
    return database.Read(transaction => new LlmContextReader(database, results).Read(transaction, session, execution, 0, request));
  }

  private static (Guid Session, Guid Execution, Guid Request) Start(UrdStore store) {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    store.UserMessages.Admit(session, 0,
        new(Guid.NewGuid(), 0, "request", TestLlmSelection.Default), true, execution);
    Guid request = store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId;
    Assert.True(store.Executions.StartRequest(session, execution, 0, request));
    return (session, execution, request);
  }

  private static void CompleteReply(UrdStore store, Guid session, Guid execution, Guid request, params string[] tools) {
    Assert.True(store.LlmResponses.Append(session, execution, 0, new(request, Guid.NewGuid(), 0, "", true)));
    Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
        new(request, ExecutionOutcome.Completed, ToolCalls: tools.Select(call =>
            new LlmToolCall(call, "fake", JsonSerializer.SerializeToElement(new { background = true }))).ToArray())));
  }
}
