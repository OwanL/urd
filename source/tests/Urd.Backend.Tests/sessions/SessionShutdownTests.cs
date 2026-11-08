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
    using UrdStore store = UrdStore.Open(database.Path);
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
    UrdStore store = UrdStore.Open(database.Path);
    var client = new ScriptedChatClient(Complete);
    var manager = new SessionManager(store, client);
    var ownedExecutions = new List<(PrimarySession Session, Guid ExecutionId)>();
    bool shutdownComplete = false;
    try {
      PrimarySession active = await manager.CreatePrimaryAsync();
      PrimarySession idle = await manager.CreatePrimaryAsync();
      var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "complete before shutdown");
      UserMessageReceipt accepted = await active.SubmitUserMessageAsync(submission);
      Guid executionId = Assert.IsType<Guid>(accepted.ExecutionId);
      ownedExecutions.Add((active, executionId));
      Assert.Equal(ExecutionOutcome.Completed,
          (await active.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);

      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      shutdownComplete = true;
      await Assert.ThrowsAsync<InvalidOperationException>(
          () => manager.CreatePrimaryAsync().WaitAsync(TestTimeout));
      await Assert.ThrowsAsync<InvalidOperationException>(
          () => manager.GetPrimaryAsync(active.Id).WaitAsync(TestTimeout));

      Assert.Equal(accepted, await active.SubmitUserMessageAsync(submission));
      Assert.Equal(accepted, await active.GetUserMessageReceiptAsync(submission.SubmissionId));
      var refusedSubmission = new UserMessageSubmission(Guid.NewGuid(), 0, "new input after shutdown");
      UserMessageReceipt refused = await active.SubmitUserMessageAsync(refusedSubmission);
      Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
      Assert.Equal(MessageRefusalReason.Stopping, refused.RefusalReason);
      Assert.Null(refused.ExecutionId);
      var idleSubmission = new UserMessageSubmission(Guid.NewGuid(), 0, "idle handle after shutdown");
      UserMessageReceipt idleRefusal = await idle.SubmitUserMessageAsync(idleSubmission);
      Assert.Equal(MessageAcceptance.Refused, idleRefusal.Acceptance);
      Assert.Equal(MessageRefusalReason.Stopping, idleRefusal.RefusalReason);

      SessionSnapshot snapshot = await active.ReadSnapshotAsync();
      Assert.True(snapshot.AdmissionFenced);
      Assert.Equal(new SessionExecutionResult(executionId, ExecutionOutcome.Completed), snapshot.LastSettlement);
      Assert.Equal("done", StoredLlmResponses.ReadText(database));
      Assert.Equal(1, client.Calls);
      Assert.Equal(0, client.DisposeCalls);

      store.Dispose();
      store = UrdStore.Open(database.Path);
      var reopenedManager = new SessionManager(store, client);
      PrimarySession reopened = await reopenedManager.GetPrimaryAsync(active.Id);
      SessionSnapshot reopenedSnapshot = await reopened.ReadSnapshotAsync();
      Assert.False(reopenedSnapshot.AdmissionFenced);
      Assert.Equal(snapshot.LastSettlement, reopenedSnapshot.LastSettlement);
      Assert.Contains(reopenedSnapshot.Messages, message => message.Preview == submission.Text);
      Assert.Contains(reopenedSnapshot.Messages, message => message.Preview == "done");
      Assert.Equal(accepted, await reopened.GetUserMessageReceiptAsync(submission.SubmissionId));
      Assert.Equal(refused, await reopened.GetUserMessageReceiptAsync(refusedSubmission.SubmissionId));
      Assert.Equal(idleRefusal, await (await reopenedManager.GetPrimaryAsync(idle.Id))
          .GetUserMessageReceiptAsync(idleSubmission.SubmissionId));
      Assert.Equal(accepted, await reopened.SubmitUserMessageAsync(submission));
      Assert.Equal(1, client.Calls);
      Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredExecutions.Read(database)).Outcome);
      Assert.Equal(2, StoredSessions.Read(database).Count);
    } finally {
      if (!shutdownComplete)
        await JoinOwnedExecutionsAsync(manager, ownedExecutions.ToArray());
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }

    Assert.Equal(1, client.DisposeCalls);
  }

  [Fact]
  public async Task ShutdownFencesBothHandlesBeforeCancellingAndJoinsReentrantCallbacksAndWaiters() {
    var database = new TemporaryDatabase();
    UrdStore store = UrdStore.Open(database.Path);
    PrimarySession[] sessions = [];
    SessionManager manager = null!;
    var entered = new[] { Signal(), Signal() };
    var cleanupEntered = new[] { Signal(), Signal() };
    var cleanupFinished = new[] { Signal(), Signal() };
    var cleanupRelease = Signal();
    var callbackRelease = Signal();
    var llmTokens = new CancellationToken[2];
    var observations = Enumerable.Range(0, 2)
        .Select(_ => new TaskCompletionSource<CallbackObservation>(TaskCreationOptions.RunContinuationsAsynchronously))
        .ToArray();
    int request = 0;
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      int index = Interlocked.Increment(ref request) - 1;
      llmTokens[index] = token;
      using CancellationTokenRegistration registration = token.Register(() => {
        try {
          PrimarySession other = sessions[1 - index];
          SessionSnapshot snapshot = other.ReadSnapshotAsync().WaitAsync(TestTimeout).GetAwaiter().GetResult();
          var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "callback reentry");
          UserMessageReceipt refusal = other.SubmitUserMessageAsync(submission).WaitAsync(TestTimeout).GetAwaiter().GetResult();
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
    var ownedExecutions = new List<(PrimarySession Session, Guid ExecutionId)>();
    try {
      manager = new SessionManager(store, client);
      sessions = [await manager.CreatePrimaryAsync(), await manager.CreatePrimaryAsync()];
      for (int index = 0; index < sessions.Length; index++) {
        UserMessageReceipt receipt = await sessions[index].SubmitUserMessageAsync(
            new UserMessageSubmission(Guid.NewGuid(), 0, $"active {index}"));
        ownedExecutions.Add((sessions[index], Assert.IsType<Guid>(receipt.ExecutionId)));
        await entered[index].Task.WaitAsync(TestTimeout);
      }

      using var waitCancellation = new CancellationTokenSource();
      Task firstWait = manager.ShutdownSessionsAsync(waitCancellation.Token);
      await Task.WhenAll(cleanupEntered.Select(signal => signal.Task)).WaitAsync(TestTimeout);
      CallbackObservation[] callbackResults = await Task.WhenAll(observations.Select(observation =>
          observation.Task)).WaitAsync(TestTimeout);
      Assert.All(callbackResults, result => {
        Assert.True(result.Snapshot.AdmissionFenced);
        Assert.Equal(MessageAcceptance.Refused, result.Refusal.Acceptance);
        Assert.Equal(MessageRefusalReason.Stopping, result.Refusal.RefusalReason);
        Assert.IsType<InvalidOperationException>(result.ManagerGetFailure);
      });
      Assert.All(llmTokens, token => Assert.True(token.IsCancellationRequested));
      SessionSnapshot[] snapshots = await Task.WhenAll(sessions.Select(session =>
          session.ReadSnapshotAsync().WaitAsync(TestTimeout))).WaitAsync(TestTimeout);
      Assert.All(snapshots, snapshot => Assert.True(snapshot.AdmissionFenced));
      for (int index = 0; index < callbackResults.Length; index++)
        Assert.Equal(callbackResults[index].Refusal,
            await sessions[1 - index].GetUserMessageReceiptAsync(callbackResults[index].Refusal.Submission.SubmissionId)
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
      SessionExecutionResult[] settlements = await Task.WhenAll(ownedExecutions.Select(execution =>
          execution.Session.WaitForSettlementAsync(execution.ExecutionId).WaitAsync(TestTimeout))).WaitAsync(TestTimeout);
      Assert.All(settlements, settlement => Assert.Equal(ExecutionOutcome.Cancelled, settlement.Outcome));
      Assert.Equal(2, client.Calls);
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      cleanupRelease.TrySetResult();
      callbackRelease.TrySetResult();
      if (manager is not null)
        await JoinOwnedExecutionsAsync(manager, ownedExecutions.ToArray());
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Fact]
  public async Task InitiallyCancelledWaitStillInitiatesShutdown() {
    var database = new TemporaryDatabase();
    UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    var llmTokens = new CancellationToken[1];
    var client = new ScriptedChatClient(token => CancellationGatedResponse(
        0, entered, cleanupEntered, cleanupRelease.Task, llmTokens, token));
    var manager = new SessionManager(store, client);
    PrimarySession? session = null;
    Guid? executionId = null;
    try {
      session = await manager.CreatePrimaryAsync();
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "active"));
      executionId = Assert.IsType<Guid>(receipt.ExecutionId);
      await entered.Task.WaitAsync(TestTimeout);

      using var alreadyCancelled = new CancellationTokenSource();
      await alreadyCancelled.CancelAsync();
      Task cancelledWait = manager.ShutdownSessionsAsync(alreadyCancelled.Token);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait.WaitAsync(TestTimeout));
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.True(llmTokens[0].IsCancellationRequested);
      Task repeatedWait = manager.ShutdownSessionsAsync();
      Assert.False(repeatedWait.IsCompleted);

      cleanupRelease.TrySetResult();
      await repeatedWait.WaitAsync(TestTimeout);
      Assert.Equal(ExecutionOutcome.Cancelled,
          (await session.WaitForSettlementAsync(executionId.Value).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      cleanupRelease.TrySetResult();
      if (executionId is { } ownedExecution)
        await JoinOwnedExecutionsAsync(manager, (session!, ownedExecution));
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
    UrdStore store = UrdStore.Open(database.Path);
    var entered = new[] { Signal(), Signal() };
    var cleanupEntered = new[] { Signal(), Signal() };
    var cleanupRelease = new[] { Signal(), Signal() };
    var llmTokens = new CancellationToken[2];
    int request = 0;
    var client = new ScriptedChatClient(token => {
      int index = Interlocked.Increment(ref request) - 1;
      return CancellationGatedResponse(index, entered[index], cleanupEntered[index], cleanupRelease[index].Task,
          llmTokens, token);
    });
    var manager = new SessionManager(store, client);
    var ownedExecutions = new List<(PrimarySession Session, Guid ExecutionId)>();
    try {
      PrimarySession[] sessions = [await manager.CreatePrimaryAsync(), await manager.CreatePrimaryAsync()];
      for (int index = 0; index < sessions.Length; index++) {
        UserMessageReceipt receipt = await sessions[index].SubmitUserMessageAsync(
            new UserMessageSubmission(Guid.NewGuid(), 0, $"active {index}"));
        ownedExecutions.Add((sessions[index], Assert.IsType<Guid>(receipt.ExecutionId)));
        await entered[index].Task.WaitAsync(TestTimeout);
      }

      Guid rejectedStopExecution = ownedExecutions[0].ExecutionId;
      database.Execute($"""
        CREATE TRIGGER reject_stops BEFORE UPDATE OF stopping ON execution_runs
        WHEN NEW.stopping = 1
        BEGIN
          SELECT CASE
            WHEN NEW.run_id = '{rejectedStopExecution:D}' THEN RAISE(ABORT, 'Injected first Stop failure')
            WHEN {(failEveryStop ? 1 : 0)} = 1 THEN RAISE(ABORT, 'Injected second Stop failure')
          END;
        END;
        """);

      Task shutdown = manager.ShutdownSessionsAsync();
      await Task.WhenAll(cleanupEntered.Select(signal => signal.Task)).WaitAsync(TestTimeout);
      Assert.All(llmTokens, token => Assert.True(token.IsCancellationRequested));
      Assert.False(shutdown.IsCompleted);
      Assert.False(Assert.Single(StoredExecutions.Read(database), execution => execution.ExecutionId == rejectedStopExecution).Stopping);
      Assert.Equal(!failEveryStop,
          Assert.Single(StoredExecutions.Read(database), execution => execution.ExecutionId == ownedExecutions[1].ExecutionId).Stopping);

      cleanupRelease[1].TrySetResult();
      await sessions[1].WaitForSettlementAsync(ownedExecutions[1].ExecutionId).WaitAsync(TestTimeout);
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
      SessionExecutionResult[] settlements = await Task.WhenAll(ownedExecutions.Select(execution =>
          execution.Session.WaitForSettlementAsync(execution.ExecutionId).WaitAsync(TestTimeout))).WaitAsync(TestTimeout);
      Assert.All(settlements, settlement => Assert.Equal(ExecutionOutcome.Cancelled, settlement.Outcome));
      Assert.All(StoredExecutions.Read(database), execution => Assert.Equal(ExecutionOutcome.Cancelled, execution.Outcome));
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      foreach (TaskCompletionSource release in cleanupRelease)
        release.TrySetResult();
      database.Execute("DROP TRIGGER IF EXISTS reject_stops;");
      await JoinOwnedExecutionsAsync(manager, ownedExecutions.ToArray());
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Fact]
  public async Task FaultedSettlementKeepsShutdownFailedAfterOtherExecutionDrains() {
    var database = new TemporaryDatabase();
    UrdStore store = UrdStore.Open(database.Path);
    var healthyEntered = Signal();
    var healthyCleanupEntered = Signal();
    var healthyCleanupRelease = Signal();
    var llmTokens = new CancellationToken[2];
    int request = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref request) == 1
        ? Complete(token)
        : CancellationGatedResponse(1, healthyEntered, healthyCleanupEntered,
            healthyCleanupRelease.Task, llmTokens, token));
    var manager = new SessionManager(store, client);
    PrimarySession faultedSession = null!;
    PrimarySession healthySession = null!;
    Guid? faultedExecution = null;
    Guid? healthyExecution = null;
    try {
      faultedSession = await manager.CreatePrimaryAsync();
      healthySession = await manager.CreatePrimaryAsync();
      database.Execute("""
        CREATE TRIGGER reject_settlement BEFORE UPDATE OF outcome ON execution_runs
        WHEN NEW.outcome IS NOT NULL
        BEGIN SELECT RAISE(ABORT, 'Injected run settlement failure'); END;
        """);
      UserMessageReceipt failedReceipt = await faultedSession.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), 0, "fault completion"));
      faultedExecution = Assert.IsType<Guid>(failedReceipt.ExecutionId);
      Exception executionFailure = await ExpectFailureAsync(faultedSession.WaitForSettlementAsync(faultedExecution.Value));
      Assert.Contains(FlattenFailures(executionFailure), error => error is SqliteException { SqliteErrorCode: 19 });
      Assert.Null(Assert.Single(StoredExecutions.Read(database), execution => execution.ExecutionId == faultedExecution.Value).Outcome);
      Assert.Equal(ExecutionOutcome.Completed,
          Assert.Single(StoredExecutions.ReadRequests(database), execution => execution.ExecutionId == faultedExecution.Value).Outcome);

      database.Execute("DROP TRIGGER reject_settlement;");
      UserMessageReceipt healthyReceipt = await healthySession.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), 0, "drain healthy run"));
      healthyExecution = Assert.IsType<Guid>(healthyReceipt.ExecutionId);
      await healthyEntered.Task.WaitAsync(TestTimeout);

      Task shutdown = manager.ShutdownSessionsAsync();
      await healthyCleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.True(llmTokens[1].IsCancellationRequested);
      Assert.False(shutdown.IsCompleted);
      healthyCleanupRelease.TrySetResult();
      Exception shutdownFailure = await ExpectFailureAsync(shutdown);
      Assert.Contains(FlattenFailures(shutdownFailure), error => error is SqliteException { SqliteErrorCode: 19 });
      Exception repeatedFailure = await ExpectFailureAsync(manager.ShutdownSessionsAsync());
      Assert.Contains(FlattenFailures(repeatedFailure), error => error is SqliteException { SqliteErrorCode: 19 });
      Assert.Equal(ExecutionOutcome.Cancelled,
          (await healthySession.WaitForSettlementAsync(healthyExecution.Value).WaitAsync(TestTimeout)).Outcome);
      Assert.Null(Assert.Single(StoredExecutions.Read(database), execution => execution.ExecutionId == faultedExecution.Value).Outcome);
      Assert.Equal(ExecutionOutcome.Cancelled,
          Assert.Single(StoredExecutions.Read(database), execution => execution.ExecutionId == healthyExecution.Value).Outcome);
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      healthyCleanupRelease.TrySetResult();
      database.Execute("DROP TRIGGER IF EXISTS reject_settlement;");
      var executions = new List<(PrimarySession Session, Guid ExecutionId)>();
      if (faultedExecution is { } failed)
        executions.Add((faultedSession, failed));
      if (healthyExecution is { } healthy)
        executions.Add((healthySession, healthy));
      await JoinOwnedExecutionsAsync(manager, executions.ToArray());
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Fact]
  public async Task ShutdownDoesNotRecoverOrReplayAnOrphanedPersistedExecution() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid();
    Guid executionId = Guid.NewGuid();
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "retained orphan");
    UserMessageReceipt accepted;
    Guid requestId;
    using (UrdStore original = UrdStore.Open(database.Path)) {
      original.Sessions.CreatePrimary(sessionId);
      accepted = original.UserMessages.Admit(sessionId, 0, submission, true, executionId).Receipt;
      requestId = Assert.IsType<LlmRequest>(original.Executions.ClaimNextRequest(sessionId, executionId, 0)).RequestId;
    }

    using var client = new ScriptedChatClient(_ =>
        throw new InvalidOperationException("Shutdown must not replay an orphaned run."));
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = new SessionManager(store, client);
    PrimarySession session = await manager.GetPrimaryAsync(sessionId);
    await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);

    SessionSnapshot snapshot = await session.ReadSnapshotAsync();
    Assert.True(snapshot.AdmissionFenced);
    Assert.Equal(executionId, snapshot.UnsettledExecutionId);
    Assert.Equal(accepted, await session.SubmitUserMessageAsync(submission));
    var newSubmission = new UserMessageSubmission(Guid.NewGuid(), 0, "new orphan input");
    UserMessageReceipt refused = await session.SubmitUserMessageAsync(newSubmission);
    Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
    Assert.Equal(MessageRefusalReason.Stopping, refused.RefusalReason);
    Assert.Equal(accepted, await session.GetUserMessageReceiptAsync(submission.SubmissionId));
    Assert.Equal(refused, await session.GetUserMessageReceiptAsync(newSubmission.SubmissionId));
    StoredExecution persistedExecution = Assert.Single(StoredExecutions.Read(database));
    Assert.Equal(executionId, persistedExecution.ExecutionId);
    Assert.False(persistedExecution.Stopping);
    Assert.Null(persistedExecution.Outcome);
    StoredRequest persistedRequest = Assert.Single(StoredExecutions.ReadRequests(database));
    Assert.Equal(requestId, persistedRequest.RequestId);
    Assert.Null(persistedRequest.Outcome);
    Assert.Equal(0, client.Calls);
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => manager.GetPrimaryAsync(sessionId).WaitAsync(TestTimeout));
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> CancellationGatedResponse(
      int index, TaskCompletionSource entered, TaskCompletionSource cleanupEntered, Task cleanupRelease,
      CancellationToken[] llmTokens, [EnumeratorCancellation] CancellationToken token) {
    llmTokens[index] = token;
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

  private static async Task JoinOwnedExecutionsAsync(SessionManager manager,
      params (PrimarySession Session, Guid ExecutionId)[] executions) {
    try {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    } catch (Exception failure) when (IsInjectedStoreFailure(failure)) {
      // Join each controlled run below; a failed shutdown alone is not a disposal barrier.
    }

    await Task.WhenAll(executions.Select(async execution => {
      try {
        await execution.Session.WaitForSettlementAsync(execution.ExecutionId).WaitAsync(TestTimeout);
      } catch (Exception failure) when (IsInjectedStoreFailure(failure)) {
        // The session joins its execution before returning the injected settlement failure.
      }
    })).WaitAsync(TestTimeout);
  }

  private static bool IsInjectedStoreFailure(Exception failure) => FlattenFailures(failure)
      .Any(error => error is SqliteException { SqliteErrorCode: 19 } &&
          error.Message.Contains("Injected", StringComparison.Ordinal));

  private sealed record CallbackObservation(
      SessionSnapshot Snapshot, UserMessageReceipt Refusal, Exception? ManagerGetFailure);
}
