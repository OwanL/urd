using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

// Response buffering through a real session: chunk sizes, flush timing and Unicode boundaries.
public sealed class LlmResponseWriterTests {
    [Fact]
    public async Task CoalescesTinyUpdatesAndSplitsOversizedTextAtUtf8ScalarBoundaries() {
        string text = new string('a', 16 * 1024 - 1) + "😀" + new string('b', 40_000) + "\0終";
        var client = new ScriptedChatClient(token => TextResponse(text, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("go");

        ExecutionResult settlement = await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout);
        Assert.Equal(ExecutionOutcome.Completed, settlement.Outcome);
        var output = StoredOutput.Read(fixture.Database);
        Assert.NotEmpty(output);
        Assert.All(output, row => Assert.InRange(row.Bytes.Length, 0, 16 * 1024));
        Assert.Equal(text, StoredOutput.ReadText(fixture.Database));
        Assert.Single(output, row => row.Final);
        Assert.True(output.Count < 100, "Tiny source updates must not each cause a durable append.");
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, client.DisposeCalls);
        Assert.Equal("go", client.LastRequest[^1].Text);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreservesSupplementaryScalarAcrossUpdateBoundaries(bool splitScalar, bool overflowBoundary) {
        const int maxChunkBytes = 16 * 1024;
        string prefix = new string('a', overflowBoundary ? maxChunkBytes - 3 : maxChunkBytes - 4);
        const string scalar = "😀";
        const string suffix = "é終\0";
        string text = prefix + scalar + suffix;
        string[] updates = splitScalar
            ? [prefix, scalar[..1], string.Empty, scalar[1..], suffix]
            : [prefix + scalar + suffix];
        var client = new ScriptedChatClient(token => TextUpdates(updates, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("go");

        Assert.Equal(ExecutionOutcome.Completed,
            (await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout)).Outcome);
        var output = StoredOutput.Read(fixture.Database);
        Assert.NotEmpty(output);
        Assert.All(output, row => Assert.InRange(row.Bytes.Length, 0, maxChunkBytes));
        Assert.Equal(text, StoredOutput.ReadText(fixture.Database));
        Assert.Single(output, row => row.Final);
        Assert.True(output[^1].Final);
    }

    [Fact]
    public async Task FlushesDeadlineWhileTheSingleNextSourceReadIsPaused() {
        var waiting = Signal();
        var release = Signal();
        var client = new ScriptedChatClient(token => Paused(waiting, release, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("go");
        try {
            await waiting.Task.WaitAsync(TestTimeout);
            await StoredOutput.WaitForAnyAsync(fixture.Database);
            Assert.Equal("visible", StoredOutput.ReadText(fixture.Database));
            Assert.All(StoredOutput.Read(fixture.Database), row => Assert.False(row.Final));
            Assert.False(fixture.Session.WaitForSettlementAsync(runId).IsCompleted);
            Assert.Equal(1, client.Calls);
        } finally {
            release.TrySetResult();
        }

        Assert.Equal(ExecutionOutcome.Completed,
            (await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout)).Outcome);
    }

    [Theory]
    [InlineData(0xDC00, null)]
    [InlineData(0xD800, 0x0078)]
    [InlineData(0xD800, 0xD800)]
    [InlineData(0xD800, null)]
    public async Task RejectsMalformedOrIncompleteUnicodeWithoutFinalMarker(int firstCodeUnit, int? secondCodeUnit) {
        // Attribute strings use UTF-8 metadata, so construct lone surrogates at runtime.
        string firstUpdate = ((char)firstCodeUnit).ToString();
        string[] updates = secondCodeUnit is { } codeUnit
            ? [firstUpdate, ((char)codeUnit).ToString()]
            : [firstUpdate];
        var client = new ScriptedChatClient(token => TextUpdates(updates, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("go");

        Assert.Equal(ExecutionOutcome.Failed,
            (await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout)).Outcome);
        Assert.DoesNotContain(StoredOutput.Read(fixture.Database), row => row.Final);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16 * 1024)]
    public async Task PersistsEmptyAndExactBoundaryResponsesWithOrderedOutputIdentity(int textLength) {
        string text = new string('x', textLength);
        var client = new ScriptedChatClient(token => ExactResponse(text, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("go");

        Assert.Equal(ExecutionOutcome.Completed,
            (await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout)).Outcome);
        var output = StoredOutput.Read(fixture.Database);
        Guid requestId = Guid.Parse(Assert.IsType<string>(fixture.Database.Scalar(
            "SELECT request_id FROM execution_requests;")));
        Assert.NotEmpty(output);
        Guid responseId = output[0].ResponseId;

        Assert.NotEqual(Guid.Empty, responseId);
        Assert.All(output, row => {
            Assert.Equal(requestId, row.RequestId);
            Assert.Equal(responseId, row.ResponseId);
        });
        Assert.Equal(Enumerable.Range(0, output.Count).Select(index => (long)index),
            output.Select(row => row.ChunkIndex));
        Assert.Equal(text, StoredOutput.ReadText(fixture.Database));
        Assert.Single(output, row => row.Final);
        Assert.True(output[^1].Final);

        if (textLength == 0) {
            Assert.Single(output);
            Assert.Empty(output[0].Bytes);
            Assert.Equal(0, output[0].ChunkIndex);
        } else {
            Assert.Equal(2, output.Count);
            Assert.Equal(16 * 1024, output[0].Bytes.Length);
            Assert.False(output[0].Final);
            Assert.Empty(output[1].Bytes);
            Assert.True(output[1].Final);
        }
    }

    [Fact]
    public async Task WaitsForChunkWritesBeforeContinuingAndKeepsTheirOrder() {
        var writing = Signal();
        var release = Signal();
        var chunks = new List<LlmResponseChunk>();
        var writer = new LlmResponseWriter(Guid.NewGuid(), async chunk => {
            writing.TrySetResult();
            await release.Task;
            chunks.Add(chunk);
        });
        string text = new string('x', 16 * 1024) + "🙂tail";
        Task append = writer.AppendTextAsync(text);
        try {
            await writing.Task.WaitAsync(TestTimeout);
            Assert.False(append.IsCompleted);
            Assert.Empty(chunks);
        } finally {
            release.TrySetResult();
            await append.WaitAsync(TestTimeout);
        }

        await writer.FinishResponseAsync();
        Assert.Equal(text, string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Collection(chunks,
            first => {
                Assert.Equal(0, first.ChunkIndex);
                Assert.False(first.IsFinal);
            },
            last => {
                Assert.Equal(1, last.ChunkIndex);
                Assert.True(last.IsFinal);
            });
    }

    [Fact]
    public async Task FailedChunkWriteStopsBeforeProcessingRemainingText() {
        var failure = new IOException("Chunk save failed.");
        int attempts = 0;
        var writer = new LlmResponseWriter(Guid.NewGuid(), _ => {
            attempts++;
            return Task.FromException(failure);
        });
        // The invalid tail must not mask the earlier persistence failure or cause another write.
        string text = new string('x', 16 * 1024) + "\ud800x";

        IOException observed = await Assert.ThrowsAsync<IOException>(() => writer.AppendTextAsync(text));
        Assert.Same(failure, observed);
        Assert.Equal(1, attempts);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TextResponse(string text,
        [EnumeratorCancellation] CancellationToken token) {
        await Task.CompletedTask;
        for (int i = 0; i < 1_000; i++) {
            token.ThrowIfCancellationRequested();
            yield return Text(text[i].ToString());
        }

        // Deliberately split the surrogate pair across updates, then yield an oversized update.
        yield return Text(text[1_000..(16 * 1024)]);
        yield return Text(text[(16 * 1024)..]);
        yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ExactResponse(string text,
        [EnumeratorCancellation] CancellationToken token) {
        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
        if (text.Length != 0)
            yield return Text(text);

        yield return new() { FinishReason = ChatFinishReason.Stop };
    }
}
