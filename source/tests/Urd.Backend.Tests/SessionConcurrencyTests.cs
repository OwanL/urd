using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests;

public sealed class SessionConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConcurrentExactSubmissionsShareOneDurableReceiptRunAndModelRequest()
    {
        var ready = Signal();
        var start = Signal();
        var modelEntered = Signal();
        var modelRelease = Signal();
        int waiting = 0;
        async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token)
        {
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
        Task<InputReceipt>[] submissions = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref waiting) == 2)
                ready.TrySetResult();
            await start.Task;
            return await session.SubmitAsync(command);
        })).ToArray();
        try
        {
            await ready.Task.WaitAsync(Timeout);
            start.TrySetResult();
            InputReceipt[] receipts = await Task.WhenAll(submissions).WaitAsync(Timeout);
            await modelEntered.Task.WaitAsync(Timeout);

            Assert.Equal(InputDecision.Accepted, receipts[0].Decision);
            Guid runId = Assert.IsType<Guid>(receipts[0].RunId);
            Assert.Equal(receipts[0], receipts[1]);
            Assert.Equal(receipts[0], await session.GetInputReceiptAsync(command.InputId));
            Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM input_decisions;"));
            Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_runs;"));
            Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_requests;"));
            Assert.Equal(1, client.Calls);
            Assert.False(session.WaitForSettlementAsync(runId).IsCompleted);

            modelRelease.TrySetResult();
            RunSettlement[] settlements = await Task.WhenAll(receipts.Select(receipt =>
                session.WaitForSettlementAsync(receipt.RunId!.Value))).WaitAsync(Timeout);
            Assert.Equal(settlements[0], settlements[1]);
            Assert.Equal(ExecutionOutcome.Completed, settlements[0].Outcome);
            Assert.Equal(1, client.Calls);
            Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_requests;"));
        }
        finally
        {
            start.TrySetResult();
            modelRelease.TrySetResult();
            // Join all callers before discovering runs, then drain owned work before disposal.
            InputReceipt[] receipts = await Task.WhenAll(submissions).WaitAsync(Timeout);
            foreach (Guid runId in receipts.Where(receipt => receipt.RunId.HasValue)
                         .Select(receipt => receipt.RunId!.Value).Distinct())
            {
                await session.RequestStopAsync(runId);
                await session.WaitForSettlementAsync(runId).WaitAsync(Timeout);
            }
            store.Dispose();
            client.Dispose();
            database.Dispose();
        }
    }

    [Fact]
    public async Task CancellingSettlementWaitDoesNotCancelOwnedModelWork()
    {
        var modelEntered = Signal();
        var modelRelease = Signal();
        CancellationToken modelToken = default;
        async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token)
        {
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
        try
        {
            InputReceipt receipt = await session.SubmitAsync(new InputCommand(Guid.NewGuid(), 0, "go"));
            ownedRun = receipt.RunId;
            Assert.Equal(InputDecision.Accepted, receipt.Decision);
            Guid runId = Assert.IsType<Guid>(ownedRun);
            await modelEntered.Task.WaitAsync(Timeout);

            Task<RunSettlement> cancelledWait = session.WaitForSettlementAsync(runId, waitCancellation.Token);
            Assert.False(cancelledWait.IsCompleted);
            waitCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait.WaitAsync(Timeout));
            Assert.False(modelToken.IsCancellationRequested);
            Assert.False(session.WaitForSettlementAsync(runId).IsCompleted);
            Assert.Equal(0L, database.Scalar("SELECT stopping FROM execution_runs;"));
            Assert.Equal(DBNull.Value, database.Scalar("SELECT outcome FROM execution_runs;"));
            Assert.Equal(DBNull.Value, database.Scalar("SELECT outcome FROM execution_requests;"));

            modelRelease.TrySetResult();
            Assert.Equal(ExecutionOutcome.Completed,
                (await session.WaitForSettlementAsync(runId).WaitAsync(Timeout)).Outcome);
            Assert.Equal((long)ExecutionOutcome.Completed, database.Scalar("SELECT outcome FROM execution_runs;"));
            Assert.Equal((long)ExecutionOutcome.Completed, database.Scalar("SELECT outcome FROM execution_requests;"));
            Assert.Equal("completed after release",
                database.Scalar("SELECT group_concat(CAST(text_utf8 AS TEXT), '' ORDER BY batch_index) FROM execution_output;"));
            Assert.Equal(1, client.Calls);
            Assert.Equal(0, client.DisposeCalls);
        }
        finally
        {
            modelRelease.TrySetResult();
            if (ownedRun is { } runId)
            {
                await session.RequestStopAsync(runId);
                await session.WaitForSettlementAsync(runId).WaitAsync(Timeout);
            }
            store.Dispose();
            client.Dispose();
            database.Dispose();
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
