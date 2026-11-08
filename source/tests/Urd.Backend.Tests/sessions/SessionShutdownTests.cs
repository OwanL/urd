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
          var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "callback reentry", TestLlmSelection.Default);
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
      manager = new SessionManager(store, _ => client);
      sessions = [await manager.CreatePrimaryAsync(), await manager.CreatePrimaryAsync()];
      for (int index = 0; index < sessions.Length; index++) {
        UserMessageReceipt receipt = await sessions[index].SubmitUserMessageAsync(
            new UserMessageSubmission(Guid.NewGuid(), 0, $"active {index}", TestLlmSelection.Default));
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
    var manager = new SessionManager(store, _ => client);
    var ownedExecutions = new List<(PrimarySession Session, Guid ExecutionId)>();
    try {
      PrimarySession[] sessions = [await manager.CreatePrimaryAsync(), await manager.CreatePrimaryAsync()];
      for (int index = 0; index < sessions.Length; index++) {
        UserMessageReceipt receipt = await sessions[index].SubmitUserMessageAsync(
            new UserMessageSubmission(Guid.NewGuid(), 0, $"active {index}", TestLlmSelection.Default));
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
