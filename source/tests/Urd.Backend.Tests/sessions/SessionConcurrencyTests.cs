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
  public async Task ConcurrentExactSubmissionsShareOneDurableReceiptExecutionAndLlmRequest() {
    var ready = Signal();
    var start = Signal();
    var llmEntered = Signal();
    var llmRelease = Signal();
    int waiting = 0;
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      llmEntered.TrySetResult();
      await llmRelease.Task.WaitAsync(token);
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    var database = new TemporaryDatabase();
    var client = new ScriptedChatClient(Response);
    var store = UrdStore.Open(database.Path);
    var manager = new SessionManager(store, client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "concurrent exact retry");
    Task<UserMessageReceipt>[] submissions = Enumerable.Range(0, 2).Select(_ => Task.Run(async () => {
      if (Interlocked.Increment(ref waiting) == 2)
        ready.TrySetResult();
      await start.Task;
      return await session.SubmitUserMessageAsync(submission);
    })).ToArray();
    try {
      await ready.Task.WaitAsync(TestTimeout);
      start.TrySetResult();
      UserMessageReceipt[] receipts = await Task.WhenAll(submissions).WaitAsync(TestTimeout);
      await llmEntered.Task.WaitAsync(TestTimeout);

      Assert.Equal(MessageAcceptance.Accepted, receipts[0].Acceptance);
      Guid executionId = Assert.IsType<Guid>(receipts[0].ExecutionId);
      Assert.Equal(receipts[0], receipts[1]);
      Assert.Equal(receipts[0], await session.GetUserMessageReceiptAsync(submission.SubmissionId));
      Assert.Single(StoredSubmissions.Read(database));
      Assert.Single(StoredExecutions.Read(database));
      Assert.Single(StoredExecutions.ReadRequests(database));
      Assert.Equal(1, client.Calls);
      Assert.False(session.WaitForSettlementAsync(executionId).IsCompleted);

      llmRelease.TrySetResult();
      SessionExecutionResult[] settlements = await Task.WhenAll(receipts.Select(receipt =>
          session.WaitForSettlementAsync(receipt.ExecutionId!.Value))).WaitAsync(TestTimeout);
      Assert.Equal(settlements[0], settlements[1]);
      Assert.Equal(ExecutionOutcome.Completed, settlements[0].Outcome);
      Assert.Equal(1, client.Calls);
      Assert.Single(StoredExecutions.ReadRequests(database));
    } finally {
      start.TrySetResult();
      llmRelease.TrySetResult();
      // Join all callers before discovering runs, then drain owned work before disposal.
      UserMessageReceipt[] receipts = await Task.WhenAll(submissions).WaitAsync(TestTimeout);
      foreach (Guid executionId in receipts.Where(receipt => receipt.ExecutionId.HasValue)
                   .Select(receipt => receipt.ExecutionId!.Value).Distinct()) {
        await session.RequestStopAsync(executionId);
        await session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout);
      }
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Fact]
  public async Task CancellingSettlementWaitDoesNotCancelOwnedLlmWork() {
    var llmEntered = Signal();
    var llmRelease = Signal();
    CancellationToken llmToken = default;
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      llmToken = token;
      llmEntered.TrySetResult();
      await llmRelease.Task.WaitAsync(token);
      yield return new() { Role = ChatRole.Assistant, Contents = [new TextContent("completed after release")] };
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    var database = new TemporaryDatabase();
    var client = new ScriptedChatClient(Response);
    var store = UrdStore.Open(database.Path);
    var manager = new SessionManager(store, client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    Guid? ownedExecution = null;
    using var waitCancellation = new CancellationTokenSource();
    try {
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new UserMessageSubmission(Guid.NewGuid(), 0, "go"));
      ownedExecution = receipt.ExecutionId;
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      Guid executionId = Assert.IsType<Guid>(ownedExecution);
      await llmEntered.Task.WaitAsync(TestTimeout);

      Task<SessionExecutionResult> cancelledWait = session.WaitForSettlementAsync(executionId, waitCancellation.Token);
      Assert.False(cancelledWait.IsCompleted);
      await waitCancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait.WaitAsync(TestTimeout));
      Assert.False(llmToken.IsCancellationRequested);
      Assert.False(session.WaitForSettlementAsync(executionId).IsCompleted);
      StoredExecution unsettled = Assert.Single(StoredExecutions.Read(database));
      Assert.False(unsettled.Stopping);
      Assert.Null(unsettled.Outcome);
      Assert.Null(Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);

      llmRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredExecutions.Read(database)).Outcome);
      Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
      Assert.Equal("completed after release", StoredLlmResponses.ReadText(database));
      Assert.Equal(1, client.Calls);
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      llmRelease.TrySetResult();
      if (ownedExecution is { } executionId) {
        await session.RequestStopAsync(executionId);
        await session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout);
      }
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }
}
