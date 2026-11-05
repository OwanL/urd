using System.Text;
using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

public sealed partial class SessionStore : IDisposable
{
    private const int SchemaVersion = 2;
    private const int MaximumInputBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
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
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();
            InitializeSchema(connection);
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

    // This creates only refusals; an identical retry returns either retained decision.
    // New acceptance needs work/run facts in the same transaction, in the admission owner.
    internal InputReceipt RecordInputRefusal(Guid sessionId, InputCommand command, InputRefusalReason reason)
    {
        ValidateInput(sessionId, command);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteTransaction transaction = _connection.BeginTransaction();
            InputReceipt? retained = ReadInputReceipt(sessionId, command.InputId, transaction);
            if (retained is not null)
            {
                if (retained.Command != command)
                {
                    throw new InvalidOperationException("The input ID already belongs to a different original command.");
                }

                transaction.Commit();
                return retained;
            }

            // Only the first decision examines current refusal conditions. In particular,
            // a retry must not replace the original reason with today's reason.
            if (!Enum.IsDefined(reason))
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            var receipt = new InputReceipt(sessionId, command, InputDecision.Refused, reason, null);
            InsertInputReceipt(transaction, receipt);
            transaction.Commit();
            return receipt;
        }
    }

    // The admission owner supplies the same transaction as its run/work writes.
    // This helper intentionally neither begins nor commits a transaction.
    private void InsertInputReceipt(SqliteTransaction transaction, InputReceipt receipt)
    {
        using SqliteCommand insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO input_decisions (
                session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason)
            SELECT session_id, $input, $revision, $text, $decision, $run, $reason
            FROM sessions WHERE session_id = $session;
            """;
        insert.Parameters.AddWithValue("$session", receipt.SessionId.ToString("D"));
        insert.Parameters.AddWithValue("$input", receipt.Command.InputId.ToString("D"));
        insert.Parameters.AddWithValue("$revision", receipt.Command.ExpectedHistoryRevision);
        // Length-bearing UTF-8 bytes preserve embedded NUL as well as exact Unicode.
        insert.Parameters.AddWithValue("$text", StrictUtf8.GetBytes(receipt.Command.Text));
        insert.Parameters.AddWithValue("$decision", (int)receipt.Decision);
        insert.Parameters.AddWithValue("$run", (object?)receipt.RunId?.ToString("D") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$reason", receipt.RefusalReason is { } reason ? (object)(int)reason : DBNull.Value);
        if (insert.ExecuteNonQuery() == 0)
        {
            throw new KeyNotFoundException($"Session {receipt.SessionId} does not exist.");
        }
    }

    internal InputReceipt? ReadInputReceipt(Guid sessionId, Guid inputId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadInputReceipt(sessionId, inputId, transaction: null);
        }
    }

    private InputReceipt? ReadInputReceipt(Guid sessionId, Guid inputId, SqliteTransaction? transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT expected_history_revision, original_text_utf8, decision, run_id, refusal_reason FROM input_decisions
            WHERE session_id = $session AND input_id = $input;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$input", inputId.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new InputReceipt(sessionId, new InputCommand(inputId, reader.GetInt64(0), StrictUtf8.GetString((byte[])reader.GetValue(1))),
                (InputDecision)reader.GetInt32(2),
                reader.IsDBNull(4) ? null : (InputRefusalReason)reader.GetInt32(4),
                reader.IsDBNull(3) ? null : reader.GetGuid(3))
            : null;
    }

    private static void ValidateInput(Guid sessionId, InputCommand command)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(command);
        if (command.InputId == Guid.Empty)
        {
            throw new ArgumentException("Input ID cannot be empty.", nameof(command));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(command.ExpectedHistoryRevision);
        ArgumentNullException.ThrowIfNull(command.Text);
        if (StrictUtf8.GetByteCount(command.Text) > MaximumInputBytes)
        {
            throw new ArgumentException($"Input text cannot exceed {MaximumInputBytes} UTF-8 bytes.", nameof(command));
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
        if (version < 0 || version > SchemaVersion)
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
                """;
            command.ExecuteNonQuery();
        }
        else
        {
            command.CommandText = "SELECT session_id, history_revision FROM sessions LIMIT 0;";
            using SqliteDataReader reader = command.ExecuteReader();
        }

        if (version < 2)
        {
            command.CommandText = """
                CREATE TABLE input_decisions (
                    session_id TEXT NOT NULL REFERENCES sessions(session_id),
                    input_id TEXT NOT NULL,
                    expected_history_revision INTEGER NOT NULL CHECK (expected_history_revision >= 0),
                    original_text_utf8 BLOB NOT NULL CHECK (length(original_text_utf8) <= 16384),
                    decision INTEGER NOT NULL CHECK (decision IN (1, 2)),
                    run_id TEXT,
                    refusal_reason INTEGER CHECK (refusal_reason IN (1, 2, 3)),
                    PRIMARY KEY (session_id, input_id),
                    CHECK (
                        (decision = 1 AND run_id IS NOT NULL
                            AND run_id <> '00000000-0000-0000-0000-000000000000' AND refusal_reason IS NULL)
                        OR (decision = 2 AND run_id IS NULL AND refusal_reason IS NOT NULL)
                    )
                ) STRICT;
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }
        else
        {
            command.CommandText = """
                SELECT session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason
                FROM input_decisions LIMIT 0;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
        }

        EnsureExecutionSchema(connection, transaction, create: version < 2);
        transaction.Commit();
    }
}
