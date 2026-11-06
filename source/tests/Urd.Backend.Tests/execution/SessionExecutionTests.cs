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

namespace Urd.Backend.Tests.Execution;

// Run lifecycle: terminal evidence, stop and cleanup ordering, and failure fencing.
public sealed class SessionExecutionTests {
    private const string CancellationCallbackFailureMessage = "Scripted cancellation callback failure.";
    private const string EnumeratorDisposalFailureMessage = "Scripted enumerator disposal failure.";

    [Theory]
    [InlineData("eof")]
    [InlineData("length")]
    [InlineData("exception")]
    [InlineData("exception-after-stop")]
    public async Task RequiresPositiveTerminalEvidenceAndAnOtherwiseSuccessfulRequest(string failureScenario) {
        var client = new ScriptedChatClient(token => FailedResponse(failureScenario, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("go");

        Assert.Equal(ExecutionOutcome.Failed,
            (await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout)).Outcome);
        Assert.Equal((long)ExecutionOutcome.Failed, fixture.Database.Scalar("SELECT outcome FROM execution_requests;"));
        Assert.All(StoredOutput.Read(fixture.Database), row => Assert.False(row.Final));
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, client.DisposeCalls);
    }

    [Fact]
    public async Task StopWaitsForActualCleanupBeforeSettlementOrReplacementAndBorrowsClient() {
        var waiting = Signal();
        var cleanupEntered = Signal();
        var cleanupRelease = Signal();
        int request = 0;
        var client = new ScriptedChatClient(token => Interlocked.Increment(ref request) == 1
            ? HeldCleanupResponse(waiting, cleanupEntered, cleanupRelease, token)
            : Complete(token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.StartRunAsync("first");
        try {
            await waiting.Task.WaitAsync(TestTimeout);
            await fixture.Session.RequestStopAsync(runId);
            await fixture.Session.RequestStopAsync(runId);
            await cleanupEntered.Task.WaitAsync(TestTimeout);
            Assert.False(fixture.Session.WaitForSettlementAsync(runId).IsCompleted);
            InputReceipt refused = await fixture.Session.SubmitUserInputAsync(new InputCommand(Guid.NewGuid(), 0, "too early"));
            Assert.Equal(InputDecision.Refused, refused.Decision);
            Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);
            Assert.Null(refused.RunId);
            Assert.Equal(1, client.Calls);
        } finally {
            cleanupRelease.TrySetResult();
        }

        Assert.Equal(ExecutionOutcome.Cancelled,
            (await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout)).Outcome);
        Guid replacement = await fixture.StartRunAsync("second");
        Assert.NotEqual(runId, replacement);
        Assert.Equal(ExecutionOutcome.Completed,
            (await fixture.Session.WaitForSettlementAsync(replacement).WaitAsync(TestTimeout)).Outcome);
        Assert.Equal(2, client.Calls);
        Assert.Equal(0, client.DisposeCalls);
    }

