using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class SessionIdentityTests {
    private static ScriptedChatClient IdentityClient()
        => new(_ => throw new InvalidOperationException("Identity operations must not start a request."));

    [Fact]
    public async Task CommittedIdentitySurvivesReopen() {
        using var database = new TemporaryDatabase();
        using var client = IdentityClient();
        PrimarySession created;
        using (SessionStore store = SessionStore.Open(database.Path)) {
            var manager = new SessionManager(store, client);
            created = await manager.CreatePrimaryAsync();
            Assert.NotEqual(Guid.Empty, created.Id);
            Assert.Equal(0L, created.HistoryRevision);
            Assert.Equal(created.Id, store.Sessions.Read(created.Id)!.Id);
        }

        Assert.Equal(3L, database.Scalar("PRAGMA user_version;"));
        Assert.Equal("wal", database.Scalar("PRAGMA journal_mode;"));
        using SessionStore reopened = SessionStore.Open(database.Path);
        var reopenedManager = new SessionManager(reopened, client);
        PrimarySession loaded = await reopenedManager.GetPrimaryAsync(created.Id);
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal(created.HistoryRevision, loaded.HistoryRevision);
        Assert.NotSame(created, loaded);
        Assert.Same(loaded, await reopenedManager.GetPrimaryAsync(created.Id));
    }

    [Fact]
    public async Task ManagerReturnsCanonicalHandleAndMissingLookupNeverInserts() {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        using var client = IdentityClient();
        var manager = new SessionManager(store, client);
        Guid missing = Guid.NewGuid();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.GetPrimaryAsync(missing));
        Assert.Null(store.Sessions.Read(missing));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sessions;"));

        PrimarySession created = await manager.CreatePrimaryAsync();
        Assert.Same(created, await manager.GetPrimaryAsync(created.Id));
        Assert.Equal(created.Id, store.Sessions.Read(created.Id)!.Id);
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sessions;"));
    }

    [Fact]
    public Task ConcurrentCreatesAndColdLookupsShareCanonicalHandles()
        => CheckConcurrentHandlesAsync().WaitAsync(TestTimeout);

    private static async Task CheckConcurrentHandlesAsync() {
        // Keep ownership here: a timed-out test wait must not dispose resources beneath callers.
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        SessionRecord saved = store.Sessions.CreatePrimary(Guid.NewGuid());
        using var client = IdentityClient();
        var manager = new SessionManager(store, client);
        var start = Signal();
        Task<(PrimarySession Created, PrimarySession Loaded)>[] tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => {
                await start.Task;
                PrimarySession created = await manager.CreatePrimaryAsync();
                PrimarySession loaded = await manager.GetPrimaryAsync(saved.Id);
                return (created, loaded);
            })).ToArray();
        start.SetResult();
        (PrimarySession Created, PrimarySession Loaded)[] results = await Task.WhenAll(tasks);

        Assert.Equal(8, results.Select(result => result.Created.Id).Distinct().Count());
        Assert.All(results, result => Assert.Same(results[0].Loaded, result.Loaded));
        Assert.Equal(saved.Id, results[0].Loaded.Id);
        Assert.Equal(9L, database.Scalar("SELECT COUNT(*) FROM sessions;"));
        foreach (var (created, _) in results) {
            Assert.Same(created, await manager.GetPrimaryAsync(created.Id));
        }
    }

    [Fact]
    public void HandleConstructionCopiesAlreadyLoadedIdentityAfterStoreCloses() {
        using var database = new TemporaryDatabase();
        Guid id = Guid.NewGuid();
        using (SessionStore store = SessionStore.Open(database.Path)) {
            store.Sessions.CreatePrimary(id);
        }

        database.Execute("UPDATE sessions SET history_revision = 7;");
        SessionRecord loaded;
        SessionStore borrowedStore;
        using (SessionStore store = SessionStore.Open(database.Path)) {
            borrowedStore = store;
            loaded = store.Sessions.Read(id)!;
        }

        using var client = IdentityClient();
        var session = new PrimarySession(loaded, borrowedStore, client);
        Assert.Equal(id, session.Id);
        Assert.Equal(7L, session.HistoryRevision);
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, client.DisposeCalls);
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sessions;"));
    }

    [Fact]
    public void DuplicateCreateDoesNotOverwriteSavedIdentity() {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        SessionRecord saved = store.Sessions.CreatePrimary(Guid.NewGuid());
        SqliteException error = Assert.Throws<SqliteException>(() => store.Sessions.CreatePrimary(saved.Id));
        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Equal(saved, store.Sessions.Read(saved.Id));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sessions;"));
    }

    [Fact]
    public void NewerSchemaIsRejectedAndAcquiredResourcesAreClosed() {
        using var database = new TemporaryDatabase();
        using (SessionStore store = SessionStore.Open(database.Path)) {
            store.Sessions.CreatePrimary(Guid.NewGuid());
        }

        database.Execute("PRAGMA user_version = 4;");
        Assert.Throws<NotSupportedException>(() => SessionStore.Open(database.Path));
        using (FileStream file = File.Open(database.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Assert.True(file.Length > 0);
        }

        Assert.Equal(4L, database.Scalar("PRAGMA user_version;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM sessions;"));
    }

    [Fact]
    public void UnversionedForeignDatabaseIsNotAdopted() {
        using var database = new TemporaryDatabase();
        database.Execute("CREATE TABLE unrelated (value TEXT); INSERT INTO unrelated VALUES ('retained');");
        Assert.Throws<NotSupportedException>(() => SessionStore.Open(database.Path));
        Assert.Equal(0L, database.Scalar("PRAGMA user_version;"));
        Assert.Equal("retained", database.Scalar("SELECT value FROM unrelated;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'sessions';"));
    }

    [Fact]
    public void DisposedStoreErrorsAreNotMistakenForAbsentIdentity() {
        using var database = new TemporaryDatabase();
        SessionStore store = SessionStore.Open(database.Path);
        store.Dispose();
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Sessions.Read(Guid.NewGuid()));
        Assert.Throws<ObjectDisposedException>(() => store.Sessions.CreatePrimary(Guid.NewGuid()));
    }
}
