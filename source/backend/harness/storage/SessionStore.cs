using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

public sealed class SessionStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    private SessionStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static SessionStore Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

        try
        {
            connection.Open();
            InitializeSchema(connection);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            if (!string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The session database could not enable WAL mode.");
            }

            command.CommandText = "PRAGMA synchronous = NORMAL;";
            command.ExecuteNonQuery();
            return new SessionStore(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal SessionRecord CreatePrimary(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using SqliteCommand command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sessions (session_id, history_revision) VALUES ($id, 0)
                RETURNING session_id, history_revision;
                """;
            command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            SessionRecord record;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                reader.Read();
                record = ReadRecord(reader);
            }

            transaction.Commit();
            return record;
        }
    }

    internal SessionRecord? ReadSession(Guid sessionId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT session_id, history_revision FROM sessions WHERE session_id = $id;";
            command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _connection.Dispose();
            _disposed = true;
        }
    }

    private static SessionRecord ReadRecord(SqliteDataReader reader)
        => new(reader.GetGuid(0), reader.GetInt64(1));

    private static void InitializeSchema(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        long version = (long)command.ExecuteScalar()!;
        if (version != 0 && version != SchemaVersion)
        {
            throw new NotSupportedException($"Session database schema version {version} is not supported (expected {SchemaVersion}).");
        }

        if (version == 0)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*');";
            if ((long)command.ExecuteScalar()! != 0)
            {
                throw new NotSupportedException("An unversioned, nonempty database cannot be initialized as a session store.");
            }

            command.CommandText = """
                CREATE TABLE sessions (
                    session_id TEXT NOT NULL PRIMARY KEY,
                    history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
                ) STRICT;
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }
        else
        {
            command.CommandText = "SELECT session_id, history_revision FROM sessions LIMIT 0;";
            using SqliteDataReader reader = command.ExecuteReader();
        }

        transaction.Commit();
    }
}
