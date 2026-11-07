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
  public async Task AcknowledgesWhileCancellationCallbackAndModelCleanupAreHeld() {
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
    Guid runId = await fixture.StartRunAsync("start");
    Task<ExecutionResult> settlement = fixture.Session.WaitForSettlementAsync(runId);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      Assert.Equal(new StopReceipt(runId, StopStatus.Requested),
          await fixture.Session.RequestStopAsync(runId).WaitAsync(TestTimeout));
      await Task.WhenAll(callbackEntered.Task, cleanupEntered.Task).WaitAsync(TestTimeout);
      Assert.Equal(new StopReceipt(runId, StopStatus.Requested),
          await fixture.Session.RequestStopAsync(runId).WaitAsync(TestTimeout));
      Assert.Equal(1, Volatile.Read(ref callbacks));
      Assert.False(settlement.IsCompleted);
      Assert.True(Assert.Single(StoredRuns.Read(fixture.Database)).Stopping);
      Assert.Null(Assert.Single(StoredRuns.Read(fixture.Database)).Outcome);
      SessionSnapshot snapshot = await fixture.Session.ReadSnapshotAsync();
      Assert.True(snapshot.StopRequested);
      Assert.Equal(runId, snapshot.UnsettledRunId);
      Assert.Null(snapshot.LastSettlement);
      InputReceipt refused = await fixture.Session.SubmitUserInputAsync(new(Guid.NewGuid(), 0, "too soon"));
      Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);

      callbackRelease.TrySetResult();
      await callbackExited.Task.WaitAsync(TestTimeout);
      Assert.False(settlement.IsCompleted); // Model cleanup still owns the run.
      Assert.Equal(1, client.Calls);
    } finally {
      callbackRelease.TrySetResult();
      cleanupRelease.TrySetResult();
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(ExecutionOutcome.Cancelled, (await settlement).Outcome);
    Assert.Equal(new StopReceipt(runId, StopStatus.Settled, ExecutionOutcome.Cancelled),
        await fixture.Session.RequestStopAsync(runId).WaitAsync(TestTimeout));
  }

  [Fact]
  public async Task NoncooperatingFakeToolRemainsStoppingUntilReleasedWithoutFollowingToolOrModelWork() {
    var entered = Signal();
    var release = Signal();
    var cancellationObserved = Signal();
    int effects = 0;
    async Task<int> Step(int number, CancellationToken token) {
      Interlocked.Increment(ref effects);
      using CancellationTokenRegistration registration = token.Register(() => cancellationObserved.TrySetResult());
      entered.TrySetResult();
      await release.Task; // Deliberately ignores cancellation; Stop cannot forcibly kill a function.
      return number;
    }

    AIFunction step = AIFunctionFactory.Create(Step, name: "step");
    var client = new ScriptedChatClient(token => ToolBatch(token,
        new("first", "step", new Dictionary<string, object?> { ["number"] = 1 }),
        new("second", "step", new Dictionary<string, object?> { ["number"] = 2 })));
    await using var fixture = new ExecutionFixture(client, [step]);
    Guid runId = await fixture.StartRunAsync("start");
    Task<ExecutionResult> settlement = fixture.Session.WaitForSettlementAsync(runId);
    var command = new InputCommand(Guid.NewGuid(), 0, "pending steering");
    InputReceipt? accepted = null;
    try {
      await entered.Task.WaitAsync(TestTimeout);
      accepted = await fixture.Session.SubmitUserInputAsync(command);
      Assert.Equal(InputDecision.Accepted, accepted.Decision);
      Assert.Equal(runId, accepted.RunId);
      Assert.Equal(new StopReceipt(runId, StopStatus.Requested),
          await fixture.Session.RequestStopAsync(runId).WaitAsync(TestTimeout));
      await cancellationObserved.Task.WaitAsync(TestTimeout);
      SessionSnapshot snapshot = await fixture.Session.ReadSnapshotAsync();
      Assert.True(snapshot.StopRequested);
      Assert.Equal(runId, snapshot.UnsettledRunId);
      Assert.Null(snapshot.LastSettlement);
      Assert.False(settlement.IsCompleted);
      Assert.Null(Assert.Single(StoredRuns.Read(fixture.Database)).Outcome);
      Assert.Null(Assert.Single(snapshot.Messages,
          message => message.Message.MessageId == command.InputId).Settlement); // The run is not settled yet.
      StoredDelivery retired = Assert.Single(StoredDeliveries.Read(fixture.Database), row => row.InputId == command.InputId);
      Assert.Null(retired.RequestId);
      Assert.Equal(ExecutionOutcome.Cancelled, retired.Outcome); // Retired in the acknowledgement transaction.
      Assert.Equal(accepted, await fixture.Session.SubmitUserInputAsync(command));
      Assert.Equal(1, Volatile.Read(ref effects));
      Assert.Equal(1, client.Calls);
      StoredTool[] heldTools = StoredTools.Read(fixture.Database).ToArray();
      Assert.True(heldTools[0].Started);
      Assert.Null(heldTools[0].ResultJson);
      Assert.False(heldTools[1].Started);
      InputReceipt refused = await fixture.Session.SubmitUserInputAsync(new(Guid.NewGuid(), 0, "replacement"));
      Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);
    } finally {
      release.TrySetResult();
      await fixture.Session.RequestStopAsync(runId).WaitAsync(TestTimeout);
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(ExecutionOutcome.Cancelled, (await settlement).Outcome);
    Assert.Equal(1, Volatile.Read(ref effects));
    Assert.Equal(1, client.Calls);
    StoredTool[] tools = StoredTools.Read(fixture.Database).ToArray();
    Assert.Equal(ExecutionOutcome.Completed, tools[0].Outcome); // Preserve actual effects, not a fictitious cancellation.
    Assert.NotNull(tools[0].ResultJson);
    Assert.False(tools[1].Started);
    Assert.Equal(ExecutionOutcome.Cancelled, tools[1].Outcome);
    Assert.NotNull(tools[1].ResultJson);
    Assert.Equal(accepted, await fixture.Session.GetInputReceiptAsync(command.InputId));
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
    Guid runId = await fixture.StartRunAsync("start");
    Task<ExecutionResult> settlement = fixture.Session.WaitForSettlementAsync(runId);
    var command = new InputCommand(Guid.NewGuid(), 0, "pending steering");
    InputReceipt? accepted = null;
    try {
      await (finalOutput ? cleanupEntered.Task : entered.Task).WaitAsync(TestTimeout);
      accepted = await fixture.Session.SubmitUserInputAsync(command);
      Assert.Equal(InputDecision.Accepted, accepted.Decision);
      StoredRequest request = Assert.Single(StoredRuns.ReadRequests(fixture.Database));
      var lastChunk = StoredOutput.Read(fixture.Database)[^1];
      Assert.Equal(finalOutput, lastChunk.Final);
      string outputBeforeStop = StoredOutput.ReadText(fixture.Database);
      fixture.Database.Execute("""
        CREATE TRIGGER reject_stop BEFORE UPDATE OF stopping ON execution_runs
        WHEN NEW.stopping = 1
        BEGIN SELECT RAISE(ABORT, 'Injected Stop failure'); END;
        """);

      SqliteException failure = await Assert.ThrowsAsync<SqliteException>(
          () => fixture.Session.RequestStopAsync(runId).WaitAsync(TestTimeout));
      Assert.Equal(19, failure.SqliteErrorCode);
      if (!finalOutput)
        await cancelled.Task.WaitAsync(TestTimeout);
      StoredRun saved = Assert.Single(StoredRuns.Read(fixture.Database));
      Assert.False(saved.Stopping); // The rejected transaction did not acknowledge durable intent.
      Assert.Null(saved.Outcome);
      SessionSnapshot snapshot = await fixture.Session.ReadSnapshotAsync();
      Assert.True(snapshot.AdmissionFenced);
      Assert.True(snapshot.StopRequested); // Local intent must not be hidden by the failed write.
      Assert.Equal(runId, snapshot.UnsettledRunId);
      Assert.Null(snapshot.LastSettlement);
      Assert.False(settlement.IsCompleted);
      InputReceipt refused = await fixture.Session.SubmitUserInputAsync(new(Guid.NewGuid(), 0, "new work"));
      Assert.Equal(InputDecision.Refused, refused.Decision);
      Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);
      Assert.Equal(accepted, await fixture.Session.SubmitUserInputAsync(command));
      StoredDelivery pending = Assert.Single(StoredDeliveries.Read(fixture.Database), row => row.InputId == command.InputId);
      Assert.Null(pending.RequestId);
      Assert.Null(pending.Outcome); // Retirement rolled back too; local cancellation must still prevent a claim.

      // Each probe is otherwise valid in storage, so rejection exercises the local Stop guard.
      if (finalOutput) {
        Assert.False(await fixture.Session.CompleteRequestAsync(runId,
            new(request.RequestId, ExecutionOutcome.Completed)));
      } else {
        Assert.False(await fixture.Session.RecordOutputAsync(runId,
            new(request.RequestId, lastChunk.ResponseId, lastChunk.ChunkIndex + 1, "late output", true)));
      }
      Assert.Equal(outputBeforeStop, StoredOutput.ReadText(fixture.Database));
      Assert.Null(Assert.Single(StoredRuns.ReadRequests(fixture.Database)).Outcome);
    } finally {
      release.TrySetResult();
      cleanupRelease.TrySetResult();
      fixture.Database.Execute("DROP TRIGGER IF EXISTS reject_stop;");
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(ExecutionOutcome.Cancelled, (await settlement).Outcome);
    Assert.Equal(ExecutionOutcome.Cancelled, Assert.Single(StoredRuns.ReadRequests(fixture.Database)).Outcome);
    Assert.False(Assert.Single(StoredRuns.Read(fixture.Database)).Stopping);
    Assert.True((await fixture.Session.ReadSnapshotAsync()).AdmissionFenced);
    Assert.Equal(finalOutput ? "done" : new string('x', 16 * 1024), StoredOutput.ReadText(fixture.Database));
    Assert.Equal(finalOutput, StoredOutput.Read(fixture.Database)[^1].Final);
    Assert.Equal(1, client.Calls);
    Assert.Equal(accepted, await fixture.Session.GetInputReceiptAsync(command.InputId));
    SessionSnapshot settled = await fixture.Session.ReadSnapshotAsync();
    Assert.Equal(ExecutionOutcome.Cancelled, Assert.Single(settled.Messages,
        message => message.Message.MessageId == command.InputId).Settlement);
  }

  [Fact]
  public async Task StaleWrongSessionAndMissingStopsLeaveReplacementAndIndependentSessionUntouched() {
    var database = new TemporaryDatabase();
    SessionStore store = SessionStore.Open(database.Path);
    var independentEntered = Signal();
    var independentRelease = Signal();
    var replacementEntered = Signal();
    var replacementRelease = Signal();
    CancellationToken independentToken = default;
    CancellationToken replacementToken = default;
    int request = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref request) switch {
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

    var manager = new SessionManager(store, client);
    var runs = new List<(PrimarySession Session, Guid RunId)>();
    try {
      PrimarySession target = await manager.CreatePrimaryAsync();
      PrimarySession independent = await manager.CreatePrimaryAsync();
      Guid original = await StartAsync(target, "original");
      await target.WaitForSettlementAsync(original).WaitAsync(TestTimeout);
      Guid independentRun = await StartAsync(independent, "independent");
      await independentEntered.Task.WaitAsync(TestTimeout);
      Guid replacement = await StartAsync(target, "replacement");
      await replacementEntered.Task.WaitAsync(TestTimeout);
      Task<ExecutionResult> independentSettlement = independent.WaitForSettlementAsync(independentRun);
      Task<ExecutionResult> replacementSettlement = target.WaitForSettlementAsync(replacement);

      Assert.Equal(new StopReceipt(original, StopStatus.Settled, ExecutionOutcome.Completed),
          await target.RequestStopAsync(original).WaitAsync(TestTimeout));
      Assert.Equal(new StopReceipt(independentRun, StopStatus.NotFound),
          await target.RequestStopAsync(independentRun).WaitAsync(TestTimeout));
      Assert.Equal(new StopReceipt(replacement, StopStatus.NotFound),
          await independent.RequestStopAsync(replacement).WaitAsync(TestTimeout));
      Guid missing = Guid.NewGuid();
      Assert.Equal(new StopReceipt(missing, StopStatus.NotFound),
          await target.RequestStopAsync(missing).WaitAsync(TestTimeout));
      await Assert.ThrowsAsync<ArgumentException>(() => target.RequestStopAsync(Guid.Empty));
      Assert.False(independentToken.IsCancellationRequested);
      Assert.False(replacementToken.IsCancellationRequested);
      Assert.False(independentSettlement.IsCompleted);
      Assert.False(replacementSettlement.IsCompleted);
      Assert.False((await target.ReadSnapshotAsync()).StopRequested);
      Assert.False((await independent.ReadSnapshotAsync()).StopRequested);

      Assert.Equal(new StopReceipt(replacement, StopStatus.Requested),
          await target.RequestStopAsync(replacement).WaitAsync(TestTimeout));
      Assert.Equal(ExecutionOutcome.Cancelled, (await replacementSettlement.WaitAsync(TestTimeout)).Outcome);
      Assert.False(independentToken.IsCancellationRequested);
      Assert.False(independentSettlement.IsCompleted);
      Assert.False((await independent.ReadSnapshotAsync()).StopRequested);
      independentRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed, (await independentSettlement.WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(3, client.Calls);
    } finally {
      independentRelease.TrySetResult();
      replacementRelease.TrySetResult();
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      foreach (var run in runs)
        await run.Session.WaitForSettlementAsync(run.RunId).WaitAsync(TestTimeout);
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }

    async Task<Guid> StartAsync(PrimarySession session, string text) {
      InputReceipt receipt = await session.SubmitUserInputAsync(new(Guid.NewGuid(), 0, text));
      Guid runId = Assert.IsType<Guid>(receipt.RunId);
      runs.Add((session, runId));
      Assert.Equal(InputDecision.Accepted, receipt.Decision);
      return runId;
    }
  }

  [Fact]
  public async Task ReopenedUnownedRunIsUnavailableWithoutMutationOrReplay() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid();
    Guid runId = Guid.NewGuid();
    var command = new InputCommand(Guid.NewGuid(), 0, "saved orphan");
    var pending = new InputCommand(Guid.NewGuid(), 0, "unclaimed steering");
    InputReceipt accepted;
    InputReceipt queued;
    Guid requestId;
    using (SessionStore original = SessionStore.Open(database.Path)) {
      original.Sessions.CreatePrimary(sessionId);
      accepted = original.Inputs.Admit(sessionId, 0, command, true, runId).Receipt;
      requestId = Assert.IsType<LlmRequest>(original.Runs.ClaimNextRequest(sessionId, runId, 0)).RequestId;
      queued = original.Inputs.Admit(sessionId, 0, pending, true, Guid.NewGuid(), runId).Receipt;
    }

    using var client = new ScriptedChatClient(_ => throw new InvalidOperationException("Stop must not replay orphan work."));
    using SessionStore store = SessionStore.Open(database.Path);
    var manager = new SessionManager(store, client);
    try {
      PrimarySession session = await manager.GetPrimaryAsync(sessionId);
      for (int retry = 0; retry < 2; retry++)
        Assert.Equal(new StopReceipt(runId, StopStatus.Unavailable),
            await session.RequestStopAsync(runId).WaitAsync(TestTimeout));
      StoredRun saved = Assert.Single(StoredRuns.Read(database));
      Assert.False(saved.Stopping);
      Assert.Null(saved.Outcome);
      StoredRequest request = Assert.Single(StoredRuns.ReadRequests(database));
      Assert.Equal(requestId, request.RequestId);
      Assert.Null(request.Outcome);
      SessionSnapshot snapshot = await session.ReadSnapshotAsync();
      Assert.True(snapshot.AdmissionFenced);
      Assert.False(snapshot.StopRequested);
      Assert.Equal(runId, snapshot.UnsettledRunId);
      Assert.Null(snapshot.LastSettlement);
      Assert.Equal(accepted, await session.SubmitUserInputAsync(command));
      Assert.Equal(queued, await session.SubmitUserInputAsync(pending));
      Assert.Null(Assert.Single(snapshot.Messages, message => message.Message.MessageId == pending.InputId).Settlement);
      Assert.Equal(0, client.Calls);
    } finally {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }
}