    [Fact]
    public async Task StopJoinsReentrantCallbackOutsideGuardAndStaleStopCannotCancelReplacement() {
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
            : Paused(replacementEntered, replacementRelease, token));
        await using var fixture = new ExecutionFixture(client);
        session = fixture.Session;
        Guid runId = await fixture.StartRunAsync("first");
        Task<ExecutionResult> settlement = session.WaitForSettlementAsync(runId);
        Task firstStop = Task.CompletedTask;
        Task secondStop = Task.CompletedTask;
        try {
            await sourceEntered.Task.WaitAsync(TestTimeout);
            firstStop = session.RequestStopAsync(runId);
            SessionSnapshot callbackRead = await callbackSnapshot.Task.WaitAsync(TestTimeout);
            Assert.True(callbackRead.StopRequested);
            sourceRelease.TrySetResult();
            await sourceExited.Task.WaitAsync(TestTimeout);
            // Reach saved request completion, not merely the iterator's finally block,
            // while the run still owns the blocked cancellation callback.
            using (var deadline = new CancellationTokenSource(TestTimeout)) {
                while (fixture.Database.Scalar("SELECT outcome FROM execution_requests;") is DBNull) {
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
        } finally {
            sourceRelease.TrySetResult();
            callbackRelease.TrySetResult();
        }

        await firstStop.WaitAsync(TestTimeout);
        await secondStop.WaitAsync(TestTimeout);
        Assert.Equal(ExecutionOutcome.Cancelled, (await settlement.WaitAsync(TestTimeout)).Outcome);

        Guid replacement = await fixture.StartRunAsync("replacement");
        Task<ExecutionResult> replacementSettlement = session.WaitForSettlementAsync(replacement);
        try {
            await replacementEntered.Task.WaitAsync(TestTimeout);
            await session.RequestStopAsync(runId).WaitAsync(TestTimeout);
            Assert.False(replacementSettlement.IsCompleted);
        } finally {
            replacementRelease.TrySetResult();
        }

        Assert.Equal(ExecutionOutcome.Completed, (await replacementSettlement.WaitAsync(TestTimeout)).Outcome);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task CancellationCallbackFailureFencesUnsettledRunAfterProviderCleanup() {
        var requestEntered = Signal();
        var providerExited = Signal();
        var database = new TemporaryDatabase();
        var store = SessionStore.Open(database.Path);
        var client = new ScriptedChatClient(token => CancellationCallbackFailureResponse(
            requestEntered, providerExited, token));
        var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, client);
        Guid? runId = null;
        try {
            InputReceipt receipt = await session.SubmitUserInputAsync(new InputCommand(Guid.NewGuid(), 0, "go"));
            Assert.Equal(InputDecision.Accepted, receipt.Decision);
            runId = Assert.IsType<Guid>(receipt.RunId);
            Guid acceptedRun = runId.Value;
            await requestEntered.Task.WaitAsync(TestTimeout);

            Task stop = session.RequestStopAsync(acceptedRun);
            await providerExited.Task.WaitAsync(TestTimeout);
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
        } finally {
            if (runId is { } acceptedRun) {
                if (requestEntered.Task.IsCompletedSuccessfully) {
                    await AssertCancellationCallbackFailureAsync(session.RequestStopAsync(acceptedRun));
                    await providerExited.Task.WaitAsync(TestTimeout);
                    await AssertCancellationCallbackFailureAsync(session.WaitForSettlementAsync(acceptedRun));
                } else {
                    await session.RequestStopAsync(acceptedRun).WaitAsync(TestTimeout);
                    await session.WaitForSettlementAsync(acceptedRun).WaitAsync(TestTimeout);
                }
            }

            store.Dispose();
            client.Dispose();
            database.Dispose();
        }
    }

    [Fact]
    public async Task OutputWriteAndCleanupFailuresAreRetainedWhileRunRemainsFenced() {
        var database = new TemporaryDatabase();
        var store = SessionStore.Open(database.Path);
        var disposed = Signal();
        var client = new ScriptedChatClient(token => WriteAndCleanupFailureResponse(disposed, token));
        var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, client);
        Guid? runId = null;
        try {
            database.Execute("""
                CREATE TRIGGER reject_output BEFORE INSERT ON execution_output
                BEGIN SELECT RAISE(ABORT, 'Injected output write failure'); END;
                """);
            InputReceipt receipt = await session.SubmitUserInputAsync(new InputCommand(Guid.NewGuid(), 0, "go"));
            Assert.Equal(InputDecision.Accepted, receipt.Decision);
            runId = receipt.RunId;
            Guid acceptedRun = Assert.IsType<Guid>(runId);

            Exception failure = await Assert.ThrowsAnyAsync<Exception>(
                () => session.WaitForSettlementAsync(acceptedRun).WaitAsync(TestTimeout));
            Exception[] failures = FlattenFailures(failure).ToArray();
            Assert.Contains(failures, error => error.GetBaseException() is SqliteException sqlite &&
                sqlite.SqliteErrorCode == 19);
            Assert.Contains(failures, error => error.Message == CancellationCallbackFailureMessage);
            Assert.Contains(failures, error => error.Message == EnumeratorDisposalFailureMessage);
            await disposed.Task.WaitAsync(TestTimeout);

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
        } finally {
            if (runId is { } acceptedRun) {
                try {
                    await session.RequestStopAsync(acceptedRun).WaitAsync(TestTimeout);
                } catch (Exception failure) when (failure is not TimeoutException &&
                      FlattenFailures(failure).Any(error => error.Message == CancellationCallbackFailureMessage)) {
                }

                await disposed.Task.WaitAsync(TestTimeout);
                try {
                    await session.WaitForSettlementAsync(acceptedRun).WaitAsync(TestTimeout);
                } catch (Exception failure) when (failure is not TimeoutException &&
                      FlattenFailures(failure).Any(error => error.Message == EnumeratorDisposalFailureMessage)) {
                }
            }

            store.Dispose();
            client.Dispose();
            database.Dispose();
        }
    }

