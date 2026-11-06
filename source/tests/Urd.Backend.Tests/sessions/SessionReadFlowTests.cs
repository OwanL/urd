using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class SessionReadFlowTests {
    [Fact]
    public async Task ReadsFollowFullCommittedContentWithoutOwningWorkOrConfusingFinalTextWithSettlement() {
        var sourceWaiting = Signal();
        var sourceRelease = Signal();
        var cleanupEntered = Signal();
        var cleanupRelease = Signal();
        CancellationToken modelToken = default;
        string input = new string('q', 600) + "\0🙂";
        string output = new string('x', 16_000) + "🙂\0λ" + new string('y', 24_000);
        async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
            modelToken = token;
            yield return Text(output[..1024]);
            sourceWaiting.TrySetResult();
            await sourceRelease.Task.WaitAsync(token);
            yield return Text(output[1024..]);
            yield return new() { FinishReason = ChatFinishReason.Stop };
        }

        var database = new TemporaryDatabase();
        var client = new ScriptedChatClient(token => new HeldDisposal(Response(token), cleanupEntered, cleanupRelease.Task));
        var store = SessionStore.Open(database.Path);
        PrimarySession session = await new SessionManager(store, client).CreatePrimaryAsync();
        Guid? ownedRun = null;
        try {
            SessionSnapshot idle = await session.ReadSnapshotAsync();
            Assert.False(idle.AdmissionFenced);
            Assert.Null(idle.UnsettledRunId);
            Assert.Empty(idle.Messages);
            Assert.Equal(0, client.Calls);

            InputReceipt receipt = await session.SubmitUserInputAsync(new(Guid.NewGuid(), 0, input));
            ownedRun = receipt.RunId;
            Guid runId = Assert.IsType<Guid>(ownedRun);
            await sourceWaiting.Task.WaitAsync(TestTimeout);
            SessionSnapshot initial = await WaitForOutputAsync(session);
            Assert.Equal(runId, initial.UnsettledRunId);
            Assert.False(initial.AdmissionFenced);
            MessagePreview user = Assert.Single(initial.Messages, row => row.Message.Role == MessageRole.User);
            MessagePreview assistant = Assert.Single(initial.Messages, row => row.Message.Role == MessageRole.Assistant);
            Assert.True(user.Truncated);
            Assert.True(assistant.Truncated);
            Assert.Equal(input, (await ReadAvailableAsync(session, new(user.Message, 0, 0))).Text);
            var first = await ReadAvailableAsync(session, new(assistant.Message, 0, 0));
            Assert.Equal(output[..1024], first.Text);
            Assert.False(first.Last.IsClosed);
            MessageContentPage tail = await session.ReadMessageContentAsync(first.Last.Next);
            Assert.Empty(tail.Text);
            Assert.Equal(first.Last.Next, tail.Next);
            Assert.False(tail.HasMore);

            using (var cancelledRead = new CancellationTokenSource()) {
                cancelledRead.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => session.ReadSnapshotAsync(cancelledRead.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => session.ReadMessageContentAsync(tail.Next, cancelledRead.Token));
            }
            Assert.False(modelToken.IsCancellationRequested);
            Assert.False(session.WaitForSettlementAsync(runId).IsCompleted);
            Assert.Equal(1, client.Calls);

            sourceRelease.TrySetResult();
            await cleanupEntered.Task.WaitAsync(TestTimeout);
            SessionSnapshot held = await session.ReadSnapshotAsync();
            MessagePreview finalText = Assert.Single(held.Messages, row => row.Message.Role == MessageRole.Assistant);
            Assert.Equal(assistant.Message, finalText.Message);
            Assert.True(finalText.AvailableThrough.SegmentIndex > assistant.AvailableThrough.SegmentIndex);
            Assert.True(finalText.FinalOutputCommitted);
            Assert.Null(finalText.Settlement);
            Assert.Equal(runId, held.UnsettledRunId);
            Assert.Null(held.LastSettlement);
            Assert.False(held.AdmissionFenced);
            Assert.False(session.WaitForSettlementAsync(runId).IsCompleted);
            var remainder = await ReadAvailableAsync(session, tail.Next);
            Assert.Equal(output, first.Text + remainder.Text);
            Assert.True(remainder.Last.IsClosed);
            Assert.True(remainder.Last.FinalOutputCommitted);

            cleanupRelease.TrySetResult();
            await session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout);
            SessionSnapshot settled = await session.ReadSnapshotAsync();
            Assert.Null(settled.UnsettledRunId);
            Assert.False(settled.AdmissionFenced);
            Assert.Equal(new ExecutionResult(runId, ExecutionOutcome.Completed), settled.LastSettlement);
            Assert.All(settled.Messages, row => Assert.Equal(ExecutionOutcome.Completed, row.Settlement));
            Assert.Equal(1, client.Calls);
            Assert.Equal(0, client.DisposeCalls);
        } finally {
            sourceRelease.TrySetResult();
            cleanupRelease.TrySetResult();
            if (ownedRun is { } runId) {
                await session.RequestStopAsync(runId);
                await session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout);
            }
            store.Dispose();
            client.Dispose();
            database.Dispose();
        }
    }

    [Fact]
    public async Task ReopenReadsRetainedUnsettledContentWithoutReplayAndDiagnosesStaleHandles() {
        using var database = new TemporaryDatabase();
        Guid sessionId, cleanId, runId = Guid.NewGuid(), outputId = Guid.NewGuid();
        using (SessionStore original = SessionStore.Open(database.Path)) {
            sessionId = original.Sessions.CreatePrimary(Guid.NewGuid()).Id;
            cleanId = original.Sessions.CreatePrimary(Guid.NewGuid()).Id;
            original.Inputs.Admit(sessionId, 0, new(Guid.NewGuid(), 0, "retained input"), true, runId);
            LlmRequest request = original.Runs.ClaimNextRequest(sessionId, runId, 0)!;
            Assert.True(original.Output.Append(sessionId, runId, 0, new(request.RequestId, outputId, 0, "partial\0🙂", false)));
        }

        using var client = new ScriptedChatClient(_ =>
            throw new InvalidOperationException("Reads must not start a model request."));
        using SessionStore store = SessionStore.Open(database.Path);
        var manager = new SessionManager(store, client);
        PrimarySession session = await manager.GetPrimaryAsync(sessionId);
        SessionSnapshot retained = await session.ReadSnapshotAsync();
        Assert.True(retained.AdmissionFenced);
        Assert.Equal(runId, retained.UnsettledRunId);
        Assert.Null(retained.LastSettlement);
        MessagePreview output = Assert.Single(retained.Messages, row => row.Message.Role == MessageRole.Assistant);
        Assert.Equal(outputId, output.Message.MessageId);
        var body = await ReadAvailableAsync(session, new(output.Message, 0, 0));
        Assert.Equal("partial\0🙂", body.Text);
        Assert.False(body.Last.IsClosed);
        Assert.False(body.Last.FinalOutputCommitted);

        PrimarySession clean = await manager.GetPrimaryAsync(cleanId);
        Assert.False((await clean.ReadSnapshotAsync()).AdmissionFenced);
        database.Execute($"UPDATE sessions SET history_revision = 1 WHERE session_id = '{cleanId:D}';");
        SessionSnapshot stale = await clean.ReadSnapshotAsync();
        Assert.Equal(1, stale.HistoryRevision);
        Assert.True(stale.AdmissionFenced);
        Assert.Null(stale.UnsettledRunId);
        Assert.Equal(0, client.Calls);
        Assert.Equal(DBNull.Value, database.Scalar("SELECT outcome FROM execution_runs;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_requests;"));
    }

    private static async Task<(string Text, MessageContentPage Last)> ReadAvailableAsync(
        PrimarySession session, MessageContentCursor cursor) {
        using var deadline = new CancellationTokenSource(TestTimeout);
        var text = new StringBuilder();
        MessageContentPage page;
        do {
            page = await session.ReadMessageContentAsync(cursor, deadline.Token).WaitAsync(deadline.Token);
            Assert.Equal(cursor, page.From);
            Assert.InRange(Encoding.UTF8.GetByteCount(page.Text), 0, 16 * 1024);
            if (page.HasMore)
                Assert.NotEqual(cursor, page.Next);
            text.Append(page.Text);
            cursor = page.Next;
        } while (page.HasMore);
        return (text.ToString(), page);
    }

    private static async Task<SessionSnapshot> WaitForOutputAsync(PrimarySession session) {
        using var deadline = new CancellationTokenSource(TestTimeout);
        while (true) {
            SessionSnapshot snapshot = await session.ReadSnapshotAsync(deadline.Token);
            if (snapshot.Messages.Any(row => row.Message.Role == MessageRole.Assistant))
                return snapshot;
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class HeldDisposal(IAsyncEnumerable<ChatResponseUpdate> source,
        TaskCompletionSource entered, Task release) : IAsyncEnumerable<ChatResponseUpdate> {
        public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new Enumerator(source.GetAsyncEnumerator(cancellationToken), entered, release);

        private sealed class Enumerator(IAsyncEnumerator<ChatResponseUpdate> inner,
            TaskCompletionSource entered, Task release) : IAsyncEnumerator<ChatResponseUpdate> {
            public ChatResponseUpdate Current => inner.Current;
            public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();
            public async ValueTask DisposeAsync() {
                await inner.DisposeAsync();
                entered.TrySetResult();
                await release;
            }
        }
    }
}
