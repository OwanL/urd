using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

// Concurrent session access: cancelled lock waiters, shared exact retries and isolated waits.
public sealed class SessionConcurrencyTests {
  [Fact]
  public async Task CancelledLockWaiterDoesNotReleaseAnotherOwnersLock() {
    var guard = new AsyncLock();
    var entered = Signal();
    async Task EnterAsync() {
      using var held = await guard.AcquireAsync();
      entered.TrySetResult();
    }

    Task waiting;
    using (await guard.AcquireAsync()) {
      waiting = EnterAsync();
      using var cancellation = new CancellationTokenSource();
      ValueTask<AsyncLock.Handle> cancelledAcquisition = guard.AcquireAsync(cancellation.Token);
      Task<AsyncLock.Handle> cancelled = cancelledAcquisition.AsTask();
      await cancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TestTimeout));
      Assert.False(entered.Task.IsCompleted);
      Assert.False(waiting.IsCompleted);
    }

    await waiting.WaitAsync(TestTimeout);
    Assert.True(entered.Task.IsCompletedSuccessfully);
    using var reacquired = await guard.AcquireAsync().AsTask().WaitAsync(TestTimeout);
  }

  [Fact]
  public async Task ConcurrentExactSubmissionsShareOneDurableReceiptRunAndModelRequest() {
    var ready = Signal();
    var start = Signal();
    var modelEntered = Signal();
    var modelRelease = Signal();
    int waiting = 0;
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      modelEntered.TrySetResult();
      await modelRelease.Task.WaitAsync(token);
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    var database = new TemporaryDatabase();
    var client = new ScriptedChatClient(Response);
    var store = SessionStore.Open(database.Path);
    var manager = new SessionManager(store, client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    var command = new InputCommand(Guid.NewGuid(), 0, "concurrent exact retry");
    Task<InputReceipt>[] submissions = Enumerable.Range(0, 2).Select(_ => Task.Run(async () => {
      if (Interlocked.Increment(ref waiting) == 2)
        ready.TrySetResult();
      await start.Task;
      return await session.SubmitUserInputAsync(command);
    })).ToArray();
    try {
      await ready.Task.WaitAsync(TestTimeout);
      start.TrySetResult();
      InputReceipt[] receipts = await Task.WhenAll(submissions).WaitAsync(TestTimeout);
      await modelEntered.Task.WaitAsync(TestTimeout);

      Assert.Equal(InputDecision.Accepted, receipts[0].Decision);
      Guid runId = Assert.IsType<Guid>(receipts[0].RunId);
      Assert.Equal(receipts[0], receipts[1]);
      Assert.Equal(receipts[0], await session.GetInputReceiptAsync(command.InputId));
      Assert.Single(StoredInputs.Read(database));
      Assert.Single(StoredRuns.Read(database));
      Assert.Single(StoredRuns.ReadRequests(database));
      Assert.Equal(1, client.Calls);
      Assert.False(session.WaitForSettlementAsync(runId).IsCompleted);

      modelRelease.TrySetResult();
      ExecutionResult[] settlements = await Task.WhenAll(receipts.Select(receipt =>
          session.WaitForSettlementAsync(receipt.RunId!.Value))).WaitAsync(TestTimeout);
      Assert.Equal(settlements[0], settlements[1]);
      Assert.Equal(ExecutionOutcome.Completed, settlements[0].Outcome);
      Assert.Equal(1, client.Calls);
      Assert.Single(StoredRuns.ReadRequests(database));
    } finally {
      start.TrySetResult();
      modelRelease.TrySetResult();
      // Join all callers before discovering runs, then drain owned work before disposal.
      InputReceipt[] receipts = await Task.WhenAll(submissions).WaitAsync(TestTimeout);
      foreach (Guid runId in receipts.Where(receipt => receipt.RunId.HasValue)
                   .Select(receipt => receipt.RunId!.Value).Distinct()) {
        await session.RequestStopAsync(runId);
        await session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout);
      }
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Fact]
  public async Task CancellingSettlementWaitDoesNotCancelOwnedModelWork() {
    var modelEntered = Signal();
    var modelRelease = Signal();
    CancellationToken modelToken = default;
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      modelToken = token;
      modelEntered.TrySetResult();
      await modelRelease.Task.WaitAsync(token);
      yield return new() { Role = ChatRole.Assistant, Contents = [new TextContent("completed after release")] };
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    var database = new TemporaryDatabase();
    var client = new ScriptedChatClient(Response);
    var store = SessionStore.Open(database.Path);
    var manager = new SessionManager(store, client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    Guid? ownedRun = null;
    using var waitCancellation = new CancellationTokenSource();
    try {
      InputReceipt receipt = await session.SubmitUserInputAsync(new InputCommand(Guid.NewGuid(), 0, "go"));
      ownedRun = receipt.RunId;
      Assert.Equal(InputDecision.Accepted, receipt.Decision);
      Guid runId = Assert.IsType<Guid>(ownedRun);
      await modelEntered.Task.WaitAsync(TestTimeout);

      Task<ExecutionResult> cancelledWait = session.WaitForSettlementAsync(runId, waitCancellation.Token);
      Assert.False(cancelledWait.IsCompleted);
      await waitCancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait.WaitAsync(TestTimeout));
      Assert.False(modelToken.IsCancellationRequested);
      Assert.False(session.WaitForSettlementAsync(runId).IsCompleted);
      StoredRun unsettled = Assert.Single(StoredRuns.Read(database));
      Assert.False(unsettled.Stopping);
      Assert.Null(unsettled.Outcome);
      Assert.Null(Assert.Single(StoredRuns.ReadRequests(database)).Outcome);

      modelRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredRuns.Read(database)).Outcome);
      Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredRuns.ReadRequests(database)).Outcome);
      Assert.Equal("completed after release", StoredOutput.ReadText(database));
      Assert.Equal(1, client.Calls);
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      modelRelease.TrySetResult();
      if (ownedRun is { } runId) {
        await session.RequestStopAsync(runId);
        await session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout);
      }
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }
}
