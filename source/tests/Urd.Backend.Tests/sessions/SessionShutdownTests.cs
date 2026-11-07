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

// Manager shutdown fences admission, joins owned work and preserves failures and borrowed resources.
public sealed class SessionShutdownTests {
  [Fact]
  public async Task EmptyManagerShutdownIsRepeatableAndPreventsNewSessions() {
    using var database = new TemporaryDatabase();
    using var client = new ScriptedChatClient(Complete);
    using SessionStore store = SessionStore.Open(database.Path);
    var manager = new SessionManager(store, client);

    await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreatePrimaryAsync().WaitAsync(TestTimeout));
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => manager.GetPrimaryAsync(Guid.NewGuid()).WaitAsync(TestTimeout));
    Assert.Empty(StoredSessions.Read(database));
    Assert.Equal(0, client.Calls);
    Assert.Equal(0, client.DisposeCalls);
  }

  [Fact]
  public async Task ShutdownFencesRegistryAndHeldHandlesButRetainsSavedReceiptsAndOwnership() {
    var database = new TemporaryDatabase();
    SessionStore store = SessionStore.Open(database.Path);
    var client = new ScriptedChatClient(Complete);
    var manager = new SessionManager(store, client);
    var ownedRuns = new List<(PrimarySession Session, Guid RunId)>();
    bool shutdownComplete = false;
    try {
      PrimarySession active = await manager.CreatePrimaryAsync();
      PrimarySession idle = await manager.CreatePrimaryAsync();
      var command = new InputCommand(Guid.NewGuid(), 0, "complete before shutdown");
      InputReceipt accepted = await active.SubmitUserInputAsync(command);
      Guid runId = Assert.IsType<Guid>(accepted.RunId);
      ownedRuns.Add((active, runId));
      Assert.Equal(ExecutionOutcome.Completed,
          (await active.WaitForSettlementAsync(runId).WaitAsync(TestTimeout)).Outcome);

      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      shutdownComplete = true;
      await Assert.ThrowsAsync<InvalidOperationException>(
          () => manager.CreatePrimaryAsync().WaitAsync(TestTimeout));
      await Assert.ThrowsAsync<InvalidOperationException>(
          () => manager.GetPrimaryAsync(active.Id).WaitAsync(TestTimeout));

      Assert.Equal(accepted, await active.SubmitUserInputAsync(command));
      Assert.Equal(accepted, await active.GetInputReceiptAsync(command.InputId));
      var refusedCommand = new InputCommand(Guid.NewGuid(), 0, "new input after shutdown");
      InputReceipt refused = await active.SubmitUserInputAsync(refusedCommand);
      Assert.Equal(InputDecision.Refused, refused.Decision);
      Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);
      Assert.Null(refused.RunId);
      var idleCommand = new InputCommand(Guid.NewGuid(), 0, "idle handle after shutdown");
      InputReceipt idleRefusal = await idle.SubmitUserInputAsync(idleCommand);
      Assert.Equal(InputDecision.Refused, idleRefusal.Decision);
      Assert.Equal(InputRefusalReason.Stopping, idleRefusal.RefusalReason);

      SessionSnapshot snapshot = await active.ReadSnapshotAsync();
      Assert.True(snapshot.AdmissionFenced);
      Assert.Equal(new ExecutionResult(runId, ExecutionOutcome.Completed), snapshot.LastSettlement);
      Assert.Equal("done", StoredOutput.ReadText(database));
      Assert.Equal(1, client.Calls);
      Assert.Equal(0, client.DisposeCalls);

      store.Dispose();
      store = SessionStore.Open(database.Path);
      var reopenedManager = new SessionManager(store, client);
      PrimarySession reopened = await reopenedManager.GetPrimaryAsync(active.Id);
      SessionSnapshot reopenedSnapshot = await reopened.ReadSnapshotAsync();
      Assert.False(reopenedSnapshot.AdmissionFenced);
      Assert.Equal(snapshot.LastSettlement, reopenedSnapshot.LastSettlement);
      Assert.Contains(reopenedSnapshot.Messages, message => message.Preview == command.Text);
      Assert.Contains(reopenedSnapshot.Messages, message => message.Preview == "done");
      Assert.Equal(accepted, await reopened.GetInputReceiptAsync(command.InputId));
      Assert.Equal(refused, await reopened.GetInputReceiptAsync(refusedCommand.InputId));
      Assert.Equal(idleRefusal, await (await reopenedManager.GetPrimaryAsync(idle.Id))
          .GetInputReceiptAsync(idleCommand.InputId));
      Assert.Equal(accepted, await reopened.SubmitUserInputAsync(command));
      Assert.Equal(1, client.Calls);
      Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredRuns.Read(database)).Outcome);
      Assert.Equal(2, StoredSessions.Read(database).Count);
    } finally {
      if (!shutdownComplete)
        await JoinOwnedRunsAsync(manager, ownedRuns.ToArray());
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }

    Assert.Equal(1, client.DisposeCalls);
  }

  [Fact]
  public async Task ShutdownFencesBothHandlesBeforeCancellingAndJoinsReentrantCallbacksAndWaiters() {
    var database = new TemporaryDatabase();
    SessionStore store = SessionStore.Open(database.Path);
    PrimarySession[] sessions = [];
    SessionManager manager = null!;
    var entered = new[] { Signal(), Signal() };
    var cleanupEntered = new[] { Signal(), Signal() };
    var cleanupFinished = new[] { Signal(), Signal() };
    var cleanupRelease = Signal();
    var callbackRelease = Signal();
    var modelTokens = new CancellationToken[2];
    var observations = Enumerable.Range(0, 2)
        .Select(_ => new TaskCompletionSource<CallbackObservation>(TaskCreationOptions.RunContinuationsAsynchronously))
        .ToArray();
    int request = 0;
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      int index = Interlocked.Increment(ref request) - 1;
      modelTokens[index] = token;
      using CancellationTokenRegistration registration = token.Register(() => {
        try {
          PrimarySession other = sessions[1 - index];
          SessionSnapshot snapshot = other.ReadSnapshotAsync().WaitAsync(TestTimeout).GetAwaiter().GetResult();
          var command = new InputCommand(Guid.NewGuid(), 0, "callback reentry");
          InputReceipt refusal = other.SubmitUserInputAsync(command).WaitAsync(TestTimeout).GetAwaiter().GetResult();
          Exception? getFailure = null;
          try {
            manager.GetPrimaryAsync(other.Id).WaitAsync(TestTimeout).GetAwaiter().GetResult();
          } catch (Exception failure) {
            getFailure = failure;
          }

          observations[index].TrySetResult(new(snapshot, refusal, getFailure));
          callbackRelease.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
        } catch (Exception failure) {
          observations[index].TrySetException(failure);
          throw;
        }
      });

      try {
        yield return Text($"run {index}");
        entered[index].TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
      } finally {
        cleanupEntered[index].TrySetResult();
        await cleanupRelease.Task;
        cleanupFinished[index].TrySetResult();
      }
    }

    var client = new ScriptedChatClient(token => Response(token));
    var ownedRuns = new List<(PrimarySession Session, Guid RunId)>();
    try {
      manager = new SessionManager(store, client);
      sessions = [await manager.CreatePrimaryAsync(), await manager.CreatePrimaryAsync()];
      for (int index = 0; index < sessions.Length; index++) {
        InputReceipt receipt = await sessions[index].SubmitUserInputAsync(
            new InputCommand(Guid.NewGuid(), 0, $"active {index}"));
        ownedRuns.Add((sessions[index], Assert.IsType<Guid>(receipt.RunId)));
        await entered[index].Task.WaitAsync(TestTimeout);
      }

      using var waitCancellation = new CancellationTokenSource();
      Task firstWait = manager.ShutdownSessionsAsync(waitCancellation.Token);
      await Task.WhenAll(cleanupEntered.Select(signal => signal.Task)).WaitAsync(TestTimeout);
      CallbackObservation[] callbackResults = await Task.WhenAll(observations.Select(observation =>
          observation.Task)).WaitAsync(TestTimeout);
      Assert.All(callbackResults, result => {
        Assert.True(result.Snapshot.AdmissionFenced);
        Assert.Equal(InputDecision.Refused, result.Refusal.Decision);
        Assert.Equal(InputRefusalReason.Stopping, result.Refusal.RefusalReason);
        Assert.IsType<InvalidOperationException>(result.ManagerGetFailure);
      });
      Assert.All(modelTokens, token => Assert.True(token.IsCancellationRequested));
      SessionSnapshot[] snapshots = await Task.WhenAll(sessions.Select(session =>
          session.ReadSnapshotAsync().WaitAsync(TestTimeout))).WaitAsync(TestTimeout);
      Assert.All(snapshots, snapshot => Assert.True(snapshot.AdmissionFenced));
      for (int index = 0; index < callbackResults.Length; index++)
        Assert.Equal(callbackResults[index].Refusal,
            await sessions[1 - index].GetInputReceiptAsync(callbackResults[index].Refusal.Command.InputId)
                .WaitAsync(TestTimeout));
      Assert.False(firstWait.IsCompleted);

      await waitCancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstWait.WaitAsync(TestTimeout));
      Task secondWait = manager.ShutdownSessionsAsync();
      Assert.False(secondWait.IsCompleted);
      cleanupRelease.TrySetResult();
      await Task.WhenAll(cleanupFinished.Select(signal => signal.Task)).WaitAsync(TestTimeout);
      Assert.False(secondWait.IsCompleted);
      callbackRelease.TrySetResult();
      await secondWait.WaitAsync(TestTimeout);
      ExecutionResult[] settlements = await Task.WhenAll(ownedRuns.Select(run =>
          run.Session.WaitForSettlementAsync(run.RunId).WaitAsync(TestTimeout))).WaitAsync(TestTimeout);
      Assert.All(settlements, settlement => Assert.Equal(ExecutionOutcome.Cancelled, settlement.Outcome));
      Assert.Equal(2, client.Calls);
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      cleanupRelease.TrySetResult();
      callbackRelease.TrySetResult();
      if (manager is not null)
        await JoinOwnedRunsAsync(manager, ownedRuns.ToArray());
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Fact]
  public async Task InitiallyCancelledWaitStillInitiatesShutdown() {
    var database = new TemporaryDatabase();
    SessionStore store = SessionStore.Open(database.Path);
    var entered = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    var modelTokens = new CancellationToken[1];
    var client = new ScriptedChatClient(token => CancellationGatedResponse(
        0, entered, cleanupEntered, cleanupRelease.Task, modelTokens, token));
    var manager = new SessionManager(store, client);
    PrimarySession? session = null;
    Guid? runId = null;
    try {
      session = await manager.CreatePrimaryAsync();
      InputReceipt receipt = await session.SubmitUserInputAsync(new(Guid.NewGuid(), 0, "active"));
      runId = Assert.IsType<Guid>(receipt.RunId);
      await entered.Task.WaitAsync(TestTimeout);

      using var alreadyCancelled = new CancellationTokenSource();
      await alreadyCancelled.CancelAsync();
      Task cancelledWait = manager.ShutdownSessionsAsync(alreadyCancelled.Token);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait.WaitAsync(TestTimeout));
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.True(modelTokens[0].IsCancellationRequested);
      Task repeatedWait = manager.ShutdownSessionsAsync();
      Assert.False(repeatedWait.IsCompleted);

      cleanupRelease.TrySetResult();
      await repeatedWait.WaitAsync(TestTimeout);
      Assert.Equal(ExecutionOutcome.Cancelled,
          (await session.WaitForSettlementAsync(runId.Value).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      cleanupRelease.TrySetResult();
      if (runId is { } ownedRun)
        await JoinOwnedRunsAsync(manager, (session!, ownedRun));
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task StopPersistenceFailuresStillCancelAndDrainEverySessionAndRemainSticky(bool failEveryStop) {
    var database = new TemporaryDatabase();
    SessionStore store = SessionStore.Open(database.Path);
    var entered = new[] { Signal(), Signal() };
    var cleanupEntered = new[] { Signal(), Signal() };
    var cleanupRelease = new[] { Signal(), Signal() };
    var modelTokens = new CancellationToken[2];
    int request = 0;
    var client = new ScriptedChatClient(token => {
      int index = Interlocked.Increment(ref request) - 1;
      return CancellationGatedResponse(index, entered[index], cleanupEntered[index], cleanupRelease[index].Task,
          modelTokens, token);
    });
    var manager = new SessionManager(store, client);
    var ownedRuns = new List<(PrimarySession Session, Guid RunId)>();
    try {
      PrimarySession[] sessions = [await manager.CreatePrimaryAsync(), await manager.CreatePrimaryAsync()];
      for (int index = 0; index < sessions.Length; index++) {
        InputReceipt receipt = await sessions[index].SubmitUserInputAsync(
            new InputCommand(Guid.NewGuid(), 0, $"active {index}"));
        ownedRuns.Add((sessions[index], Assert.IsType<Guid>(receipt.RunId)));
        await entered[index].Task.WaitAsync(TestTimeout);
      }

      Guid rejectedStopRun = ownedRuns[0].RunId;
      database.Execute($"""
        CREATE TRIGGER reject_stops BEFORE UPDATE OF stopping ON execution_runs
        WHEN NEW.stopping = 1
        BEGIN
          SELECT CASE
            WHEN NEW.run_id = '{rejectedStopRun:D}' THEN RAISE(ABORT, 'Injected first Stop failure')
            WHEN {(failEveryStop ? 1 : 0)} = 1 THEN RAISE(ABORT, 'Injected second Stop failure')
          END;
        END;
        """);

      Task shutdown = manager.ShutdownSessionsAsync();
      await Task.WhenAll(cleanupEntered.Select(signal => signal.Task)).WaitAsync(TestTimeout);
      Assert.All(modelTokens, token => Assert.True(token.IsCancellationRequested));
      Assert.False(shutdown.IsCompleted);
      Assert.False(Assert.Single(StoredRuns.Read(database), run => run.RunId == rejectedStopRun).Stopping);
      Assert.Equal(!failEveryStop,
          Assert.Single(StoredRuns.Read(database), run => run.RunId == ownedRuns[1].RunId).Stopping);

      cleanupRelease[1].TrySetResult();
      await sessions[1].WaitForSettlementAsync(ownedRuns[1].RunId).WaitAsync(TestTimeout);
      Assert.False(shutdown.IsCompleted);

      database.Execute("DROP TRIGGER reject_stops;");
      cleanupRelease[0].TrySetResult();
      Exception failure = await ExpectFailureAsync(shutdown);
      Exception repeatedFailure = await ExpectFailureAsync(manager.ShutdownSessionsAsync());
      foreach (Exception observed in new[] { failure, repeatedFailure }) {
        SqliteException[] failures = FlattenFailures(observed).OfType<SqliteException>().ToArray();
        Assert.Equal(failEveryStop ? 2 : 1, failures.Length);
        Assert.All(failures, error => Assert.Equal(19, error.SqliteErrorCode));
        Assert.Contains(failures, error => error.Message.Contains("Injected first Stop failure", StringComparison.Ordinal));
        if (failEveryStop)
          Assert.Contains(failures, error => error.Message.Contains("Injected second Stop failure", StringComparison.Ordinal));
      }
      ExecutionResult[] settlements = await Task.WhenAll(ownedRuns.Select(run =>
          run.Session.WaitForSettlementAsync(run.RunId).WaitAsync(TestTimeout))).WaitAsync(TestTimeout);
      Assert.All(settlements, settlement => Assert.Equal(ExecutionOutcome.Cancelled, settlement.Outcome));
      Assert.All(StoredRuns.Read(database), run => Assert.Equal(ExecutionOutcome.Cancelled, run.Outcome));
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      foreach (TaskCompletionSource release in cleanupRelease)
        release.TrySetResult();
      database.Execute("DROP TRIGGER IF EXISTS reject_stops;");
      await JoinOwnedRunsAsync(manager, ownedRuns.ToArray());
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Fact]
  public async Task FaultedSettlementKeepsShutdownFailedAfterOtherRunDrains() {
    var database = new TemporaryDatabase();
    SessionStore store = SessionStore.Open(database.Path);
    var healthyEntered = Signal();
    var healthyCleanupEntered = Signal();
    var healthyCleanupRelease = Signal();
    var modelTokens = new CancellationToken[2];
    int request = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref request) == 1
        ? Complete(token)
        : CancellationGatedResponse(1, healthyEntered, healthyCleanupEntered,
            healthyCleanupRelease.Task, modelTokens, token));
    var manager = new SessionManager(store, client);
    PrimarySession faultedSession = null!;
    PrimarySession healthySession = null!;
    Guid? faultedRun = null;
    Guid? healthyRun = null;
    try {
      faultedSession = await manager.CreatePrimaryAsync();
      healthySession = await manager.CreatePrimaryAsync();
      database.Execute("""
        CREATE TRIGGER reject_settlement BEFORE UPDATE OF outcome ON execution_runs
        WHEN NEW.outcome IS NOT NULL
        BEGIN SELECT RAISE(ABORT, 'Injected run settlement failure'); END;
        """);
      InputReceipt failedReceipt = await faultedSession.SubmitUserInputAsync(
          new InputCommand(Guid.NewGuid(), 0, "fault completion"));
      faultedRun = Assert.IsType<Guid>(failedReceipt.RunId);
      Exception runFailure = await ExpectFailureAsync(faultedSession.WaitForSettlementAsync(faultedRun.Value));
      Assert.Contains(FlattenFailures(runFailure), error => error is SqliteException { SqliteErrorCode: 19 });
      Assert.Null(Assert.Single(StoredRuns.Read(database), run => run.RunId == faultedRun.Value).Outcome);
      Assert.Equal(ExecutionOutcome.Completed,
          Assert.Single(StoredRuns.ReadRequests(database), run => run.RunId == faultedRun.Value).Outcome);

      database.Execute("DROP TRIGGER reject_settlement;");
      InputReceipt healthyReceipt = await healthySession.SubmitUserInputAsync(
          new InputCommand(Guid.NewGuid(), 0, "drain healthy run"));
      healthyRun = Assert.IsType<Guid>(healthyReceipt.RunId);
      await healthyEntered.Task.WaitAsync(TestTimeout);

      Task shutdown = manager.ShutdownSessionsAsync();
      await healthyCleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.True(modelTokens[1].IsCancellationRequested);
      Assert.False(shutdown.IsCompleted);
      healthyCleanupRelease.TrySetResult();
      Exception shutdownFailure = await ExpectFailureAsync(shutdown);
      Assert.Contains(FlattenFailures(shutdownFailure), error => error is SqliteException { SqliteErrorCode: 19 });
      Exception repeatedFailure = await ExpectFailureAsync(manager.ShutdownSessionsAsync());
      Assert.Contains(FlattenFailures(repeatedFailure), error => error is SqliteException { SqliteErrorCode: 19 });
      Assert.Equal(ExecutionOutcome.Cancelled,
          (await healthySession.WaitForSettlementAsync(healthyRun.Value).WaitAsync(TestTimeout)).Outcome);
      Assert.Null(Assert.Single(StoredRuns.Read(database), run => run.RunId == faultedRun.Value).Outcome);
      Assert.Equal(ExecutionOutcome.Cancelled,
          Assert.Single(StoredRuns.Read(database), run => run.RunId == healthyRun.Value).Outcome);
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      healthyCleanupRelease.TrySetResult();
      database.Execute("DROP TRIGGER IF EXISTS reject_settlement;");
      var runs = new List<(PrimarySession Session, Guid RunId)>();
      if (faultedRun is { } failed)
        runs.Add((faultedSession, failed));
      if (healthyRun is { } healthy)
        runs.Add((healthySession, healthy));
      await JoinOwnedRunsAsync(manager, runs.ToArray());
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Fact]
  public async Task ShutdownDoesNotRecoverOrReplayAnOrphanedPersistedRun() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid();
    Guid runId = Guid.NewGuid();
    var command = new InputCommand(Guid.NewGuid(), 0, "retained orphan");
    InputReceipt accepted;
    Guid requestId;
    using (SessionStore original = SessionStore.Open(database.Path)) {
      original.Sessions.CreatePrimary(sessionId);
      accepted = original.Inputs.Admit(sessionId, 0, command, true, runId).Receipt;
      requestId = Assert.IsType<LlmRequest>(original.Runs.ClaimNextRequest(sessionId, runId, 0)).RequestId;
    }

    using var client = new ScriptedChatClient(_ =>
        throw new InvalidOperationException("Shutdown must not replay an orphaned run."));
    using SessionStore store = SessionStore.Open(database.Path);
    var manager = new SessionManager(store, client);
    PrimarySession session = await manager.GetPrimaryAsync(sessionId);
    await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);

    SessionSnapshot snapshot = await session.ReadSnapshotAsync();
    Assert.True(snapshot.AdmissionFenced);
    Assert.Equal(runId, snapshot.UnsettledRunId);
    Assert.Equal(accepted, await session.SubmitUserInputAsync(command));
    var newCommand = new InputCommand(Guid.NewGuid(), 0, "new orphan input");
    InputReceipt refused = await session.SubmitUserInputAsync(newCommand);
    Assert.Equal(InputDecision.Refused, refused.Decision);
    Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);
    Assert.Equal(accepted, await session.GetInputReceiptAsync(command.InputId));
    Assert.Equal(refused, await session.GetInputReceiptAsync(newCommand.InputId));
    StoredRun persistedRun = Assert.Single(StoredRuns.Read(database));
    Assert.Equal(runId, persistedRun.RunId);
    Assert.False(persistedRun.Stopping);
    Assert.Null(persistedRun.Outcome);
    StoredRequest persistedRequest = Assert.Single(StoredRuns.ReadRequests(database));
    Assert.Equal(requestId, persistedRequest.RequestId);
    Assert.Null(persistedRequest.Outcome);
    Assert.Equal(0, client.Calls);
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => manager.GetPrimaryAsync(sessionId).WaitAsync(TestTimeout));
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> CancellationGatedResponse(
      int index, TaskCompletionSource entered, TaskCompletionSource cleanupEntered, Task cleanupRelease,
      CancellationToken[] modelTokens, [EnumeratorCancellation] CancellationToken token) {
    modelTokens[index] = token;
    try {
      yield return Text($"active {index}");
      entered.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, token);
    } finally {
      cleanupEntered.TrySetResult();
      await cleanupRelease;
    }
  }

  private static async Task<Exception> ExpectFailureAsync(Task operation) =>
      await Assert.ThrowsAnyAsync<Exception>(() => operation.WaitAsync(TestTimeout));

  private static IEnumerable<Exception> FlattenFailures(Exception failure) {
    if (failure is AggregateException aggregate)
      return aggregate.Flatten().InnerExceptions.SelectMany(FlattenFailures);

    if (failure.InnerException is { } inner)
      return FlattenFailures(inner);

    return [failure];
  }

  private static async Task JoinOwnedRunsAsync(SessionManager manager,
      params (PrimarySession Session, Guid RunId)[] runs) {
    try {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    } catch (Exception failure) when (IsInjectedStoreFailure(failure)) {
      // Join each controlled run below; a failed shutdown alone is not a disposal barrier.
    }

    await Task.WhenAll(runs.Select(async run => {
      try {
        await run.Session.WaitForSettlementAsync(run.RunId).WaitAsync(TestTimeout);
      } catch (Exception failure) when (IsInjectedStoreFailure(failure)) {
        // The session joins its execution before returning the injected settlement failure.
      }
    })).WaitAsync(TestTimeout);
  }

  private static bool IsInjectedStoreFailure(Exception failure) => FlattenFailures(failure)
      .Any(error => error is SqliteException { SqliteErrorCode: 19 } &&
          error.Message.Contains("Injected", StringComparison.Ordinal));

  private sealed record CallbackObservation(
      SessionSnapshot Snapshot, InputReceipt Refusal, Exception? ManagerGetFailure);
}
