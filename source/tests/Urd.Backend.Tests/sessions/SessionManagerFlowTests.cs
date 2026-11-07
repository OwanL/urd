using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

// Two-session manager flow through reopen: one shared client, receipts and retained retries.
public sealed class SessionManagerFlowTests {
  [Fact]
  public async Task SharedClientStreamsAcrossCanonicalChatsAndRetainedRetriesNeverRestartWork() {
    var bothEntered = Signal();
    var release = Signal();
    int entered = 0;
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      if (Interlocked.Increment(ref entered) == 2)
        bothEntered.TrySetResult();
      await release.Task.WaitAsync(token);
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    var database = new TemporaryDatabase();
    var client = new ScriptedChatClient(Response);
    SessionStore? store = null;
    var executions = new List<(PrimarySession Session, Guid ExecutionId)>();
    try {
      store = SessionStore.Open(database.Path);
      var manager = new SessionManager(store, client);
      PrimarySession[] sessions = [await manager.CreatePrimaryAsync(), await manager.CreatePrimaryAsync()];
      Assert.NotEqual(sessions[0].Id, sessions[1].Id);
      Guid submissionId = Guid.NewGuid();
      UserMessageSubmission[] submissions = [new(submissionId, 0, "first chat"), new(submissionId, 0, "second chat")];
      var receipts = new UserMessageReceipt[2];
      for (int i = 0; i < sessions.Length; i++) {
        Assert.Same(sessions[i], await manager.GetPrimaryAsync(sessions[i].Id));
        receipts[i] = await sessions[i].SubmitUserMessageAsync(submissions[i]);
        if (receipts[i].ExecutionId is { } executionId)
          executions.Add((sessions[i], executionId));
        Assert.Equal(MessageAcceptance.Accepted, receipts[i].Acceptance);
        Assert.IsType<Guid>(receipts[i].ExecutionId);
        Assert.Equal(sessions[i].Id, receipts[i].SessionId);
        Assert.Equal(submissions[i], receipts[i].Submission);
      }

      await bothEntered.Task.WaitAsync(TestTimeout);
      Assert.All(executions, execution => Assert.False(execution.Session.WaitForSettlementAsync(execution.ExecutionId).IsCompleted));
      for (int i = 0; i < sessions.Length; i++) {
        Assert.Equal(receipts[i], await sessions[i].GetUserMessageReceiptAsync(submissionId));
        UserMessageReceipt retry = await sessions[i].SubmitUserMessageAsync(submissions[i]);
        if (retry.ExecutionId is { } executionId && executionId != receipts[i].ExecutionId)
          executions.Add((sessions[i], executionId));
        Assert.Equal(receipts[i], retry);
      }
      Assert.Equal(2, client.Calls);
      release.TrySetResult();
      SessionExecutionResult[] settlements = await Task.WhenAll(executions.Select(execution =>
          execution.Session.WaitForSettlementAsync(execution.ExecutionId))).WaitAsync(TestTimeout);
      Assert.All(settlements, settlement => Assert.Equal(ExecutionOutcome.Completed, settlement.Outcome));
      executions.Clear();
      store.Dispose();
      Assert.Equal(0, client.DisposeCalls);

      store = SessionStore.Open(database.Path);
      var reopenedManager = new SessionManager(store, client);
      for (int i = 0; i < sessions.Length; i++) {
        PrimarySession loaded = await reopenedManager.GetPrimaryAsync(sessions[i].Id);
        Assert.NotSame(sessions[i], loaded);
        Assert.Same(loaded, await reopenedManager.GetPrimaryAsync(loaded.Id));
        Assert.Equal(receipts[i], await loaded.GetUserMessageReceiptAsync(submissionId));
        UserMessageReceipt retry = await loaded.SubmitUserMessageAsync(submissions[i]);
        if (retry.ExecutionId is { } executionId)
          executions.Add((loaded, executionId));
        Assert.Equal(receipts[i], retry);
        Assert.Equal(ExecutionOutcome.Completed,
            (await loaded.WaitForSettlementAsync(retry.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
      }
      Assert.Equal(2, client.Calls);
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      release.TrySetResult();
      // A failed timed wait must not dispose borrowed resources beneath live work.
      await Task.WhenAll(executions.Select(execution => execution.Session.RequestStopAsync(execution.ExecutionId))).WaitAsync(TestTimeout);
      await Task.WhenAll(executions.Select(execution => execution.Session.WaitForSettlementAsync(execution.ExecutionId))).WaitAsync(TestTimeout);
      store?.Dispose();
      client.Dispose();
      database.Dispose();
    }
    Assert.Equal(2, client.Calls);
    Assert.Equal(1, client.DisposeCalls); // Only the test owner disposed the shared client.
  }
}
