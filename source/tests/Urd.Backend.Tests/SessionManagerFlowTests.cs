using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests;

public sealed class SessionManagerFlowTests
{
    [Fact]
    public async Task SharedClientStreamsAcrossCanonicalChatsAndRetainedRetriesNeverRestartWork()
    {
        TimeSpan timeout = TimeSpan.FromSeconds(10);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token)
        {
            if (Interlocked.Increment(ref entered) == 2)
                bothEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            yield return new() { FinishReason = ChatFinishReason.Stop };
        }

        var database = new TemporaryDatabase();
        var client = new ScriptedChatClient(Response);
        SessionStore? store = null;
        var runs = new List<(PrimarySession Session, Guid RunId)>();
        try
        {
            store = SessionStore.Open(database.Path);
            var manager = new SessionManager(store, client);
            PrimarySession[] sessions = [await manager.CreatePrimaryAsync(), await manager.CreatePrimaryAsync()];
            Assert.NotEqual(sessions[0].Id, sessions[1].Id);
            Guid inputId = Guid.NewGuid();
            InputCommand[] commands = [new(inputId, 0, "first chat"), new(inputId, 0, "second chat")];
            var receipts = new InputReceipt[2];
            for (int i = 0; i < sessions.Length; i++)
            {
                Assert.Same(sessions[i], await manager.GetPrimaryAsync(sessions[i].Id));
                receipts[i] = await sessions[i].SubmitAsync(commands[i]);
                if (receipts[i].RunId is { } runId)
                    runs.Add((sessions[i], runId));
                Assert.Equal(InputDecision.Accepted, receipts[i].Decision);
                Assert.IsType<Guid>(receipts[i].RunId);
                Assert.Equal(sessions[i].Id, receipts[i].SessionId);
                Assert.Equal(commands[i], receipts[i].Command);
            }

            await bothEntered.Task.WaitAsync(timeout);
            foreach (var (session, runId) in runs)
                Assert.False(session.WaitForSettlementAsync(runId).IsCompleted);
            for (int i = 0; i < sessions.Length; i++)
            {
                Assert.Equal(receipts[i], await sessions[i].GetInputReceiptAsync(inputId));
                InputReceipt retry = await sessions[i].SubmitAsync(commands[i]);
                if (retry.RunId is { } runId && runId != receipts[i].RunId)
                    runs.Add((sessions[i], runId));
                Assert.Equal(receipts[i], retry);
            }
            Assert.Equal(2, client.Calls);
            release.TrySetResult();
            RunSettlement[] settlements = await Task.WhenAll(runs.Select(run =>
                run.Session.WaitForSettlementAsync(run.RunId))).WaitAsync(timeout);
            Assert.All(settlements, settlement => Assert.Equal(ExecutionOutcome.Completed, settlement.Outcome));
            runs.Clear();
            store.Dispose();
            Assert.Equal(0, client.DisposeCalls);

            store = SessionStore.Open(database.Path);
            var reopenedManager = new SessionManager(store, client);
            for (int i = 0; i < sessions.Length; i++)
            {
                PrimarySession loaded = await reopenedManager.GetPrimaryAsync(sessions[i].Id);
                Assert.NotSame(sessions[i], loaded);
                Assert.Same(loaded, await reopenedManager.GetPrimaryAsync(loaded.Id));
                Assert.Equal(receipts[i], await loaded.GetInputReceiptAsync(inputId));
                InputReceipt retry = await loaded.SubmitAsync(commands[i]);
                if (retry.RunId is { } runId)
                    runs.Add((loaded, runId));
                Assert.Equal(receipts[i], retry);
                Assert.Equal(ExecutionOutcome.Completed,
                    (await loaded.WaitForSettlementAsync(retry.RunId!.Value).WaitAsync(timeout)).Outcome);
            }
            Assert.Equal(2, client.Calls);
            Assert.Equal(0, client.DisposeCalls);
        }
        finally
        {
            release.TrySetResult();
            // A failed timed wait must not dispose borrowed resources beneath live work.
            await Task.WhenAll(runs.Select(run => run.Session.RequestStopAsync(run.RunId))).WaitAsync(timeout);
            await Task.WhenAll(runs.Select(run => run.Session.WaitForSettlementAsync(run.RunId))).WaitAsync(timeout);
            store?.Dispose();
            client.Dispose();
            database.Dispose();
        }
        Assert.Equal(2, client.Calls);
        Assert.Equal(1, client.DisposeCalls); // Only the test owner disposed the shared client.
    }
}
