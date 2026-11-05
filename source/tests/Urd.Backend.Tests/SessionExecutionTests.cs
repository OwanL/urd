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

    [Fact]
    public async Task CoalescesTinyUpdatesAndSplitsOversizedTextAtUtf8ScalarBoundaries()
    {
        string text = new string('a', 16 * 1024 - 1) + "😀" + new string('b', 40_000) + "\0終";
        var client = new ScriptedChatClient(token => TextResponse(text, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.SubmitAsync("go");

        RunSettlement settlement = await fixture.Session.WaitForSettlementAsync(runId).WaitAsync(Timeout);
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

    [Fact]
    public async Task FlushesDeadlineWhileTheSingleNextSourceReadIsPaused()
    {
        var waiting = Signal();
        var release = Signal();
        var client = new ScriptedChatClient(token => PausedResponse(waiting, release, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.SubmitAsync("go");
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
    public async Task RequiresPositiveTerminalEvidenceAndAnOtherwiseSuccessfulRequest(string failure)
    {
        var client = new ScriptedChatClient(token => FailedResponse(failure, token));
        await using var fixture = new ExecutionFixture(client);
        Guid runId = await fixture.SubmitAsync("go");

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
        Guid runId = await fixture.SubmitAsync("first");
        try
        {
            await waiting.Task.WaitAsync(Timeout);
            await fixture.Session.RequestStopAsync(runId);
            await fixture.Session.RequestStopAsync(runId);
            await cleanupEntered.Task.WaitAsync(Timeout);
            Assert.False(fixture.Session.WaitForSettlementAsync(runId).IsCompleted);
            InputReceipt refused = await fixture.Session.SubmitAsync(new InputCommand(Guid.NewGuid(), 0, "too early"));
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
        Guid replacement = await fixture.SubmitAsync("second");
        Assert.NotEqual(runId, replacement);
        Assert.Equal(ExecutionOutcome.Completed,
            (await fixture.Session.WaitForSettlementAsync(replacement).WaitAsync(Timeout)).Outcome);
        Assert.Equal(2, client.Calls);
        Assert.Equal(0, client.DisposeCalls);
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
            InputReceipt receipt = await session.SubmitAsync(command);
            runId = receipt.RunId;
            Assert.Equal(InputDecision.Accepted, receipt.Decision);
            Guid acceptedRun = Assert.IsType<Guid>(runId);

            Exception failure = await Assert.ThrowsAnyAsync<Exception>(
                () => session.WaitForSettlementAsync(acceptedRun).WaitAsync(Timeout));
            Assert.Equal(19, Assert.IsType<SqliteException>(failure.GetBaseException()).SqliteErrorCode);
            Assert.True(await cleaned.Task.WaitAsync(Timeout));
            Assert.Equal(DBNull.Value, database.Scalar("SELECT outcome FROM execution_runs;"));
            Assert.Equal(DBNull.Value, database.Scalar("SELECT outcome FROM execution_requests;"));
            Assert.Empty(ReadOutput(database));
            InputReceipt refused = await session.SubmitAsync(new InputCommand(Guid.NewGuid(), 0, "replacement"));
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

    private static async IAsyncEnumerable<ChatResponseUpdate> FailedResponse(string failure,
        [EnumeratorCancellation] CancellationToken token)
    {
        await Task.CompletedTask;
        token.ThrowIfCancellationRequested();
        yield return Text("partial");
        if (failure == "length")
        {
            yield return new() { FinishReason = ChatFinishReason.Length };
        }

        if (failure == "exception-after-stop")
        {
            yield return new() { FinishReason = ChatFinishReason.Stop };
        }

        if (failure.StartsWith("exception", StringComparison.Ordinal))
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

    private static async Task WaitForOutputAsync(TemporaryDatabase database)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        while (ReadOutput(database).Count == 0)
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static List<(byte[] Bytes, bool Final)> ReadOutput(TemporaryDatabase database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT text_utf8, is_final FROM execution_output ORDER BY request_id, batch_index;";
        using SqliteDataReader reader = command.ExecuteReader();
        var output = new List<(byte[] Bytes, bool Final)>();
        while (reader.Read())
        {
            output.Add(((byte[])reader.GetValue(0), reader.GetBoolean(1)));
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

        internal async Task<Guid> SubmitAsync(string text)
        {
            InputReceipt receipt = await Session.SubmitAsync(new InputCommand(Guid.NewGuid(), 0, text));
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
