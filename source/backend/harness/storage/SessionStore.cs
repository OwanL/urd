using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Opens the session database and exposes its focused storage operations.
public sealed class SessionStore : IDisposable {
    private readonly Database _database;

    private SessionStore(SqliteConnection connection) {
        _database = new(connection);
        Sessions = new(_database);
        Inputs = new(_database);
        Runs = new(_database);
        Output = new(_database);
        Content = new(_database);
        Snapshots = new(_database, Content);
    }

    internal SessionStorage Sessions { get; }
    internal InputStorage Inputs { get; }
    internal RunStorage Runs { get; }
    internal OutputStorage Output { get; }
    internal SnapshotReader Snapshots { get; }
    internal MessageContentReader Content { get; }

    public static SessionStore Open(string databasePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

        try {
            connection.Open();
            DatabaseSchema.Initialize(connection);
            return new SessionStore(connection);
        } catch {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose() => _database.Dispose();
}
