using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Compaction;

public sealed class ManualCompactionTests {
  private static readonly LlmSelection Selection = TestLlmSelection.Default with { ContextWindow = 8_192 };
  private const string Summary = "Goal: retain the latest decision.";

  [Fact]
  public async Task IdleCompactionIsIdempotentAndKeepsReadableHistoryAndLatestModelContextAfterReopen() {
    using var database = new TemporaryDatabase();
    using var client = new ScriptedChatClient(Complete, _ => Task.FromResult(SummaryResponse()));
    Guid sessionId;
    long revision;
    CompactionSummary saved;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      PrimarySession session = CreateSession(store, client);
      sessionId = session.Id;
      await SubmitTurnAsync(session, "ancient-turn: " + new string('x', 100_000));
      await SubmitTurnAsync(session, "latest-turn: keep this decision");
      revision = (await session.ReadSnapshotAsync()).HistoryRevision;
      var submission = new ManualCompactionRequest(Guid.NewGuid(), revision, Selection);

      CompactionReceipt accepted = Assert.IsType<CompactionReceipt>(await session.CompactAsync(submission));
      CompactionReceipt retry = Assert.IsType<CompactionReceipt>(await session.CompactAsync(submission));
      Assert.Equal(accepted.ExecutionId, retry.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(accepted.ExecutionId).WaitAsync(TestTimeout)).Outcome);
      CompactionReceipt settled = Assert.IsType<CompactionReceipt>(await session.ReadCompactionReceiptAsync(submission.OperationId));
      Assert.Equal(accepted.ExecutionId, settled.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed, settled.Outcome);
      SavedHistoryCompaction boundary = Assert.IsType<SavedHistoryCompaction>(store.Compactions.ReadContext(session.Id, revision).PreviousCompaction);
      saved = Assert.IsType<CompactionSummary>(await session.ReadCompactionAsync(boundary.Id, revision));
      Assert.Equal(Summary, saved.Summary);
      Assert.Equal(3, client.Calls);
      Assert.Empty(client.Options[2]!.Tools!);

