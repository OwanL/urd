using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class SessionConcurrencyTests {
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
    var manager = new SessionManager(store, _ => client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "concurrent exact retry", TestLlmSelection.Default);
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
      // Join callers before draining the run and disposing borrowed resources.
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
}
