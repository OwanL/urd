using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests;

public sealed class SessionExecutionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string CancellationCallbackFailureMessage = "Scripted cancellation callback failure.";
    private const string EnumeratorDisposalFailureMessage = "Scripted enumerator disposal failure.";

    [Fact]
    public async Task CoalescesTinyUpdatesAndSplitsOversizedTextAtUtf8ScalarBoundaries()
    {
        string text = new string('a', 16 * 1024 - 1) + "😀" + new string('b', 40_000) + "\0終";
        var client = new ScriptedChatClient(token => TextResponse(text, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("go");

        ExecutionResult settlement = await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(Timeout);
        Assert.Equal(ExecutionOutcome.Completed, settlement.Outcome);
        List<(byte[] Bytes, bool Final)> output = ReadOutput(fixture.Database);
        Assert.NotEmpty(output);
        Assert.All(output, row => Assert.InRange(row.Bytes.Length, 0, 16 * 1024));
        Assert.Equal(text, string.Concat(output.Select(row => StrictUtf8.GetString(row.Bytes))));
        Assert.Single(output, row => row.Final);
        Assert.True(output.Count < 100, "Tiny source updates must not each cause a durable append.");
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, client.DisposeCalls);
        Assert.Equal("go", client.LastRequest.Last().Text);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreservesSupplementaryScalarAcrossUpdateBoundaries(bool splitScalar, bool overflowBoundary)
    {
        const int maxChunkBytes = 16 * 1024;
        string prefix = new string('a', overflowBoundary ? maxChunkBytes - 3 : maxChunkBytes - 4);
        const string scalar = "😀";
        const string suffix = "é終\0";
        string text = prefix + scalar + suffix;
        string[] updates = splitScalar
            ? [prefix, scalar[..1], string.Empty, scalar[1..], suffix]
            : [prefix + scalar + suffix];
        var client = new ScriptedChatClient(token => TextUpdatesResponse(updates, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("go");

        Assert.Equal(ExecutionOutcome.Completed,
            (await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(Timeout)).Outcome);
        List<(byte[] Bytes, bool Final)> output = ReadOutput(fixture.Database);
        Assert.NotEmpty(output);
        Assert.All(output, row => Assert.InRange(row.Bytes.Length, 0, maxChunkBytes));
        Assert.Equal(text, string.Concat(output.Select(row => StrictUtf8.GetString(row.Bytes))));
        Assert.Single(output, row => row.Final);
        Assert.True(output[^1].Final);
    }

    [Fact]
    public async Task FlushesDeadlineWhileTheSingleNextSourceReadIsPaused()
    {
        var waiting = Signal();
        var release = Signal();
        var client = new ScriptedChatClient(token => PausedResponse(waiting, release, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("go");
        try
        {
            await waiting.Task.WaitAsync(Timeout);
            await WaitForOutputAsync(fixture.Database);
            Assert.Equal("visible", string.Concat(ReadOutput(fixture.Database).Select(row => StrictUtf8.GetString(row.Bytes))));
            Assert.All(ReadOutput(fixture.Database), row => Assert.False(row.Final));
            Assert.False(fixture.Session.WaitForSettlementAsync(runId).IsCompleted);
            Assert.Equal(1, client.Calls);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(ExecutionOutcome.Completed,
            (await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(Timeout)).Outcome);
    }

    [Theory]
    [InlineData("eof")]
    [InlineData("length")]
    [InlineData("exception")]
    [InlineData("exception-after-stop")]
    public async Task RequiresPositiveTerminalEvidenceAndAnOtherwiseSuccessfulRequest(string failureScenario)
    {
        var client = new ScriptedChatClient(token => FailedResponse(failureScenario, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("go");

        Assert.Equal(ExecutionOutcome.Failed,
            (await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(Timeout)).Outcome);
        Assert.Equal((long)ExecutionOutcome.Failed, fixture.Database.Scalar("SELECT outcome FROM execution_requests;"));
        Assert.All(ReadOutput(fixture.Database), row => Assert.False(row.Final));
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, client.DisposeCalls);
    }

    [Fact]
    public async Task StopWaitsForActualCleanupBeforeSettlementOrReplacementAndBorrowsClient()
    {
        var waiting = Signal();
        var cleanupEntered = Signal();
        var cleanupRelease = Signal();
        int request = 0;
        var client = new ScriptedChatClient(token => Interlocked.Increment(ref request) == 1
            ? HeldCleanupResponse(waiting, cleanupEntered, cleanupRelease, token)
            : CompleteResponse(token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("first");
        try
        {
            await waiting.Task.WaitAsync(Timeout);
            await fixture.Session.RequestStopAsync(runId);
            await fixture.Session.RequestStopAsync(runId);
            await cleanupEntered.Task.WaitAsync(Timeout);
            Assert.False(fixture.Session.WaitForSettlementAsync(runId).IsCompleted);
            InputReceipt refused = await fixture.Session.SubmitUserInputAsync(new InputCommand(Guid.NewGuid(), 0, "too early"));
            Assert.Equal(InputDecision.Refused, refused.Decision);
            Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);
            Assert.Null(refused.RunId);
            Assert.Equal(1, client.Calls);
        }
        finally
        {
            cleanupRelease.TrySetResult();
        }

        Assert.Equal(ExecutionOutcome.Cancelled,
            (await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(Timeout)).Outcome);
        Guid replacement = await fixture.StartRunAsync("second");
        Assert.NotEqual(runId, replacement);
        Assert.Equal(ExecutionOutcome.Completed,
            (await fixture.Session.WaitForSettlementAsync(replacement).WaitAsync(Timeout)).Outcome);
        Assert.Equal(2, client.Calls);
        Assert.Equal(0, client.DisposeCalls);
    }

    [Theory]
    [InlineData(0xDC00, null)]
    [InlineData(0xD800, "x")]
    [InlineData(0xD800, null)]
    public async Task RejectsMalformedOrIncompleteUnicodeWithoutFinalMarker(int firstCodeUnit, string? secondUpdate)
    {
        // Attribute strings use UTF-8 metadata, so construct the lone surrogate at runtime.
        string firstUpdate = ((char)firstCodeUnit).ToString();
        string[] updates = secondUpdate is null ? [firstUpdate] : [firstUpdate, secondUpdate];
        var client = new ScriptedChatClient(token => TextUpdatesResponse(updates, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("go");

        Assert.Equal(ExecutionOutcome.Failed,
            (await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(Timeout)).Outcome);
        Assert.DoesNotContain(ReadOutput(fixture.Database), row => row.Final);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16 * 1024)]
    public async Task PersistsEmptyAndExactBoundaryResponsesWithOrderedOutputIdentity(int textLength)
    {
        string text = new string('x', textLength);
        var client = new ScriptedChatClient(token => ExactResponse(text, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("go");

        Assert.Equal(ExecutionOutcome.Completed,
            (await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(Timeout)).Outcome);
        List<(Guid RequestId, Guid ResponseId, long ChunkIndex, byte[] Bytes, bool Final)> output =
            ReadOutputDetails(fixture.Database);
        Guid requestId = Guid.Parse(Assert.IsType<string>(fixture.Database.Scalar(
            "SELECT request_id FROM execution_requests;")));
        Assert.NotEmpty(output);
        Guid responseId = output[0].ResponseId;

        Assert.NotEqual(Guid.Empty, responseId);
        Assert.All(output, row =>
        {
            Assert.Equal(requestId, row.RequestId);
            Assert.Equal(responseId, row.ResponseId);
        });
        Assert.Equal(Enumerable.Range(0, output.Count).Select(index => (long)index),
            output.Select(row => row.ChunkIndex));
        Assert.Equal(text, string.Concat(output.Select(row => StrictUtf8.GetString(row.Bytes))));
        Assert.Single(output, row => row.Final);
        Assert.True(output[^1].Final);

        if (textLength == 0)
        {
            Assert.Single(output);
            Assert.Empty(output[0].Bytes);
            Assert.Equal(0, output[0].ChunkIndex);
        }
        else
        {
            Assert.Equal(2, output.Count);
            Assert.Equal(16 * 1024, output[0].Bytes.Length);
            Assert.False(output[0].Final);
            Assert.Empty(output[1].Bytes);
            Assert.True(output[1].Final);
        }
    }

    [Fact]
    public async Task StopJoinsReentrantCallbackOutsideGuardAndStaleStopCannotCancelReplacement()
    {
        var sourceEntered = Signal();
        var sourceRelease = Signal();
        var sourceExited = Signal();
        var callbackRelease = Signal();
        var replacementEntered = Signal();
        var replacementRelease = Signal();
        var callbackSnapshot = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        PrimarySession session = null!;
        int request = 0;
        var client = new ScriptedChatClient(token => Interlocked.Increment(ref request) == 1
            ? ReentrantBlockedCancellationResponse(session, sourceEntered, sourceRelease, sourceExited,
                callbackSnapshot, callbackRelease, token)
            : PausedResponse(replacementEntered, replacementRelease, token));
        await using var fixture = new ExecutionFixture(client);
        session = fixture.Session;
        Guid runId = await fixture.StartRunAsync("first");
        Task<ExecutionResult> settlement = session.WaitForSettlementAsync(runId);
        Task firstStop = Task.CompletedTask;
        Task secondStop = Task.CompletedTask;
        try
        {
            await sourceEntered.Task.WaitAsync(Timeout);
            firstStop = session.RequestStopAsync(runId);
            SessionSnapshot callbackRead = await callbackSnapshot.Task.WaitAsync(Timeout);
            Assert.True(callbackRead.StopRequested);
            sourceRelease.TrySetResult();
            await sourceExited.Task.WaitAsync(Timeout);
            // Reach saved request completion, not merely the iterator's finally block,
            // while the run still owns the blocked cancellation callback.
            using (var deadline = new CancellationTokenSource(Timeout))
            {
                while (fixture.Database.Scalar("SELECT outcome FROM execution_requests;") is DBNull)
                {
                    await Task.Delay(10, deadline.Token);
                }
            }

            secondStop = session.RequestStopAsync(runId);
            Assert.False(firstStop.IsCompleted);
            Assert.False(secondStop.IsCompleted);
            Assert.False(settlement.IsCompleted);
            InputReceipt refused = await session.SubmitUserInputAsync(
                new InputCommand(Guid.NewGuid(), 0, "too early"));
            Assert.Equal(InputDecision.Refused, refused.Decision);
            Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);
            Assert.Equal(1, client.Calls);
        }
        finally
        {
            sourceRelease.TrySetResult();
            callbackRelease.TrySetResult();
        }

        await firstStop.WaitAsync(Timeout);
        await secondStop.WaitAsync(Timeout);
        Assert.Equal(ExecutionOutcome.Cancelled, (await settlement.WaitAsync(Timeout)).Outcome);

        Guid replacement = await fixture.StartRunAsync("replacement");
        Task<ExecutionResult> replacementSettlement = session.WaitForSettlementAsync(replacement);
        try
        {
            await replacementEntered.Task.WaitAsync(Timeout);
            await session.RequestStopAsync(runId).WaitAsync(Timeout);
            Assert.False(replacementSettlement.IsCompleted);
        }
        finally
        {
            replacementRelease.TrySetResult();
        }

        Assert.Equal(ExecutionOutcome.Completed, (await replacementSettlement.WaitAsync(Timeout)).Outcome);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task CancellationCallbackFailureFencesUnsettledRunAfterProviderCleanup()
    {
        var requestEntered = Signal();
        var providerExited = Signal();
        var database = new TemporaryDatabase();
        var store = SessionStore.Open(database.Path);
        var client = new ScriptedChatClient(token => CancellationCallbackFailureResponse(
            requestEntered, providerExited, token));
        var session = new PrimarySession(store.CreatePrimary(Guid.NewGuid()), store, client);
        Guid? runId = null;
        try
        {
            InputReceipt receipt = await session.SubmitUserInputAsync(new InputCommand(Guid.NewGuid(), 0, "go"));
            Assert.Equal(InputDecision.Accepted, receipt.Decision);
            runId = Assert.IsType<Guid>(receipt.RunId);
            Guid acceptedRun = runId.Value;
            await requestEntered.Task.WaitAsync(Timeout);

            Task stop = session.RequestStopAsync(acceptedRun);
            await providerExited.Task.WaitAsync(Timeout);
            await AssertCancellationCallbackFailureAsync(stop);
            await AssertCancellationCallbackFailureAsync(session.WaitForSettlementAsync(acceptedRun));

            SessionSnapshot snapshot = await session.ReadSnapshotAsync();
            Assert.True(snapshot.AdmissionFenced);
            Assert.Equal(acceptedRun, snapshot.UnsettledRunId);
            Assert.Null(snapshot.LastSettlement);
            Assert.Equal(DBNull.Value, database.Scalar("SELECT outcome FROM execution_runs;"));
            InputReceipt refused = await session.SubmitUserInputAsync(
                new InputCommand(Guid.NewGuid(), 0, "replacement"));
            Assert.Equal(InputDecision.Refused, refused.Decision);
            Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);
            Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_runs;"));
        }
        finally
        {
            if (runId is { } acceptedRun)
            {
                if (requestEntered.Task.IsCompletedSuccessfully)
                {
                    await AssertCancellationCallbackFailureAsync(session.RequestStopAsync(acceptedRun));
                    await providerExited.Task.WaitAsync(Timeout);
                    await AssertCancellationCallbackFailureAsync(session.WaitForSettlementAsync(acceptedRun));
                }
                else
                {
                    await session.RequestStopAsync(acceptedRun).WaitAsync(Timeout);
                    await session.WaitForSettlementAsync(acceptedRun).WaitAsync(Timeout);
                }
            }

            store.Dispose();
            client.Dispose();
            database.Dispose();
        }
    }

    [Fact]
    public async Task OutputWriteAndCleanupFailuresAreRetainedWhileRunRemainsFenced()
    {
        var database = new TemporaryDatabase();
        var store = SessionStore.Open(database.Path);
        var disposed = Signal();
        var client = new ScriptedChatClient(token => WriteAndCleanupFailureResponse(disposed, token));
        var session = new PrimarySession(store.CreatePrimary(Guid.NewGuid()), store, client);
        Guid? runId = null;
        try
        {
            database.Execute("""
                CREATE TRIGGER reject_output BEFORE INSERT ON execution_output
                BEGIN SELECT RAISE(ABORT, 'Injected output write failure'); END;
                """);
            InputReceipt receipt = await session.SubmitUserInputAsync(new InputCommand(Guid.NewGuid(), 0, "go"));
            Assert.Equal(InputDecision.Accepted, receipt.Decision);
            runId = receipt.RunId;
            Guid acceptedRun = Assert.IsType<Guid>(runId);

            Exception failure = await Assert.ThrowsAnyAsync<Exception>(
                () => session.WaitForSettlementAsync(acceptedRun).WaitAsync(Timeout));
            Exception[] failures = FlattenFailures(failure).ToArray();
            Assert.Contains(failures, error => error.GetBaseException() is SqliteException sqlite &&
                sqlite.SqliteErrorCode == 19);
            Assert.Contains(failures, error => error.Message == CancellationCallbackFailureMessage);
            Assert.Contains(failures, error => error.Message == EnumeratorDisposalFailureMessage);
            await disposed.Task.WaitAsync(Timeout);

            SessionSnapshot snapshot = await session.ReadSnapshotAsync();
            Assert.True(snapshot.AdmissionFenced);
            Assert.Equal(acceptedRun, snapshot.UnsettledRunId);
            Assert.Null(snapshot.LastSettlement);
            Assert.Equal(DBNull.Value, database.Scalar("SELECT outcome FROM execution_runs;"));
            Assert.Equal(DBNull.Value, database.Scalar("SELECT outcome FROM execution_requests;"));
            InputReceipt refused = await session.SubmitUserInputAsync(
                new InputCommand(Guid.NewGuid(), 0, "replacement"));
            Assert.Equal(InputDecision.Refused, refused.Decision);
            Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);
            Assert.Null(refused.RunId);
            Assert.Equal(1, client.Calls);
            Assert.Equal(0, client.DisposeCalls);
        }
        finally
        {
            if (runId is { } acceptedRun)
            {
                try
                {
                    await session.RequestStopAsync(acceptedRun).WaitAsync(Timeout);
                }
                catch (Exception failure) when (failure is not TimeoutException &&
                    FlattenFailures(failure).Any(error => error.Message == CancellationCallbackFailureMessage))
                {
                }

                await disposed.Task.WaitAsync(Timeout);
                try
                {
                    await session.WaitForSettlementAsync(acceptedRun).WaitAsync(Timeout);
                }
                catch (Exception failure) when (failure is not TimeoutException &&
                    FlattenFailures(failure).Any(error => error.Message == EnumeratorDisposalFailureMessage))
                {
                }
            }

            store.Dispose();
            client.Dispose();
            database.Dispose();
        }
    }

    [Fact]
    public async Task OutputWriteFailureCleansUpButFencesRatherThanSettlesOrReplacesTheRun()
    {
        var database = new TemporaryDatabase();
        var store = SessionStore.Open(database.Path);
        var cleaned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(token => WriteFailureResponse(cleaned, token));
        var session = new PrimarySession(store.CreatePrimary(Guid.NewGuid()), store, client);
        Guid? runId = null;
        var command = new InputCommand(Guid.NewGuid(), 0, "go");
        try
        {
            database.Execute("""
                CREATE TRIGGER reject_output BEFORE INSERT ON execution_output
                BEGIN SELECT RAISE(ABORT, 'Injected output write failure'); END;
                """);
            InputReceipt receipt = await session.SubmitUserInputAsync(command);
            runId = receipt.RunId;
            Assert.Equal(InputDecision.Accepted, receipt.Decision);
            Guid acceptedRun = Assert.IsType<Guid>(runId);

            Exception failure = await Assert.ThrowsAnyAsync<Exception>(
                () => session.WaitForSettlementAsync(acceptedRun).WaitAsync(Timeout));
            Assert.Equal(19, Assert.IsType<SqliteException>(failure.GetBaseException()).SqliteErrorCode);
            Assert.True(await cleaned.Task.WaitAsync(Timeout));
            SessionSnapshot snapshot = await session.ReadSnapshotAsync();
            Assert.True(snapshot.AdmissionFenced);
            Assert.Equal(acceptedRun, snapshot.UnsettledRunId);
            Assert.Null(snapshot.LastSettlement);
            Assert.Single(snapshot.Messages, row => row.Message.Role == MessageRole.User);
            Assert.DoesNotContain(snapshot.Messages, row => row.Message.Role == MessageRole.Assistant);
            Assert.Equal(DBNull.Value, database.Scalar("SELECT outcome FROM execution_runs;"));
            Assert.Equal(DBNull.Value, database.Scalar("SELECT outcome FROM execution_requests;"));
            Assert.Empty(ReadOutput(database));
            InputReceipt refused = await session.SubmitUserInputAsync(new InputCommand(Guid.NewGuid(), 0, "replacement"));
            Assert.Equal(InputDecision.Refused, refused.Decision);
            Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);
            Assert.Null(refused.RunId);
            Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_runs;"));
            Assert.Equal(1, client.Calls);
            Assert.Equal(0, client.DisposeCalls);
        }
        finally
        {
            if (runId is { } acceptedRun)
            {
                // Stop is only a cleanup fallback, not recovery or proof of saved settlement.
                await session.RequestStopAsync(acceptedRun);
                await cleaned.Task.WaitAsync(Timeout);
                try
                {
                    await session.WaitForSettlementAsync(acceptedRun).WaitAsync(Timeout);
                }
                catch (Exception failure) when (failure.GetBaseException() is SqliteException)
                {
                    // Expected driver failure. Timeouts/other uncertain exits must not permit disposal.
                }

            }

            store.Dispose();
            client.Dispose();
            database.Dispose();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WriteAndCleanupFailureResponse(
        TaskCompletionSource disposed, [EnumeratorCancellation] CancellationToken token)
    {
        token.Register(() => throw new InvalidOperationException(CancellationCallbackFailureMessage));
        try
        {
            yield return Text(new string('x', 16 * 1024));
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        }
        finally
        {
            disposed.TrySetResult();
            throw new InvalidOperationException(EnumeratorDisposalFailureMessage);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WriteFailureResponse(TaskCompletionSource<bool> cleaned,
        [EnumeratorCancellation] CancellationToken token)
    {
        try
        {
            yield return Text("working");
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        }
        finally
        {
            cleaned.TrySetResult(token.IsCancellationRequested);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TextResponse(string text,
        [EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        for (int i = 0; i < 1_000; i++)
        {
            token.ThrowIfCancellationRequested();
            yield return Text(text[i].ToString());
        }

        // Deliberately split the surrogate pair across updates, then yield an oversized update.
        yield return Text(text[1_000..(16 * 1024)]);
        yield return Text(text[(16 * 1024)..]);
        yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> PausedResponse(TaskCompletionSource waiting,
        TaskCompletionSource release, [EnumeratorCancellation] CancellationToken token)
    {
        yield return Text("visible");
        waiting.TrySetResult();
        await release.Task.WaitAsync(token);
        yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> TextUpdatesResponse(IEnumerable<string> updates,
        [EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        foreach (string update in updates)
        {
            token.ThrowIfCancellationRequested();
            yield return Text(update);
        }

        yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ExactResponse(string text,
        [EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
        if (text.Length != 0)
        {
            yield return Text(text);
        }

        yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ReentrantBlockedCancellationResponse(
        PrimarySession session, TaskCompletionSource sourceEntered, TaskCompletionSource sourceRelease,
        TaskCompletionSource sourceExited, TaskCompletionSource<SessionSnapshot> callbackSnapshot,
        TaskCompletionSource callbackRelease,
        [EnumeratorCancellation] CancellationToken token)
    {
        // Do not dispose this registration in the iterator: enumerator cleanup must be
        // able to finish while the callback deliberately waits for its release barrier.
        token.Register(() =>
        {
            SessionSnapshot snapshot = session.ReadSnapshotAsync().WaitAsync(Timeout).GetAwaiter().GetResult();
            callbackSnapshot.TrySetResult(snapshot);
            callbackRelease.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
        });

        try
        {
            yield return Text("working");
            sourceEntered.TrySetResult();
            await sourceRelease.Task;
        }
        finally
        {
            sourceExited.TrySetResult();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> CancellationCallbackFailureResponse(
        TaskCompletionSource requestEntered, TaskCompletionSource providerExited,
        [EnumeratorCancellation] CancellationToken token)
    {
        var cancellationObserved = Signal();
        token.Register(() =>
        {
            cancellationObserved.TrySetResult();
            throw new InvalidOperationException(CancellationCallbackFailureMessage);
        });
        try
        {
            yield return Text("working");
            requestEntered.TrySetResult();
            await cancellationObserved.Task;
        }
        finally
        {
            providerExited.TrySetResult();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FailedResponse(string failureScenario,
        [EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
        yield return Text("partial");
        if (failureScenario == "length")
        {
            yield return new() { FinishReason = ChatFinishReason.Length };
        }

        if (failureScenario == "exception-after-stop")
        {
            yield return new() { FinishReason = ChatFinishReason.Stop };
        }

        if (failureScenario.StartsWith("exception", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Scripted request failure.");
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> HeldCleanupResponse(TaskCompletionSource waiting,
        TaskCompletionSource cleanupEntered, TaskCompletionSource cleanupRelease,
        [EnumeratorCancellation] CancellationToken token)
    {
        try
        {
            yield return Text("working");
            waiting.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        }
        finally
        {
            cleanupEntered.TrySetResult();
            await cleanupRelease.Task;
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> CompleteResponse(
        [EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
        yield return Text("done");
        yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    private static ChatResponseUpdate Text(string text) => new()
    {
        Role = ChatRole.Assistant,
        Contents = [new TextContent(text)]
    };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static IEnumerable<Exception> FlattenFailures(Exception failure)
    {
        if (failure is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.SelectMany(FlattenFailures);
        }

        if (failure.InnerException is { } inner)
        {
            return FlattenFailures(inner);
        }

        return [failure];
    }

    private static async Task AssertCancellationCallbackFailureAsync(Task operation)
    {
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            async () => await operation.WaitAsync(Timeout));
        Assert.Contains(failure.Flatten().InnerExceptions,
            error => error is InvalidOperationException && error.Message == CancellationCallbackFailureMessage);
    }

    private static async Task WaitForOutputAsync(TemporaryDatabase database)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        while (ReadOutput(database).Count == 0)
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static List<(byte[] Bytes, bool Final)> ReadOutput(TemporaryDatabase database) =>
        ReadOutputDetails(database).Select(row => (row.Bytes, row.Final)).ToList();

    private static List<(Guid RequestId, Guid ResponseId, long ChunkIndex, byte[] Bytes, bool Final)>
        ReadOutputDetails(TemporaryDatabase database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.request_id, q.output_id, o.batch_index, o.text_utf8, o.is_final
            FROM execution_output o JOIN execution_requests q ON q.request_id = o.request_id
            ORDER BY o.request_id, o.batch_index;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var output = new List<(Guid RequestId, Guid ResponseId, long ChunkIndex, byte[] Bytes, bool Final)>();
        while (reader.Read())
        {
            output.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
                (byte[])reader.GetValue(3), reader.GetBoolean(4)));
        }

        return output;
    }

    private sealed class ExecutionFixture : IAsyncDisposable
    {
        private readonly SessionStore _store;
        private readonly ScriptedChatClient _client;
        private readonly List<Guid> _runs = [];

        internal ExecutionFixture(ScriptedChatClient client)
        {
            _client = client;
            Database = new TemporaryDatabase();
            _store = SessionStore.Open(Database.Path);
            Session = new PrimarySession(_store.CreatePrimary(Guid.NewGuid()), _store, client);
        }

        internal TemporaryDatabase Database { get; }
        internal PrimarySession Session { get; }

        internal async Task<Guid> StartRunAsync(string text)
        {
            InputReceipt receipt = await Session.SubmitUserInputAsync(new InputCommand(Guid.NewGuid(), 0, text));
            Assert.Equal(InputDecision.Accepted, receipt.Decision);
            Guid runId = Assert.IsType<Guid>(receipt.RunId);
            _runs.Add(runId);
            return runId;
        }

        public async ValueTask DisposeAsync()
        {
            // Even a failed assertion must not dispose the DB/client underneath owned work.
            foreach (Guid runId in _runs)
            {
                await Session.RequestStopAsync(runId);
                await Session.WaitForSettlementAsync(runId).WaitAsync(Timeout);
            }

            _store.Dispose();
            _client.Dispose();
            Database.Dispose();
        }
    }
}

internal sealed class ScriptedChatClient(Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> script) : IChatClient
{
    private int _calls, _disposeCalls;
    internal int Calls => Volatile.Read(ref _calls);
    internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
    internal IReadOnlyList<ChatMessage> LastRequest { get; private set; } = [];

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(DisposeCalls != 0, this);
        LastRequest = messages.ToArray();
        Interlocked.Increment(ref _calls);
        return script(cancellationToken);
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Streaming fake only.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() => Interlocked.Increment(ref _disposeCalls);
}