    [Fact]
    public async Task OutputWriteFailureCleansUpButFencesRatherThanSettlesOrReplacesTheRun() {
        var database = new TemporaryDatabase();
        var store = SessionStore.Open(database.Path);
        var cleaned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedChatClient(token => WriteFailureResponse(cleaned, token));
        var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, client);
        Guid? runId = null;
        var command = new InputCommand(Guid.NewGuid(), 0, "go");
        try {
            database.Execute("""
                CREATE TRIGGER reject_output BEFORE INSERT ON execution_output
                BEGIN SELECT RAISE(ABORT, 'Injected output write failure'); END;
                """);
            InputReceipt receipt = await session.SubmitUserInputAsync(command);
            runId = receipt.RunId;
            Assert.Equal(InputDecision.Accepted, receipt.Decision);
            Guid acceptedRun = Assert.IsType<Guid>(runId);

            Exception failure = await Assert.ThrowsAnyAsync<Exception>(
                () => session.WaitForSettlementAsync(acceptedRun).WaitAsync(TestTimeout));
            Assert.Equal(19, Assert.IsType<SqliteException>(failure.GetBaseException()).SqliteErrorCode);
            Assert.True(await cleaned.Task.WaitAsync(TestTimeout));
            SessionSnapshot snapshot = await session.ReadSnapshotAsync();
            Assert.True(snapshot.AdmissionFenced);
            Assert.Equal(acceptedRun, snapshot.UnsettledRunId);
            Assert.Null(snapshot.LastSettlement);
            Assert.Single(snapshot.Messages, row => row.Message.Role == MessageRole.User);
            Assert.DoesNotContain(snapshot.Messages, row => row.Message.Role == MessageRole.Assistant);
            Assert.Equal(DBNull.Value, database.Scalar("SELECT outcome FROM execution_runs;"));
            Assert.Equal(DBNull.Value, database.Scalar("SELECT outcome FROM execution_requests;"));
            Assert.Empty(StoredOutput.Read(database));
            InputReceipt refused = await session.SubmitUserInputAsync(new InputCommand(Guid.NewGuid(), 0, "replacement"));
            Assert.Equal(InputDecision.Refused, refused.Decision);
            Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);
            Assert.Null(refused.RunId);
            Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_runs;"));
            Assert.Equal(1, client.Calls);
            Assert.Equal(0, client.DisposeCalls);
        } finally {
            if (runId is { } acceptedRun) {
                // Stop is only a cleanup fallback, not recovery or proof of saved settlement.
                await session.RequestStopAsync(acceptedRun);
                await cleaned.Task.WaitAsync(TestTimeout);
                try {
                    await session.WaitForSettlementAsync(acceptedRun).WaitAsync(TestTimeout);
                } catch (Exception failure) when (failure.GetBaseException() is SqliteException) {
                    // Expected driver failure. Timeouts/other uncertain exits must not permit disposal.
                }

            }

            store.Dispose();
            client.Dispose();
            database.Dispose();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WriteAndCleanupFailureResponse(
        TaskCompletionSource disposed, [EnumeratorCancellation] CancellationToken token) {
        token.Register(() => throw new InvalidOperationException(CancellationCallbackFailureMessage));
        try {
            yield return Text(new string('x', 16 * 1024));
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        } finally {
            disposed.TrySetResult();
            throw new InvalidOperationException(EnumeratorDisposalFailureMessage);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WriteFailureResponse(TaskCompletionSource<bool> cleaned,
        [EnumeratorCancellation] CancellationToken token) {
        try {
            yield return Text("working");
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        } finally {
            cleaned.TrySetResult(token.IsCancellationRequested);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ReentrantBlockedCancellationResponse(
        PrimarySession session, TaskCompletionSource sourceEntered, TaskCompletionSource sourceRelease,
        TaskCompletionSource sourceExited, TaskCompletionSource<SessionSnapshot> callbackSnapshot,
        TaskCompletionSource callbackRelease,
        [EnumeratorCancellation] CancellationToken token) {
        // Do not dispose this registration in the iterator: enumerator cleanup must be
        // able to finish while the callback deliberately waits for its release barrier.
        token.Register(() => {
            SessionSnapshot snapshot = session.ReadSnapshotAsync().WaitAsync(TestTimeout).GetAwaiter().GetResult();
            callbackSnapshot.TrySetResult(snapshot);
            callbackRelease.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
        });

        try {
            yield return Text("working");
            sourceEntered.TrySetResult();
            await sourceRelease.Task;
        } finally {
            sourceExited.TrySetResult();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> CancellationCallbackFailureResponse(
        TaskCompletionSource requestEntered, TaskCompletionSource providerExited,
        [EnumeratorCancellation] CancellationToken token) {
        var cancellationObserved = Signal();
        token.Register(() => {
            cancellationObserved.TrySetResult();
            throw new InvalidOperationException(CancellationCallbackFailureMessage);
        });
        try {
            yield return Text("working");
            requestEntered.TrySetResult();
            await cancellationObserved.Task;
        } finally {
            providerExited.TrySetResult();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FailedResponse(string failureScenario,
        [EnumeratorCancellation] CancellationToken token) {
        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
        yield return Text("partial");
        if (failureScenario == "length") {
            yield return new() { FinishReason = ChatFinishReason.Length };
        }

        if (failureScenario == "exception-after-stop") {
            yield return new() { FinishReason = ChatFinishReason.Stop };
        }

        if (failureScenario.StartsWith("exception", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Scripted request failure.");
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> HeldCleanupResponse(TaskCompletionSource waiting,
        TaskCompletionSource cleanupEntered, TaskCompletionSource cleanupRelease,
        [EnumeratorCancellation] CancellationToken token) {
        try {
            yield return Text("working");
            waiting.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        } finally {
            cleanupEntered.TrySetResult();
            await cleanupRelease.Task;
        }
    }

    private static IEnumerable<Exception> FlattenFailures(Exception failure) {
        if (failure is AggregateException aggregate) {
            return aggregate.InnerExceptions.SelectMany(FlattenFailures);
        }

        if (failure.InnerException is { } inner) {
            return FlattenFailures(inner);
        }

        return [failure];
    }

    private static async Task AssertCancellationCallbackFailureAsync(Task operation) {
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            async () => await operation.WaitAsync(TestTimeout));
        Assert.Contains(failure.Flatten().InnerExceptions,
            error => error is InvalidOperationException && error.Message == CancellationCallbackFailureMessage);
    }

}