      TranscriptHistoryPage history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision));
      Assert.Single(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
      TranscriptEntryDescriptor original = history.Entries.First(entry => entry.Entry.Kind == TranscriptEntryKind.User);
      Assert.StartsWith("ancient-turn: ", (await session.Transcript.ReadContentAsync(original.Text!.From)).Text);
    }

    using (UrdStore reopened = UrdStore.Open(database.Path)) {
      PrimarySession session = new(Assert.IsType<SessionRecord>(reopened.Sessions.Read(sessionId)), reopened, _ => client);
      await SubmitTurnAsync(session, "after-reopen-turn");
      string modelContext = string.Join("\n", client.Requests[^1].SelectMany(message =>
          message.Contents.OfType<TextContent>().Select(content => content.Text)));
      Assert.Contains("[HISTORY SUMMARY:", modelContext);
      Assert.Contains(Summary, modelContext);
      Assert.Contains("latest-turn: keep this decision", modelContext);
      Assert.Contains("after-reopen-turn", modelContext);
      Assert.DoesNotContain("ancient-turn:", modelContext);
      Assert.Equal(saved.Id, Assert.IsType<CompactionSummary>(await session.ReadCompactionAsync(saved.Id, revision)).Id);

      var history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision));
      MessageRef latest = history.Entries.Last(entry => entry.Entry.Kind == TranscriptEntryKind.User).Text!.From.Message;
      var edited = await session.EditUserMessageAsync(new(Guid.NewGuid(), latest, "revised recent turn", TestLlmSelection.Default));
      await session.WaitForExecutionFinishAsync(edited.ExecutionId).WaitAsync(TestTimeout);
      Assert.Equal(saved.Id, reopened.Compactions.ReadContext(sessionId, edited.HistoryRevision).PreviousCompaction!.Id);
      Assert.Equal(saved, await session.ReadCompactionAsync(saved.Id, edited.HistoryRevision));

      history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, edited.HistoryRevision));
      MessageRef ancient = history.Entries.First(entry => entry.Entry.Kind == TranscriptEntryKind.User).Text!.From.Message;
      var reset = await session.EditUserMessageAsync(new(Guid.NewGuid(), ancient, "fresh beginning", TestLlmSelection.Default));
      await session.WaitForExecutionFinishAsync(reset.ExecutionId).WaitAsync(TestTimeout);
      Assert.Null(reopened.Compactions.ReadContext(sessionId, reset.HistoryRevision).PreviousCompaction);
      Assert.Null(await session.ReadCompactionAsync(saved.Id, reset.HistoryRevision));
      Assert.DoesNotContain(client.Requests[^1], message => message.Text?.Contains(Summary, StringComparison.Ordinal) == true);
    }
  }

  [Fact]
  public async Task OversizedLatestToolResultKeepsWholeTurnAndCompactsOlderHistory() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    // Below the model-facing output cap (ToolOutputLimits.MaxBytes) but far above the retained-history
    // token limit, so compaction still must keep the latest turn whole and cut older history.
    string toolResult = new('r', 48_000);
    var tool = new TestAgentTool("lookup", (_, _, _) => Task.FromResult(new ToolOutput(toolResult, [])));
    int streamCalls = 0;
    int summaryCalls = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref streamCalls) switch {
      1 => Complete(token),
      2 => ToolBatch(token, new FunctionCallContent("lookup-call", "lookup", new Dictionary<string, object?>())),
      3 => FailContinuation(token),
      _ => throw new InvalidOperationException("Unexpected model request.")
    }, _ => {
      Interlocked.Increment(ref summaryCalls);
      return Task.FromResult(SummaryResponse());
    });
    var manager = new SessionManager(store, _ => client, [tool], Path.GetDirectoryName(database.Path)!);
    try {
      PrimarySession session = await manager.CreatePrimaryAsync();
      await SubmitTurnAsync(session, "older-turn: " + new string('x', 10_000));
      UserMessageReceipt latest = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "latest-turn", Selection));
      Assert.Equal(ExecutionOutcome.Failed,
          (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(latest.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
      long revision = (await session.ReadSnapshotAsync()).HistoryRevision;
      ModelHistory before = store.Compactions.ReadContext(session.Id, revision);
      HistoryMessage latestStart = Assert.Single(before.Messages, message => message.Message.Text == "latest-turn");

      CompactionReceipt receipt = Assert.IsType<CompactionReceipt>(await session.CompactAsync(
          new(Guid.NewGuid(), revision, Selection)));
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(receipt.ExecutionId).WaitAsync(TestTimeout)).Outcome);

      ModelHistory after = store.Compactions.ReadContext(session.Id, revision);
      SavedHistoryCompaction saved = Assert.IsType<SavedHistoryCompaction>(after.PreviousCompaction);
      Assert.Equal(latestStart.Location, saved.FirstRetainedMessageLocation);
      Assert.Equal(Summary, Assert.IsType<CompactionSummary>(await session.ReadCompactionAsync(saved.Id, revision)).Summary);
      Assert.Single((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision))).Entries,
          entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
      Assert.DoesNotContain(after.Messages, message => message.Message.Text?.Contains("older-turn:") == true);
      Assert.Equal(before.Messages.SkipWhile(message => message.Location != latestStart.Location).Select(Position),
          after.Messages.Select(Position));
      Assert.Contains(after.Messages, message => message.Message.Role == ChatRole.Assistant &&
          message.Message.Contents.OfType<FunctionCallContent>().Any(call => call.CallId == "lookup-call"));
      Assert.Contains(after.Messages, message => message.Message.Role == ChatRole.Tool &&
          message.Message.Contents.OfType<FunctionResultContent>().Any(result => result.CallId == "lookup-call" &&
              Equals(result.Result, toolResult)));
      Assert.Equal(1, summaryCalls);
    } finally {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task QueuedPromptWaitsForManualCompactionToSettle(bool summaryFails) {
    using var database = new TemporaryDatabase();
    LlmSelection selection = Selection with { ContextWindow = 200_000 };
    var entered = Signal();
    var release = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    var summaryEntered = Signal();
    var summaryRelease = Signal();
    int calls = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref calls) == 3
        ? Paused(entered, release, token, cleanupEntered, cleanupRelease) : Complete(token), async cancellation => {
      summaryEntered.TrySetResult();
      await summaryRelease.Task.WaitAsync(cancellation);
      if (summaryFails)
        throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
      return SummaryResponse();
    });
    using UrdStore store = UrdStore.Open(database.Path);
    PrimarySession session = CreateSession(store, client);
    await SubmitTurnAsync(session, "ancient-turn: " + new string('x', 100_000));
    await SubmitTurnAsync(session, "latest-turn");
    UserMessageReceipt busy = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "working", TestLlmSelection.Default));
    Guid execution = Assert.IsType<Guid>(busy.ExecutionId);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      SessionSnapshot snapshot = await session.ReadSnapshotAsync();
      Assert.True(snapshot.CanCompact);
      var request = new ManualCompactionRequest(Guid.NewGuid(), snapshot.HistoryRevision, selection);
      CompactionReceipt queued = Assert.IsType<CompactionReceipt>(await session.CompactAsync(request));
      Assert.True(queued.Queued);
      Assert.Equal(request.OperationId, queued.ExecutionId);
      Assert.Equal(queued, await session.CompactAsync(request));
      Assert.Equal(queued, await session.ReadCompactionReceiptAsync(request.OperationId));
      Assert.Null(await session.CompactAsync(request with { OperationId = Guid.NewGuid() }));
      await Assert.ThrowsAsync<UserMessageConflictException>(() => session.CompactAsync(request with { Focus = "different" }));
      Assert.Equal(request.OperationId, (await session.ReadSnapshotAsync()).QueuedCompactionId);
      Assert.Single(StoredExecutions.Read(database), run => run.Outcome is null);
      release.TrySetResult();
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.False(summaryEntered.Task.IsCompleted);
      Assert.Equal(request.OperationId, (await session.ReadSnapshotAsync()).QueuedCompactionId);
      cleanupRelease.TrySetResult();
      await session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout);
      await summaryEntered.Task.WaitAsync(TestTimeout);
      SessionSnapshot running = await session.ReadSnapshotAsync();
      Assert.Null(running.QueuedCompactionId);
      Assert.Equal(request.OperationId, running.ManualCompactionId);

      var first = new UserMessageSubmission(Guid.NewGuid(), running.HistoryRevision, "first queued prompt", selection);
      var removed = first with { SubmissionId = Guid.NewGuid(), Text = "removed prompt" };
      var last = first with { SubmissionId = Guid.NewGuid(), Text = "last queued prompt" };
      UserMessageReceipt accepted = await session.SubmitUserMessageAsync(first);
      Assert.Equal(MessageAcceptance.Accepted, accepted.Acceptance);
      Guid nextExecution = Assert.IsType<Guid>(accepted.ExecutionId);
      Assert.NotEqual(request.OperationId, nextExecution);
      Assert.Equal(accepted, await session.SubmitUserMessageAsync(first));
      Assert.Equal(nextExecution, (await session.SubmitUserMessageAsync(removed)).ExecutionId);
      Assert.Equal(nextExecution, (await session.SubmitUserMessageAsync(last)).ExecutionId);
      Assert.False(await session.RemovePendingInputAsync(request.OperationId, removed.SubmissionId));
      Assert.True(await session.RemovePendingInputAsync(nextExecution, removed.SubmissionId));
      SessionSnapshot withQueue = await session.ReadSnapshotAsync();
      Assert.Equal(request.OperationId, withQueue.UnsettledExecutionId);
      Assert.Equal(nextExecution, withQueue.QueuedInputExecutionId);
      Assert.Equal(new[] { first.SubmissionId, last.SubmissionId }, withQueue.PendingInputs.Select(input => input.SubmissionId));
      Assert.DoesNotContain(client.Requests.SelectMany(messages => messages), message => message.Text == first.Text);

      summaryRelease.TrySetResult();
      ExecutionOutcome compactionOutcome = summaryFails ? ExecutionOutcome.Failed : ExecutionOutcome.Completed;
      Assert.Equal(compactionOutcome,
          (await session.WaitForExecutionFinishAsync(request.OperationId).WaitAsync(TestTimeout)).Outcome);
      CompactionReceipt settled = Assert.IsType<CompactionReceipt>(await session.ReadCompactionReceiptAsync(request.OperationId));
      Assert.False(settled.Queued);
      Assert.Equal(compactionOutcome, settled.Outcome);
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(nextExecution).WaitAsync(TestTimeout)).Outcome);
      var continuation = client.Requests[^1].Select(message => message.Text).ToArray();
      Assert.Equal(!summaryFails, continuation.Any(text => text?.Contains(Summary, StringComparison.Ordinal) == true));
      Assert.Equal(summaryFails, continuation.Any(text => text?.StartsWith("ancient-turn: ", StringComparison.Ordinal) == true));
      Assert.DoesNotContain("removed prompt", continuation);
      Assert.Equal(new[] { first.Text, last.Text }, continuation[^2..]);
      Assert.Equal(accepted, await session.ReadUserMessageReceiptAsync(first.SubmissionId));
    } finally {
      release.TrySetResult();
      cleanupRelease.TrySetResult();
      summaryRelease.TrySetResult();
    }
  }

  [Fact]
  public void RecoveryRetiresQueuedPromptWithoutReplayingIt() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid();
    Guid compactionId = Guid.NewGuid();
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "queued before interruption", Selection);
    UserMessageReceipt receipt;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(sessionId);
      Assert.NotNull(store.Compactions.Start(sessionId, new(compactionId, 0, Selection), accepting: true));
      receipt = store.UserMessages.Submit(sessionId, 0, submission, accepting: true, Guid.NewGuid(), compactionId).Receipt;
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(compactionId, reopened.Snapshots.ReadPersisted(sessionId).UnsettledExecutionId);
    reopened.RecoverInterruptedExecutions();

    Assert.Null(reopened.Snapshots.ReadPersisted(sessionId).UnsettledExecutionId);
    NotSentInput notSent = Assert.Single(reopened.NotSentInputs.Read(sessionId, cursor: null).Inputs);
    Assert.Equal(receipt.ExecutionId, notSent.ExecutionId);
    Assert.Equal(submission.SubmissionId, notSent.SubmissionId);
    Assert.Equal(receipt, reopened.UserMessages.ReadReceipt(sessionId, submission.SubmissionId));
  }

  [Fact]
  public async Task CancelQueuedCompactionIsIdempotentAndDoesNotRunAfterBusyExecution() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var release = Signal();
    int calls = 0;
    int summaries = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref calls) == 2
        ? Paused(entered, release, token) : Complete(token), _ => {
      Interlocked.Increment(ref summaries);
      return Task.FromResult(SummaryResponse());
    });
    PrimarySession session = CreateSession(store, client);
    await SubmitTurnAsync(session, new string('x', 100_000));
    UserMessageReceipt busy = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "busy turn", TestLlmSelection.Default));
    try {
      await entered.Task.WaitAsync(TestTimeout);
      var request = new ManualCompactionRequest(Guid.NewGuid(), 0, Selection);
      Assert.True(Assert.IsType<CompactionReceipt>(await session.CompactAsync(request)).Queued);

      Assert.Null(await session.CancelQueuedCompactionAsync(Guid.NewGuid()));
      Assert.Equal(request.OperationId, (await session.ReadSnapshotAsync()).QueuedCompactionId);
      Assert.True(Assert.IsType<CompactionReceipt>(await session.ReadCompactionReceiptAsync(request.OperationId)).Queued);

      CompactionReceipt cancelled = Assert.IsType<CompactionReceipt>(await session.CancelQueuedCompactionAsync(request.OperationId));
      Assert.Equal(request.OperationId, cancelled.ExecutionId);
      Assert.False(cancelled.Queued);
      Assert.Equal(ExecutionOutcome.Cancelled, cancelled.Outcome);
      Assert.Null((await session.ReadSnapshotAsync()).QueuedCompactionId);
      Assert.Equal(cancelled, await session.CancelQueuedCompactionAsync(request.OperationId));
      Assert.Equal(cancelled, await session.ReadCompactionReceiptAsync(request.OperationId));
      Assert.Equal(cancelled, await session.CompactAsync(request));
      Assert.False(string.IsNullOrWhiteSpace(cancelled.Error));

      var next = new ManualCompactionRequest(Guid.NewGuid(), 0, Selection);
      Assert.True(Assert.IsType<CompactionReceipt>(await session.CompactAsync(next)).Queued);
      Assert.Equal(cancelled, await session.CancelQueuedCompactionAsync(request.OperationId));
      Assert.Equal(next.OperationId, (await session.ReadSnapshotAsync()).QueuedCompactionId);
      Assert.NotNull(await session.CancelQueuedCompactionAsync(next.OperationId));
      Assert.Null(await session.CancelQueuedCompactionAsync(request.OperationId));

      release.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(busy.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(0, summaries);
      Assert.DoesNotContain(StoredExecutions.Read(database), run => run.ExecutionId == request.OperationId);
      Assert.Null(store.Compactions.ReadContext(session.Id, 0).PreviousCompaction);
    } finally {
      release.TrySetResult();
    }
  }

  [Fact]
  public async Task CancelQueuedCompactionDoesNotCancelStartedWork() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var busyEntered = Signal();
    var busyRelease = Signal();
    var entered = Signal();
    var release = Signal();
    int calls = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref calls) == 3
        ? Paused(busyEntered, busyRelease, token) : Complete(token), async cancellation => {
      entered.TrySetResult();
      await release.Task.WaitAsync(cancellation);
      return SummaryResponse();
    });
    PrimarySession session = CreateSession(store, client);
    await SubmitTurnAsync(session, new string('x', 100_000));
    await SubmitTurnAsync(session, "latest turn");
    UserMessageReceipt busy = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "busy turn", TestLlmSelection.Default));
    try {
      await busyEntered.Task.WaitAsync(TestTimeout);
      var request = new ManualCompactionRequest(Guid.NewGuid(), 0, Selection);
      CompactionReceipt accepted = Assert.IsType<CompactionReceipt>(await session.CompactAsync(request));
      Assert.True(accepted.Queued);
      busyRelease.TrySetResult();
      await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(busy.ExecutionId)).WaitAsync(TestTimeout);
      await entered.Task.WaitAsync(TestTimeout);
      Assert.Null(await session.CancelQueuedCompactionAsync(request.OperationId));
      Assert.Equal(request.OperationId, (await session.ReadSnapshotAsync()).ManualCompactionId);
      release.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(accepted.ExecutionId).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(ExecutionOutcome.Completed,
          Assert.IsType<CompactionReceipt>(await session.ReadCompactionReceiptAsync(request.OperationId)).Outcome);
      Assert.NotNull(store.Compactions.ReadContext(session.Id, 0).PreviousCompaction);
    } finally {
      busyRelease.TrySetResult();
      release.TrySetResult();
    }
  }

  [Fact]
  public async Task HistoryEditCancelsQueuedCompactionWithAReadableReceipt() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var release = Signal();
    int calls = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref calls) == 2
        ? Paused(entered, release, token) : Complete(token), _ => Task.FromResult(SummaryResponse()));
    PrimarySession session = CreateSession(store, client);
    await SubmitTurnAsync(session, new string('x', 8_000));
    TranscriptHistoryPage page = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
    MessageRef original = page.Entries.First(entry => entry.Entry.Kind == TranscriptEntryKind.User).Text!.From.Message;
    await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "busy turn", TestLlmSelection.Default));
    try {
      await entered.Task.WaitAsync(TestTimeout);
      var request = new ManualCompactionRequest(Guid.NewGuid(), 0, Selection);
      Assert.True(Assert.IsType<CompactionReceipt>(await session.CompactAsync(request)).Queued);
      MessageEditReceipt edited = await session.EditUserMessageAsync(
          new(Guid.NewGuid(), original, "replacement", TestLlmSelection.Default)).WaitAsync(TestTimeout);
      await session.WaitForExecutionFinishAsync(edited.ExecutionId).WaitAsync(TestTimeout);
      Assert.Null((await session.ReadSnapshotAsync()).QueuedCompactionId);
      CompactionReceipt discarded = Assert.IsType<CompactionReceipt>(await session.ReadCompactionReceiptAsync(request.OperationId));
      Assert.Equal(ExecutionOutcome.Cancelled, discarded.Outcome);
      Assert.Contains("history changed", discarded.Error);
      Assert.Equal(discarded, await session.CompactAsync(request));
      Assert.DoesNotContain(StoredExecutions.Read(database), run => run.ExecutionId == request.OperationId);
    } finally {
      release.TrySetResult();
    }
  }

  [Fact]
  public async Task EmptyHistoryCannotBeCompacted() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(Complete, _ => Task.FromResult(SummaryResponse()));
    PrimarySession session = CreateSession(store, client);
    SessionSnapshot snapshot = await session.ReadSnapshotAsync();
    Assert.False(snapshot.CanCompact);
    Assert.False(string.IsNullOrWhiteSpace(snapshot.CompactionUnavailableReason));
    Assert.Null(await session.CompactAsync(new(Guid.NewGuid(), snapshot.HistoryRevision, Selection)));
    Assert.Equal(0, client.Calls);
  }

  [Fact]
  public async Task MeasuredLowUsageCannotBeCompacted() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(LowUsage, _ => Task.FromResult(SummaryResponse()));
    PrimarySession session = CreateSession(store, client);
    await SubmitTurnAsync(session, "short turn");
    SessionSnapshot snapshot = await session.ReadSnapshotAsync();
    Assert.Equal(SessionContextUsageSource.Measured, snapshot.ContextUsage?.Source);
    Assert.False(snapshot.CanCompact);
    Assert.Contains("low", snapshot.CompactionUnavailableReason!, StringComparison.OrdinalIgnoreCase);
    Assert.Null(await session.CompactAsync(new(Guid.NewGuid(), snapshot.HistoryRevision, Selection)));
  }

  [Fact]
  public async Task QueuedCompactionSkipsWhenSettledUsageIsLow() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var release = Signal();
    int summaries = 0;
    using var client = new ScriptedChatClient(PausedLowUsage, _ => {
      Interlocked.Increment(ref summaries);
      return Task.FromResult(SummaryResponse());
    });
    PrimarySession session = CreateSession(store, client);
    UserMessageReceipt busy = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "short turn", TestLlmSelection.Default));
    try {
      await entered.Task.WaitAsync(TestTimeout);
      SessionSnapshot snapshot = await session.ReadSnapshotAsync();
      var request = new ManualCompactionRequest(Guid.NewGuid(), snapshot.HistoryRevision, Selection);
      Assert.True(Assert.IsType<CompactionReceipt>(await session.CompactAsync(request)).Queued);
      release.TrySetResult();
      await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(busy.ExecutionId)).WaitAsync(TestTimeout);
      await session.WaitForExecutionFinishAsync(request.OperationId).WaitAsync(TestTimeout);
      CompactionReceipt receipt = Assert.IsType<CompactionReceipt>(await session.ReadCompactionReceiptAsync(request.OperationId));
      Assert.Equal(ExecutionOutcome.Completed, receipt.Outcome);
      Assert.Contains("low", receipt.Error!, StringComparison.OrdinalIgnoreCase);
      Assert.Equal(0, summaries);
    } finally {
      release.TrySetResult();
    }

    async IAsyncEnumerable<ChatResponseUpdate> PausedLowUsage(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) {
      entered.TrySetResult();
      await release.Task.WaitAsync(token);
      await foreach (ChatResponseUpdate update in LowUsage(token))
        yield return update;
    }
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> LowUsage(
      [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) {
    await Task.Yield();
    token.ThrowIfCancellationRequested();
    yield return new() { Contents = [new TextContent("answer"), new UsageContent(new UsageDetails {
      InputTokenCount = 12, OutputTokenCount = 3, TotalTokenCount = 15
    })], FinishReason = ChatFinishReason.Stop };
  }

  private static IAsyncEnumerable<ChatResponseUpdate> FailContinuation(CancellationToken token) {
    token.ThrowIfCancellationRequested();
    throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
  }

  [Fact]
  public async Task StopDuringBlockedSummaryLeavesNoBoundaryAndJoinsSummaryCleanup() {
    using var database = new TemporaryDatabase();
    var entered = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    using var client = new ScriptedChatClient(Complete, async cancellation => {
      entered.TrySetResult();
      try {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        return SummaryResponse();
      } finally {
        cleanupEntered.TrySetResult();
        await cleanupRelease.Task;
      }
    });
    using UrdStore store = UrdStore.Open(database.Path);
    PrimarySession session = CreateSession(store, client);
    await SubmitTurnAsync(session, "ancient-turn: " + new string('x', 100_000));
    await SubmitTurnAsync(session, "latest-turn");
    long revision = (await session.ReadSnapshotAsync()).HistoryRevision;
    CompactionReceipt accepted = Assert.IsType<CompactionReceipt>(await session.CompactAsync(
        new(Guid.NewGuid(), revision, Selection)));
    Task<SessionExecutionResult> settlement = session.WaitForExecutionFinishAsync(accepted.ExecutionId);
    var submission = new UserMessageSubmission(Guid.NewGuid(), revision, "queued before Stop", Selection);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      UserMessageReceipt queued = await session.SubmitUserMessageAsync(submission);
      await session.RequestStopAsync(accepted.ExecutionId).WaitAsync(TestTimeout);
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.Equal(MessageAcceptance.Accepted, queued.Acceptance);
      Assert.Equal(queued, await session.SubmitUserMessageAsync(submission));
      Assert.Empty((await session.ReadSnapshotAsync()).PendingInputs);
      Assert.Equal(submission.SubmissionId, Assert.Single((await session.ReadNotSentInputsAsync()).Inputs).SubmissionId);
      Assert.False(settlement.IsCompleted);
      Assert.Null(store.Compactions.ReadContext(session.Id, revision).PreviousCompaction);
      Assert.DoesNotContain((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision))).Entries,
          entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
    } finally {
      cleanupRelease.TrySetResult();
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(ExecutionOutcome.Cancelled, (await settlement).Outcome);
    SessionSnapshot stopped = await session.ReadSnapshotAsync();
    Assert.Null(stopped.UnsettledExecutionId);
    Assert.Null(stopped.QueuedInputExecutionId);
    Assert.DoesNotContain(client.Requests.SelectMany(messages => messages), message => message.Text == submission.Text);
    Assert.Null(store.Compactions.ReadContext(session.Id, revision).PreviousCompaction);
  }

  [Fact]
  public async Task SummaryFailurePreservesPreviousContextAndDoesNotAddMarker() {
    using var database = new TemporaryDatabase();
    int summaryCalls = 0;
    using var client = new ScriptedChatClient(Complete, _ => Interlocked.Increment(ref summaryCalls) == 2
        ? Task.FromException<ChatResponse>(new ProviderOperationException(ProviderFailure.ProviderUnavailable, "summary service timed out"))
        : Task.FromResult(SummaryResponse()));
    using UrdStore store = UrdStore.Open(database.Path);
    PrimarySession session = CreateSession(store, client);
    await SubmitTurnAsync(session, "ancient-turn: " + new string('x', 100_000));
    await SubmitTurnAsync(session, "first-latest-turn");
    long revision = (await session.ReadSnapshotAsync()).HistoryRevision;
    CompactionReceipt first = Assert.IsType<CompactionReceipt>(await session.CompactAsync(
        new(Guid.NewGuid(), revision, Selection)));
    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForExecutionFinishAsync(first.ExecutionId).WaitAsync(TestTimeout)).Outcome);
    await SubmitTurnAsync(session, "new-large-turn: " + new string('y', 100_000));
    await SubmitTurnAsync(session, "second-latest-turn");
    ModelHistory before = store.Compactions.ReadContext(session.Id, revision);
    CompactionReceipt failed = Assert.IsType<CompactionReceipt>(await session.CompactAsync(
        new(Guid.NewGuid(), revision, Selection)));
    Assert.Equal(ExecutionOutcome.Failed,
        (await session.WaitForExecutionFinishAsync(failed.ExecutionId).WaitAsync(TestTimeout)).Outcome);

    CompactionReceipt final = Assert.IsType<CompactionReceipt>(await session.ReadCompactionReceiptAsync(failed.ExecutionId));
    Assert.Contains("summary service timed out", final.Error, StringComparison.Ordinal);
    ModelHistory after = store.Compactions.ReadContext(session.Id, revision);
    Assert.Equal(before.PreviousCompaction!.Id, after.PreviousCompaction!.Id);
    Assert.Equal(before.Messages.Select(Position), after.Messages.Select(Position));
    Assert.Single((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision))).Entries,
        entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
    Assert.Equal(2, summaryCalls);
    Assert.Empty(client.Options[2]!.Tools!);
    Assert.Empty(client.Options[5]!.Tools!);
  }

  private static (Guid Request, int Index, string? Text) Position(HistoryMessage entry) =>
      (entry.Location.RequestId, entry.Location.MessageIndexInRequest, entry.Message.Text);

  private static PrimarySession CreateSession(UrdStore store, ScriptedChatClient client) =>
      new(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);

  private static async Task SubmitTurnAsync(PrimarySession session, string text) {
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
        new(Guid.NewGuid(), 0, text, TestLlmSelection.Default));
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
  }

  private static ChatResponse SummaryResponse() => new(new ChatMessage(ChatRole.Assistant, Summary)) {
    FinishReason = ChatFinishReason.Stop
  };
}
