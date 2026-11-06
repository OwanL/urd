using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Initializes connection pragmas and creates, verifies, or upgrades the schema.
internal static class DatabaseSchema
{
    private const int SchemaVersion = 3;

    internal static void Initialize(SqliteConnection connection)
    {
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
    }

    private static void InitializeSchema(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        long version = (long)command.ExecuteScalar()!;
        if (version < 0 || version > SchemaVersion)
        {
            throw new NotSupportedException(
                $"Session database schema version {version} is not supported (expected {SchemaVersion}).");
        }

        EnsureSessionSchema(command, version);
        EnsureInputSchema(command, version);
        EnsureExecutionSchema(connection, transaction, create: version < 2);
        if (version < 3)
        {
            command.CommandText = """
                CREATE INDEX execution_runs_history_order
                    ON execution_runs(session_id, history_revision, ordinal DESC);
                CREATE INDEX execution_runs_terminal_order
                    ON execution_runs(session_id, ordinal DESC) WHERE outcome IS NOT NULL;
                PRAGMA user_version = 3;
                """;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void EnsureSessionSchema(SqliteCommand command, long version)
    {
        if (version != 0)
        {
            command.CommandText = "SELECT session_id, history_revision FROM sessions LIMIT 0;";
            using SqliteDataReader reader = command.ExecuteReader();
            return;
        }

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

    private static void EnsureInputSchema(SqliteCommand command, long version)
    {
        if (version >= 2)
        {
            command.CommandText = """
                SELECT session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason
                FROM input_decisions LIMIT 0;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            return;
        }

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

    private static void EnsureExecutionSchema(SqliteConnection connection, SqliteTransaction transaction, bool create)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = create ? """
            CREATE TABLE execution_runs (
                ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL REFERENCES sessions(session_id),
                run_id TEXT NOT NULL UNIQUE,
                history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
                input_id TEXT NOT NULL,
                stopping INTEGER NOT NULL DEFAULT 0 CHECK (stopping IN (0, 1)),
                outcome INTEGER CHECK (outcome IN (0, 1, 2)),
                UNIQUE (session_id, input_id),
                FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id)
            ) STRICT;
            CREATE UNIQUE INDEX one_active_execution ON execution_runs(session_id) WHERE outcome IS NULL;
            CREATE TABLE execution_requests (
                request_id TEXT PRIMARY KEY NOT NULL,
                run_id TEXT NOT NULL UNIQUE REFERENCES execution_runs(run_id),
                output_id TEXT,
                next_batch_index INTEGER NOT NULL DEFAULT 0 CHECK (next_batch_index >= 0),
                output_final INTEGER NOT NULL DEFAULT 0 CHECK (output_final IN (0, 1)),
                outcome INTEGER CHECK (outcome IN (0, 1, 2)),
                error TEXT
            ) STRICT;
            CREATE TABLE execution_output (
                request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
                batch_index INTEGER NOT NULL CHECK (batch_index >= 0),
                text_utf8 BLOB NOT NULL CHECK (length(text_utf8) <= 16384),
                is_final INTEGER NOT NULL CHECK (is_final IN (0, 1)),
                PRIMARY KEY (request_id, batch_index)
            ) STRICT;
            """ : """
            SELECT ordinal, session_id, run_id, history_revision, input_id, stopping, outcome FROM execution_runs LIMIT 0;
            SELECT request_id, run_id, output_id, next_batch_index, output_final, outcome, error FROM execution_requests LIMIT 0;
            SELECT request_id, batch_index, text_utf8, is_final FROM execution_output LIMIT 0;
            """;
        command.ExecuteNonQuery();
    }
}
