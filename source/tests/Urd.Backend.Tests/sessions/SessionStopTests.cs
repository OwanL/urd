using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

// Public Stop acknowledges durable intent; settlement still joins all locally owned work.
public sealed class SessionStopTests {
  [Fact]
  public async Task AcknowledgesWhileCancellationCallbackAndLlmCleanupAreHeld() {
    var entered = Signal();
    var callbackEntered = Signal();
    var callbackRelease = Signal();
    var callbackExited = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    int callbacks = 0;
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      // Deliberately retained until cancellation finishes; disposing this registration
      // in the iterator would couple the two independently held cleanup barriers.
      token.Register(() => {
        Interlocked.Increment(ref callbacks);
        callbackEntered.TrySetResult();
        callbackRelease.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
        callbackExited.TrySetResult();
      });
      try {
        yield return Text("working");
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
      } finally {
        cleanupEntered.TrySetResult();
        await cleanupRelease.Task;
      }
    }

    var client = new ScriptedChatClient(Response);
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("start");
    Task<SessionExecutionResult> settlement = fixture.Session.WaitForSettlementAsync(executionId);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      Assert.Equal(new StopReceipt(executionId, StopStatus.Requested),
          await fixture.Session.RequestStopAsync(executionId).WaitAsync(TestTimeout));
      await Task.WhenAll(callbackEntered.Task, cleanupEntered.Task).WaitAsync(TestTimeout);
      Assert.Equal(new StopReceipt(executionId, StopStatus.Requested),
          await fixture.Session.RequestStopAsync(executionId).WaitAsync(TestTimeout));
      Assert.Equal(1, Volatile.Read(ref callbacks));
      Assert.False(settlement.IsCompleted);
      Assert.True(Assert.Single(StoredExecutions.Read(fixture.Database)).Stopping);
      Assert.Null(Assert.Single(StoredExecutions.Read(fixture.Database)).Outcome);
      SessionSnapshot snapshot = await fixture.Session.ReadSnapshotAsync();
      Assert.True(snapshot.StopRequested);
      Assert.Equal(executionId, snapshot.UnsettledExecutionId);
      Assert.Null(snapshot.LastSettlement);
      UserMessageReceipt refused = await fixture.Session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "too soon", TestLlmSelection.Default));
      Assert.Equal(MessageRefusalReason.Stopping, refused.RefusalReason);

      callbackRelease.TrySetResult();
      await callbackExited.Task.WaitAsync(TestTimeout);
      Assert.False(settlement.IsCompleted); // Llm cleanup still owns the run.
      Assert.Equal(1, client.Calls);
    } finally {
      callbackRelease.TrySetResult();
      cleanupRelease.TrySetResult();
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(ExecutionOutcome.Cancelled, (await settlement).Outcome);
    Assert.Equal(new StopReceipt(executionId, StopStatus.Settled, ExecutionOutcome.Cancelled),
        await fixture.Session.RequestStopAsync(executionId).WaitAsync(TestTimeout));
  }

  [Fact]
  public async Task NoncooperatingParallelToolsKeepStopPendingUntilAllReturnWithoutLlmContinuation() {
    var entered = Signal();
    var release = Signal();
    var cancellationObserved = Signal();
    int effects = 0;
    async Task<int> Step(int number, CancellationToken token) {
      using CancellationTokenRegistration registration = token.Register(() => cancellationObserved.TrySetResult());
      if (Interlocked.Increment(ref effects) == 2)
        entered.TrySetResult();
      await release.Task; // Deliberately ignores cancellation; Stop cannot forcibly kill a function.
      return number;
    }

    AIFunction step = AIFunctionFactory.Create(Step, name: "step");
    var client = new ScriptedChatClient(token => ToolBatch(token,
        new("first", "step", new Dictionary<string, object?> { ["number"] = 1 }),
        new("second", "step", new Dictionary<string, object?> { ["number"] = 2 })));
    await using var fixture = new ExecutionFixture(client, [step]);
    Guid executionId = await fixture.StartExecutionAsync("start");
    Task<SessionExecutionResult> settlement = fixture.Session.WaitForSettlementAsync(executionId);
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "pending steering", TestLlmSelection.Default);
    UserMessageReceipt? accepted = null;
    try {
      await entered.Task.WaitAsync(TestTimeout);
      accepted = await fixture.Session.SubmitUserMessageAsync(submission);
      Assert.Equal(MessageAcceptance.Accepted, accepted.Acceptance);
      Assert.Equal(executionId, accepted.ExecutionId);
      SessionSnapshot pendingSnapshot = await fixture.Session.ReadSnapshotAsync();
      Assert.Contains(pendingSnapshot.PendingInputs, input => input.SubmissionId == submission.SubmissionId);
      Assert.Equal(new StopReceipt(executionId, StopStatus.Requested),
          await fixture.Session.RequestStopAsync(executionId).WaitAsync(TestTimeout));
      await cancellationObserved.Task.WaitAsync(TestTimeout);
      SessionSnapshot snapshot = await fixture.Session.ReadSnapshotAsync();
      Assert.True(snapshot.StopRequested);
      Assert.Equal(executionId, snapshot.UnsettledExecutionId);
      Assert.Null(snapshot.LastSettlement);
      Assert.False(settlement.IsCompleted);
      Assert.Empty(snapshot.PendingInputs); // Stop retires accepted inputs that never reached a send.
      Assert.Null(Assert.Single(StoredExecutions.Read(fixture.Database)).Outcome);
      TranscriptHistoryPage history = await fixture.Session.Transcript.ReadHistoryAsync(
          new(TranscriptHistoryMode.Latest, snapshot.HistoryRevision));
      Assert.DoesNotContain(history.Entries, entry => entry.Entry.EntryId == submission.SubmissionId);
      Assert.Empty(history.Settlements); // The run is not settled yet.
      StoredDelivery retired = Assert.Single(StoredDeliveries.Read(fixture.Database), row => row.SubmissionId == submission.SubmissionId);
      Assert.Null(retired.RequestId);
      Assert.Equal(ExecutionOutcome.Cancelled, retired.Outcome); // Retired in the acknowledgement transaction.
      Assert.Equal(accepted, await fixture.Session.SubmitUserMessageAsync(submission));
      Assert.Equal(2, Volatile.Read(ref effects));
      Assert.Equal(1, client.Calls);
      Assert.All(StoredTools.Read(fixture.Database), tool => {
        Assert.True(tool.Started);
        Assert.Null(tool.ResultJson);
      });
      UserMessageReceipt refused = await fixture.Session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "replacement", TestLlmSelection.Default));
      Assert.Equal(MessageRefusalReason.Stopping, refused.RefusalReason);
    } finally {
      release.TrySetResult();
      await fixture.Session.RequestStopAsync(executionId).WaitAsync(TestTimeout);
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(ExecutionOutcome.Cancelled, (await settlement).Outcome);
    Assert.Equal(2, Volatile.Read(ref effects));
    Assert.Equal(1, client.Calls);
    Assert.All(StoredTools.Read(fixture.Database), tool => {
      Assert.True(tool.Started); // Cancelled means Stop was observed, not that effects rolled back.
      Assert.Equal(ExecutionOutcome.Cancelled, tool.Outcome);
      Assert.NotNull(tool.ResultJson);
    });
    Assert.Equal(accepted, await fixture.Session.GetUserMessageReceiptAsync(submission.SubmissionId));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task StopSaveFailureStillCancelsLocallyAndBlocksLateOutputSuccessAndPendingWork(bool finalOutput) {
    var entered = Signal();
    var release = Signal();
    var cancelled = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    async IAsyncEnumerable<ChatResponseUpdate> UncooperativeResponse([EnumeratorCancellation] CancellationToken token) {
      using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
      yield return Text(new string('x', 16 * 1024));
      entered.TrySetResult();
      await release.Task; // The provider deliberately tries to return output after local cancellation.
      yield return Text(new string('y', 16 * 1024));
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    var client = new ScriptedChatClient(token => finalOutput
        ? new HeldDisposal(Complete(token), cleanupEntered, cleanupRelease.Task)
        : UncooperativeResponse(token));
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("start");
    Task<SessionExecutionResult> settlement = fixture.Session.WaitForSettlementAsync(executionId);
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "pending steering", TestLlmSelection.Default);
    UserMessageReceipt? accepted = null;
    try {
      await (finalOutput ? cleanupEntered.Task : entered.Task).WaitAsync(TestTimeout);
      accepted = await fixture.Session.SubmitUserMessageAsync(submission);
      Assert.Equal(MessageAcceptance.Accepted, accepted.Acceptance);
      StoredRequest request = Assert.Single(StoredExecutions.ReadRequests(fixture.Database));
      var lastChunk = StoredLlmResponses.Read(fixture.Database)[^1];
      Assert.Equal(finalOutput, lastChunk.Final);
      string outputBeforeStop = StoredLlmResponses.ReadText(fixture.Database);
      fixture.Database.Execute("""
        CREATE TRIGGER reject_stop BEFORE UPDATE OF stopping ON execution_runs
        WHEN NEW.stopping = 1
        BEGIN SELECT RAISE(ABORT, 'Injected Stop failure'); END;
        """);

      SqliteException failure = await Assert.ThrowsAsync<SqliteException>(
          () => fixture.Session.RequestStopAsync(executionId).WaitAsync(TestTimeout));
      Assert.Equal(19, failure.SqliteErrorCode);
      if (!finalOutput)
        await cancelled.Task.WaitAsync(TestTimeout);
      StoredExecution saved = Assert.Single(StoredExecutions.Read(fixture.Database));
      Assert.False(saved.Stopping); // The rejected transaction did not acknowledge durable intent.
      Assert.Null(saved.Outcome);
      SessionSnapshot snapshot = await fixture.Session.ReadSnapshotAsync();
      Assert.True(snapshot.AdmissionFenced);
      Assert.True(snapshot.StopRequested); // Local intent must not be hidden by the failed write.
      Assert.Equal(executionId, snapshot.UnsettledExecutionId);
      Assert.Null(snapshot.LastSettlement);
      Assert.Contains(snapshot.PendingInputs, input => input.SubmissionId == submission.SubmissionId);
      Assert.False(settlement.IsCompleted);
      UserMessageReceipt refused = await fixture.Session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "new work", TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
      Assert.Equal(MessageRefusalReason.Stopping, refused.RefusalReason);
      Assert.Equal(accepted, await fixture.Session.SubmitUserMessageAsync(submission));
      StoredDelivery pending = Assert.Single(StoredDeliveries.Read(fixture.Database), row => row.SubmissionId == submission.SubmissionId);
      Assert.Null(pending.RequestId);
      Assert.Null(pending.Outcome); // Retirement rolled back too; local cancellation must still prevent a claim.

      // Each probe is otherwise valid in storage, so rejection exercises the local Stop guard.
      if (finalOutput) {
        Assert.False(await fixture.Session.Recorder.CompleteRequestAsync(executionId,
            new(request.RequestId, ExecutionOutcome.Completed)));
      } else {
        Assert.False(await fixture.Session.Recorder.RecordOutputAsync(executionId,
            new(request.RequestId, lastChunk.ResponseId, lastChunk.ChunkIndex + 1, "late output", true)));
      }
      Assert.Equal(outputBeforeStop, StoredLlmResponses.ReadText(fixture.Database));
      Assert.Null(Assert.Single(StoredExecutions.ReadRequests(fixture.Database)).Outcome);
    } finally {
      release.TrySetResult();
      cleanupRelease.TrySetResult();
      fixture.Database.Execute("DROP TRIGGER IF EXISTS reject_stop;");
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(ExecutionOutcome.Cancelled, (await settlement).Outcome);
    Assert.Equal(ExecutionOutcome.Cancelled, Assert.Single(StoredExecutions.ReadRequests(fixture.Database)).Outcome);
    Assert.False(Assert.Single(StoredExecutions.Read(fixture.Database)).Stopping);
    Assert.True((await fixture.Session.ReadSnapshotAsync()).AdmissionFenced);
    Assert.Equal(finalOutput ? "done" : new string('x', 16 * 1024), StoredLlmResponses.ReadText(fixture.Database));
    Assert.Equal(finalOutput, StoredLlmResponses.Read(fixture.Database)[^1].Final);
    Assert.Equal(1, client.Calls);
    Assert.Equal(accepted, await fixture.Session.GetUserMessageReceiptAsync(submission.SubmissionId));
    SessionSnapshot settled = await fixture.Session.ReadSnapshotAsync();
    Assert.Equal(ExecutionOutcome.Cancelled, settled.LastSettlement!.Outcome);
    Assert.Empty(settled.PendingInputs);
    TranscriptHistoryPage history = await fixture.Session.Transcript.ReadHistoryAsync(
        new(TranscriptHistoryMode.Latest, settled.HistoryRevision));
    Assert.DoesNotContain(history.Entries, entry => entry.Entry.EntryId == submission.SubmissionId);
  }

  [Fact]
  public async Task StaleWrongSessionAndMissingStopsLeaveReplacementAndIndependentSessionUntouched() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var independentEntered = Signal();
    var independentRelease = Signal();
    var replacementEntered = Signal();
    var replacementRelease = Signal();
    CancellationToken independentToken = default;
    CancellationToken replacementToken = default;
    int request = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref request) switch {
      1 => Complete(token),
      2 => Independent(token),
      _ => Replacement(token)
    });
    IAsyncEnumerable<ChatResponseUpdate> Independent(CancellationToken token) {
      independentToken = token;
      return Paused(independentEntered, independentRelease, token);
    }
    IAsyncEnumerable<ChatResponseUpdate> Replacement(CancellationToken token) {
      replacementToken = token;
      return Paused(replacementEntered, replacementRelease, token);
    }

    var manager = new SessionManager(store, _ => client);
    PrimarySession target = await manager.CreatePrimaryAsync();
    PrimarySession independent = await manager.CreatePrimaryAsync();
    try {
      Guid original = await StartAsync(target, "original");
      await target.WaitForSettlementAsync(original).WaitAsync(TestTimeout);
      Guid independentExecution = await StartAsync(independent, "independent");
      await independentEntered.Task.WaitAsync(TestTimeout);
      Guid replacement = await StartAsync(target, "replacement");
      await replacementEntered.Task.WaitAsync(TestTimeout);
      Task<SessionExecutionResult> independentSettlement = independent.WaitForSettlementAsync(independentExecution);
      Task<SessionExecutionResult> replacementSettlement = target.WaitForSettlementAsync(replacement);

      Assert.Equal(new StopReceipt(original, StopStatus.Settled, ExecutionOutcome.Completed),
          await target.RequestStopAsync(original).WaitAsync(TestTimeout));
      Assert.Equal(new StopReceipt(independentExecution, StopStatus.NotFound),
          await target.RequestStopAsync(independentExecution).WaitAsync(TestTimeout));
      Assert.Equal(new StopReceipt(replacement, StopStatus.NotFound),
          await independent.RequestStopAsync(replacement).WaitAsync(TestTimeout));
      Guid missing = Guid.NewGuid();
      Assert.Equal(new StopReceipt(missing, StopStatus.NotFound),
          await target.RequestStopAsync(missing).WaitAsync(TestTimeout));
      Assert.False(independentToken.IsCancellationRequested);
      Assert.False(replacementToken.IsCancellationRequested);
      Assert.False(independentSettlement.IsCompleted);
      Assert.False(replacementSettlement.IsCompleted);

      Assert.Equal(new StopReceipt(replacement, StopStatus.Requested),
          await target.RequestStopAsync(replacement).WaitAsync(TestTimeout));
      Assert.Equal(ExecutionOutcome.Cancelled, (await replacementSettlement.WaitAsync(TestTimeout)).Outcome);
      Assert.False(independentToken.IsCancellationRequested);
      Assert.False(independentSettlement.IsCompleted);
      independentRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed, (await independentSettlement.WaitAsync(TestTimeout)).Outcome);
    } finally {
      independentRelease.TrySetResult();
      replacementRelease.TrySetResult();
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }

    async Task<Guid> StartAsync(PrimarySession session, string text) {
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, text, TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      return Assert.IsType<Guid>(receipt.ExecutionId);
    }
  }

}
