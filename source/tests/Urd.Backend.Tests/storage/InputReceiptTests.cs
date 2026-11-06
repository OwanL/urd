using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class InputReceiptTests
{
    [Fact]
    public void RefusalSurvivesLostReplyReopenAndChangedConditions()
    {
        using var database = new TemporaryDatabase();
        Guid sessionId = Guid.NewGuid();
        var command = new InputCommand(Guid.NewGuid(), 0, "original \r\n e\u0301 \0 🙂");
        using (SessionStore store = SessionStore.Open(database.Path))
        {
            store.Sessions.CreatePrimary(sessionId);
            // Deliberately discard the committed reply, as an uncertain caller would.
            store.Inputs.RecordRefusal(sessionId, command, InputRefusalReason.Capacity);
        }

        database.Execute("UPDATE sessions SET history_revision = 9;");
        using SessionStore reopened = SessionStore.Open(database.Path);
        InputReceipt receipt = reopened.Inputs.ReadReceipt(sessionId, command.InputId)!;
        Assert.Equal(sessionId, receipt.SessionId);
        Assert.Equal(command, receipt.Command);
        Assert.Equal(InputDecision.Refused, receipt.Decision);
        Assert.Null(receipt.RunId);
        Assert.Equal(InputRefusalReason.Capacity, receipt.RefusalReason);
        Assert.Equal(receipt, reopened.Inputs.RecordRefusal(sessionId, command, InputRefusalReason.Stopping));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM input_decisions;"));
        Assert.Equal(9L, reopened.Sessions.Read(sessionId)!.HistoryRevision);
    }

    [Fact]
    public void DelayedOriginalCannotReplaceFirstRefusal()
    {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
        var command = new InputCommand(Guid.NewGuid(), 0, "delayed original");
        InputReceipt retryReceipt = store.Inputs.RecordRefusal(sessionId, command, InputRefusalReason.Stopping);

        Assert.Equal(retryReceipt, store.Inputs.RecordRefusal(sessionId, command, InputRefusalReason.Capacity));
        Assert.Equal(retryReceipt, store.Inputs.ReadReceipt(sessionId, command.InputId));
        Assert.Equal(0L, store.Sessions.Read(sessionId)!.HistoryRevision);
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM input_decisions;"));
    }

    [Fact]
    public void ConflictingTextOrRevisionNeverResolvesOrOverwritesOriginal()
    {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
        var command = new InputCommand(Guid.NewGuid(), 0, "e\u0301\r\n");
        InputReceipt original = store.Inputs.RecordRefusal(sessionId, command, InputRefusalReason.Capacity);
        InputCommand[] conflicts =
        [
            command with { Text = "é\r\n" },
            command with { Text = "e\u0301\n" },
            command with { Text = command.Text + " " },
            command with { ExpectedHistoryRevision = 1 }
        ];

        foreach (InputCommand conflict in conflicts)
        {
            Assert.Throws<InvalidOperationException>(() =>
                store.Inputs.RecordRefusal(sessionId, conflict, InputRefusalReason.Stopping));
        }

        Assert.Equal(original, store.Inputs.ReadReceipt(sessionId, command.InputId));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM input_decisions;"));
    }

    [Fact]
    public void InputIdentityIsScopedToSessionAndMissingLookupDoesNotInsert()
    {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid first = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
        Guid second = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
        var command = new InputCommand(Guid.NewGuid(), 0, "first session");
        InputReceipt firstReceipt = store.Inputs.RecordRefusal(first, command, InputRefusalReason.Capacity);
        Assert.Null(store.Inputs.ReadReceipt(second, command.InputId));
        Assert.Null(store.Inputs.ReadReceipt(Guid.NewGuid(), command.InputId));
        InputReceipt secondReceipt = store.Inputs.RecordRefusal(second, command with { Text = "second session" }, InputRefusalReason.Stopping);
        Assert.NotEqual(firstReceipt, secondReceipt);
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM input_decisions;"));
    }

    [Fact]
    public void TransactionFailureLeavesNoPartialDecisionAndAllowsRetry()
    {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
        var command = new InputCommand(Guid.NewGuid(), 0, "rolled back");
        database.Execute("""
            CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
            CREATE TABLE test_child (
                parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED
            );
            CREATE TRIGGER fail_decision AFTER INSERT ON input_decisions
            BEGIN
                UPDATE sessions SET history_revision = 17;
                INSERT INTO test_child VALUES (1);
            END;
            """);

        // The insert succeeds; the deferred foreign key makes COMMIT fail.
        SqliteException error = Assert.Throws<SqliteException>(() => store.Inputs.RecordRefusal(sessionId, command, InputRefusalReason.Capacity));
        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Null(store.Inputs.ReadReceipt(sessionId, command.InputId));
        Assert.Equal(0L, store.Sessions.Read(sessionId)!.HistoryRevision);
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;"));
        database.Execute("DROP TRIGGER fail_decision;");
        InputReceipt receipt = store.Inputs.RecordRefusal(sessionId, command, InputRefusalReason.Stopping);
        Assert.Equal(InputRefusalReason.Stopping, receipt.RefusalReason);
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM input_decisions;"));
    }

    [Fact]
    public void LookupErrorsAreNotReportedAsAbsence()
    {
        using var database = new TemporaryDatabase();
        SessionStore store = SessionStore.Open(database.Path);
        Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
        var command = new InputCommand(Guid.NewGuid(), 0, "error distinction");
        Assert.Null(store.Inputs.ReadReceipt(sessionId, command.InputId));
        database.Execute("DROP TABLE input_decisions;");
        Assert.Throws<SqliteException>(() => store.Inputs.ReadReceipt(sessionId, command.InputId));
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Inputs.ReadReceipt(sessionId, command.InputId));
        Assert.Throws<ObjectDisposedException>(() => store.Inputs.RecordRefusal(sessionId, command, InputRefusalReason.Capacity));
    }

    [Fact]
    public void InvalidOrMissingSessionCommandsCreateNoDecision()
    {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
        var command = new InputCommand(Guid.NewGuid(), 0, "valid");
        Assert.Throws<KeyNotFoundException>(() => store.Inputs.RecordRefusal(Guid.NewGuid(), command, InputRefusalReason.Capacity));
        Assert.Throws<ArgumentException>(() => store.Inputs.RecordRefusal(sessionId, command with { InputId = Guid.Empty }, InputRefusalReason.Capacity));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Inputs.RecordRefusal(sessionId, command with { ExpectedHistoryRevision = -1 }, InputRefusalReason.Capacity));
        Assert.ThrowsAny<ArgumentException>(() => store.Inputs.RecordRefusal(sessionId, command with { Text = "\ud800" }, InputRefusalReason.Capacity));
        Assert.Throws<ArgumentException>(() => store.Inputs.RecordRefusal(sessionId, command with { Text = new string('é', 8193) }, InputRefusalReason.Capacity));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Inputs.RecordRefusal(sessionId, command, (InputRefusalReason)99));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM input_decisions;"));

        InputReceipt boundary = store.Inputs.RecordRefusal(sessionId, command with { Text = new string('é', 8192) }, InputRefusalReason.Capacity);
        Assert.Equal(8192, boundary.Command.Text.Length);
        Assert.Equal(boundary, store.Inputs.ReadReceipt(sessionId, command.InputId));
    }

    [Fact]
    public void VersionOneIdentityDatabaseMigratesWithoutChangingIdentity()
    {
        using var database = new TemporaryDatabase();
        Guid sessionId = Guid.NewGuid();
        database.Execute($"""
            CREATE TABLE sessions (
                session_id TEXT NOT NULL PRIMARY KEY,
                history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
            ) STRICT;
            INSERT INTO sessions VALUES ('{sessionId:D}', 7);
            PRAGMA user_version = 1;
            """);
        using (SessionStore store = SessionStore.Open(database.Path))
        {
            Assert.Equal(7L, store.Sessions.Read(sessionId)!.HistoryRevision);
            Assert.Null(store.Inputs.ReadReceipt(sessionId, Guid.NewGuid()));
        }

        Assert.Equal(3L, database.Scalar("PRAGMA user_version;"));
        using SessionStore reopened = SessionStore.Open(database.Path);
        Assert.Equal(7L, reopened.Sessions.Read(sessionId)!.HistoryRevision);
    }

    [Fact]
    public void FailedVersionOneMigrationRollsBackSchemaAndKeepsIdentity()
    {
        using var database = new TemporaryDatabase();
        database.Execute("""
            CREATE TABLE sessions (
                session_id TEXT NOT NULL PRIMARY KEY,
                history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
            ) STRICT;
            INSERT INTO sessions VALUES ('00000000-0000-0000-0000-000000000001', 7);
            CREATE TABLE execution_runs (unrelated TEXT);
            INSERT INTO execution_runs VALUES ('retained');
            PRAGMA user_version = 1;
            """);

        Assert.Throws<SqliteException>(() => SessionStore.Open(database.Path));
        Assert.Equal(1L, database.Scalar("PRAGMA user_version;"));
        Assert.Equal(7L, database.Scalar("SELECT history_revision FROM sessions;"));
        Assert.Equal("retained", database.Scalar("SELECT unrelated FROM execution_runs;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'input_decisions';"));
    }

    [Fact]
    public async Task ConcurrentStoresRetainOneFirstDecision()
    {
        using var database = new TemporaryDatabase();
        using SessionStore first = SessionStore.Open(database.Path);
        using SessionStore second = SessionStore.Open(database.Path);
        Guid sessionId = first.Sessions.CreatePrimary(Guid.NewGuid()).Id;
        var command = new InputCommand(Guid.NewGuid(), 0, "same command");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<InputReceipt>[] tasks = new[] { first, second }.Select((store, index) => Task.Run(async () =>
        {
            await start.Task;
            return store.Inputs.RecordRefusal(sessionId, command, index == 0 ? InputRefusalReason.Capacity : InputRefusalReason.Stopping);
        })).ToArray();
        start.SetResult();
        InputReceipt[] receipts = await Task.WhenAll(tasks);
        Assert.Equal(receipts[0], receipts[1]);
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM input_decisions;"));
    }
}
