using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Initializes connection pragmas and creates, verifies, or upgrades the schema.
internal static class DatabaseSchema {
  private const int SchemaVersion = 6;

  internal static void Initialize(SqliteConnection connection) {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "PRAGMA foreign_keys = ON;";
    command.ExecuteNonQuery();
    InitializeSchema(connection);
    command.CommandText = "PRAGMA journal_mode = WAL;";
    if (!string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("The session database could not enable WAL mode.");

    command.CommandText = "PRAGMA synchronous = NORMAL;";
    command.ExecuteNonQuery();
  }

  private static void InitializeSchema(SqliteConnection connection) {
    using SqliteTransaction transaction = connection.BeginTransaction();
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "PRAGMA user_version;";
    long version = (long)command.ExecuteScalar()!;
    if (version < 0 || version > SchemaVersion) {
      throw new NotSupportedException(
          $"Session database schema version {version} is not supported (expected {SchemaVersion}).");
    }

    EnsureSessionSchema(command, version);
    EnsureInputSchema(command, version);
    EnsureExecutionSchema(connection, transaction, create: version < 2);
    if (version < 3) {
      command.CommandText = """
        CREATE INDEX execution_runs_history_order
            ON execution_runs(session_id, history_revision, ordinal DESC);
        CREATE INDEX execution_runs_terminal_order
            ON execution_runs(session_id, ordinal DESC) WHERE outcome IS NOT NULL;
        PRAGMA user_version = 3;
        """;
      command.ExecuteNonQuery();
    }

    EnsureSteeringSchema(command, version);
    EnsureCreationSchema(command, version);
    EnsureProviderSchema(command, version);
    transaction.Commit();
  }

  private static void EnsureProviderSchema(SqliteCommand command, long version) {
    if (version >= 6) {
      command.CommandText = """
        SELECT connection_id, name, route, api, endpoint, authentication, generation, credential_version,
            registration_json, credentials_json, disconnecting, catalog_json FROM provider_connections LIMIT 0;
        """;
      command.ExecuteNonQuery();
      command.CommandText = "SELECT singleton, host_id FROM provider_host LIMIT 0;";
      command.ExecuteNonQuery();
      return;
    }

    command.CommandText = """
      CREATE TABLE provider_connections (
          connection_id TEXT PRIMARY KEY NOT NULL,
          name TEXT NOT NULL,
          route INTEGER NOT NULL CHECK (route IN (0, 1, 2)),
          api INTEGER NOT NULL CHECK (api IN (0, 1)),
          endpoint TEXT NOT NULL,
          authentication INTEGER NOT NULL CHECK (authentication IN (0, 1, 2)),
          generation INTEGER NOT NULL CHECK (generation >= 0),
          credential_version INTEGER NOT NULL CHECK (credential_version >= 0),
          registration_json TEXT,
          credentials_json TEXT,
          disconnecting INTEGER NOT NULL CHECK (disconnecting IN (0, 1)),
          catalog_json TEXT,
          CHECK (route <> 2 OR authentication IN (0, 1))
      ) STRICT;
      CREATE TABLE provider_host (
          singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
          host_id TEXT NOT NULL
      ) STRICT;
      PRAGMA user_version = 6;
      """;
    command.ExecuteNonQuery();
  }

  private static void EnsureCreationSchema(SqliteCommand command, long version) {
    if (version >= 5) {
      command.CommandText = "SELECT ordinal, session_id FROM session_creation LIMIT 0;";
      command.ExecuteNonQuery();
      return;
    }

    // Legacy identities have no creation chronology; give them a deterministic ID-based order.
    command.CommandText = """
      CREATE TABLE session_creation (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL UNIQUE REFERENCES sessions(session_id) ON DELETE CASCADE
      ) STRICT;
      INSERT INTO session_creation (session_id)
      SELECT session_id FROM sessions ORDER BY session_id COLLATE BINARY;
      PRAGMA user_version = 5;
      """;
    command.ExecuteNonQuery();
  }

  private static void EnsureSteeringSchema(SqliteCommand command, long version) {
    if (version >= 4) {
      command.CommandText = """
        SELECT request_index FROM execution_requests LIMIT 0;
        SELECT ordinal, session_id, input_id, run_id, request_id, outcome FROM execution_inputs LIMIT 0;
        SELECT ordinal, session_id, history_revision, run_id, role, message_id, input_id, request_id
        FROM execution_messages LIMIT 0;
        SELECT run_id, request_id, call_index, call_id, name, arguments_json, started, outcome, result_json, error
        FROM execution_tools LIMIT 0;
        """;
      command.ExecuteNonQuery();
      return;
    }

    // Rebuild both sides of the request/output relationship with foreign keys enabled.
    // The original tables remain intact until every row has been copied successfully.
    command.CommandText = """
      CREATE TABLE execution_requests_v4 (
          request_id TEXT PRIMARY KEY NOT NULL,
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          request_index INTEGER NOT NULL DEFAULT 0 CHECK (request_index >= 0),
          output_id TEXT,
          next_batch_index INTEGER NOT NULL DEFAULT 0 CHECK (next_batch_index >= 0),
          output_final INTEGER NOT NULL DEFAULT 0 CHECK (output_final IN (0, 1)),
          outcome INTEGER CHECK (outcome IN (0, 1, 2)),
          error TEXT,
          UNIQUE (run_id, request_index)
      ) STRICT;
      CREATE TABLE execution_output_v4 (
          request_id TEXT NOT NULL REFERENCES execution_requests_v4(request_id),
          batch_index INTEGER NOT NULL CHECK (batch_index >= 0),
          text_utf8 BLOB NOT NULL CHECK (length(text_utf8) <= 16384),
          is_final INTEGER NOT NULL CHECK (is_final IN (0, 1)),
          PRIMARY KEY (request_id, batch_index)
      ) STRICT;
      INSERT INTO execution_requests_v4
          (request_id, run_id, output_id, next_batch_index, output_final, outcome, error)
      SELECT request_id, run_id, output_id, next_batch_index, output_final, outcome, error
      FROM execution_requests;
      INSERT INTO execution_output_v4 SELECT * FROM execution_output;
      DROP TABLE execution_output;
      DROP TABLE execution_requests;
      ALTER TABLE execution_requests_v4 RENAME TO execution_requests;
      ALTER TABLE execution_output_v4 RENAME TO execution_output;
      CREATE UNIQUE INDEX one_unfinished_request ON execution_requests(run_id) WHERE outcome IS NULL;
      CREATE TABLE execution_inputs (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          input_id TEXT NOT NULL,
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          request_id TEXT REFERENCES execution_requests(request_id),
          outcome INTEGER CHECK (outcome IN (0, 1, 2)),
          UNIQUE (session_id, input_id),
          FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id)
      ) STRICT;
      CREATE INDEX pending_execution_inputs ON execution_inputs(run_id, ordinal)
          WHERE request_id IS NULL AND outcome IS NULL;
      CREATE INDEX nonterminal_execution_inputs ON execution_inputs(run_id) WHERE outcome IS NULL;
      CREATE INDEX execution_inputs_request_order ON execution_inputs(request_id, ordinal) WHERE request_id IS NOT NULL;
      CREATE TABLE execution_messages (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          role INTEGER NOT NULL CHECK (role IN (0, 1)),
          message_id TEXT NOT NULL,
          input_id TEXT,
          request_id TEXT REFERENCES execution_requests(request_id),
          UNIQUE (run_id, role, message_id),
          FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id),
          CHECK ((role = 0 AND input_id IS NOT NULL AND request_id IS NULL)
              OR (role = 1 AND input_id IS NULL AND request_id IS NOT NULL))
      ) STRICT;
      CREATE INDEX execution_messages_history_order
          ON execution_messages(session_id, history_revision, ordinal DESC);
      CREATE TABLE execution_tools (
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
          call_index INTEGER NOT NULL CHECK (call_index >= 0),
          call_id TEXT NOT NULL,
          name TEXT NOT NULL,
          arguments_json TEXT NOT NULL CHECK (length(CAST(arguments_json AS BLOB)) <= 16384),
          started INTEGER NOT NULL DEFAULT 0 CHECK (started IN (0, 1)),
          outcome INTEGER CHECK (outcome IN (0, 1, 2)),
          result_json TEXT CHECK (length(CAST(result_json AS BLOB)) <= 16384),
          error TEXT,
          PRIMARY KEY (request_id, call_id),
          UNIQUE (request_id, call_index),
          UNIQUE (run_id, call_id)
      ) STRICT;
      INSERT INTO execution_inputs (session_id, input_id, run_id, request_id, outcome)
      SELECT r.session_id, r.input_id, r.run_id, q.request_id, COALESCE(q.outcome, r.outcome)
      FROM execution_runs r LEFT JOIN execution_requests q ON q.run_id = r.run_id ORDER BY r.ordinal;
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id, request_id)
      SELECT session_id, history_revision, run_id, role, message_id, input_id, request_id FROM (
          SELECT r.ordinal AS run_ordinal, r.session_id, r.history_revision, r.run_id,
              0 AS role, r.input_id AS message_id, r.input_id, NULL AS request_id
          FROM execution_runs r
          UNION ALL
          SELECT r.ordinal, r.session_id, r.history_revision, r.run_id, 1, q.output_id, NULL, q.request_id
          FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
          WHERE q.output_id IS NOT NULL
      ) ORDER BY run_ordinal, role;
      PRAGMA user_version = 4;
      """;
    command.ExecuteNonQuery();
  }

  private static void EnsureSessionSchema(SqliteCommand command, long version) {
    if (version != 0) {
      command.CommandText = "SELECT session_id, history_revision FROM sessions LIMIT 0;";
      using SqliteDataReader reader = command.ExecuteReader();
      return;
    }

    command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*');";
    if ((long)command.ExecuteScalar()! != 0)
      throw new NotSupportedException("An unversioned, nonempty database cannot be initialized as a session store.");

    command.CommandText = """
      CREATE TABLE sessions (
          session_id TEXT NOT NULL PRIMARY KEY,
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
      ) STRICT;
      """;
    command.ExecuteNonQuery();
  }

  private static void EnsureInputSchema(SqliteCommand command, long version) {
    if (version >= 2) {
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

  private static void EnsureExecutionSchema(SqliteConnection connection, SqliteTransaction transaction, bool create) {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    if (!create) {
      command.CommandText = """
        SELECT ordinal, session_id, run_id, history_revision, input_id, stopping, outcome FROM execution_runs LIMIT 0;
        SELECT request_id, run_id, output_id, next_batch_index, output_final, outcome, error FROM execution_requests LIMIT 0;
        SELECT request_id, batch_index, text_utf8, is_final FROM execution_output LIMIT 0;
        """;
      command.ExecuteNonQuery();
      return;
    }

    // Storage columns call one response's chunks "batches": output_id is its response
    // identity and next_batch_index/batch_index are its chunk index.
    command.CommandText = """
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
      """;
    command.ExecuteNonQuery();
  }
}
