using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Initializes connection pragmas and creates, verifies, or upgrades the schema.
internal static class DatabaseSchema {
  private const int SchemaVersion = 10;

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
    using SqliteCommand command = connection.CreateCommand();
    // SQLite's table-rebuild procedure requires this outside the transaction. Keep
    // child tables and their references intact; check every foreign key before commit.
    command.CommandText = "PRAGMA foreign_keys = OFF;";
    command.ExecuteNonQuery();
    try {
      using SqliteTransaction transaction = connection.BeginTransaction();
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
      EnsureProviderContextSchema(command, version);
      EnsureSelectionSchema(command, version);
      EnsureTranscriptSchema(connection, transaction, version);
      EnsureSendAttemptSchema(connection, transaction, version);
      if (version < SchemaVersion) {
        command.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
          throw new InvalidOperationException("The session database migration would violate a foreign key.");
      }

      transaction.Commit();
    } finally {
      command.Transaction = null;
      command.CommandText = "PRAGMA foreign_keys = ON;";
      command.ExecuteNonQuery();
    }
  }

  private static void EnsureSelectionSchema(SqliteCommand command, long version) {
    if (version >= 8) {
      command.CommandText = "SELECT llm_selection_json FROM input_decisions LIMIT 0;";
      command.ExecuteNonQuery();
      return;
    }

    // Widening a CHECK requires a rebuild, not ADD COLUMN alone. Never rename the
    // original table: SQLite would redirect execution foreign keys to that name.
    // input_decisions has no explicit indexes, triggers or views to recreate.
    command.CommandText = """
      CREATE TABLE input_decisions_v8 (
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          input_id TEXT NOT NULL,
          expected_history_revision INTEGER NOT NULL CHECK (expected_history_revision >= 0),
          original_text_utf8 BLOB NOT NULL CHECK (length(original_text_utf8) <= 16384),
          decision INTEGER NOT NULL CHECK (decision IN (1, 2)),
          run_id TEXT,
          refusal_reason INTEGER CHECK (refusal_reason IN (1, 2, 3, 4)),
          llm_selection_json TEXT,
          PRIMARY KEY (session_id, input_id),
          CHECK (
              (decision = 1 AND run_id IS NOT NULL
                  AND run_id <> '00000000-0000-0000-0000-000000000000' AND refusal_reason IS NULL)
              OR (decision = 2 AND run_id IS NULL AND refusal_reason IS NOT NULL)
          )
      ) STRICT;
      INSERT INTO input_decisions_v8 (
          session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason)
      SELECT session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason
      FROM input_decisions;
      DROP TABLE input_decisions;
      ALTER TABLE input_decisions_v8 RENAME TO input_decisions;
      PRAGMA user_version = 8;
      """;
    command.ExecuteNonQuery();
  }

  private static void EnsureTranscriptSchema(SqliteConnection connection, SqliteTransaction transaction, long version) {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    if (version >= 9) {
      command.CommandText = "SELECT role, message_id, input_id, request_id, first_batch_index, end_batch_index, created_at, call_id, tool_name FROM execution_messages LIMIT 0;";
      command.ExecuteNonQuery();
      command.CommandText = "SELECT started_at, ended_at FROM execution_tools LIMIT 0;";
      command.ExecuteNonQuery();
      command.CommandText = "SELECT usage_json FROM execution_requests LIMIT 0;";
      command.ExecuteNonQuery();
      return;
    }

    command.CommandText = """
      CREATE TABLE input_decisions_v9 (
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          input_id TEXT NOT NULL,
          expected_history_revision INTEGER NOT NULL CHECK (expected_history_revision >= 0),
          original_text_utf8 BLOB NOT NULL,
          decision INTEGER NOT NULL CHECK (decision IN (1, 2)),
          run_id TEXT,
          refusal_reason INTEGER CHECK (refusal_reason IN (1, 2, 3, 4)),
          llm_selection_json TEXT,
          PRIMARY KEY (session_id, input_id),
          CHECK (
              (decision = 1 AND run_id IS NOT NULL
                  AND run_id <> '00000000-0000-0000-0000-000000000000' AND refusal_reason IS NULL)
              OR (decision = 2 AND run_id IS NULL AND refusal_reason IS NOT NULL)
          )
      ) STRICT;
      INSERT INTO input_decisions_v9
      SELECT session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id,
          refusal_reason, llm_selection_json
      FROM input_decisions;
      DROP TABLE input_decisions;
      ALTER TABLE input_decisions_v9 RENAME TO input_decisions;

      CREATE TABLE execution_tools_v9 (
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
          call_index INTEGER NOT NULL CHECK (call_index >= 0),
          call_id TEXT NOT NULL,
          name TEXT NOT NULL,
          arguments_json TEXT NOT NULL,
          started INTEGER NOT NULL DEFAULT 0 CHECK (started IN (0, 1)),
          outcome INTEGER CHECK (outcome IN (0, 1, 2)),
          result_json TEXT,
          error TEXT,
          started_at TEXT,
          ended_at TEXT,
          PRIMARY KEY (request_id, call_id),
          UNIQUE (request_id, call_index),
          UNIQUE (run_id, call_id)
      ) STRICT;
      INSERT INTO execution_tools_v9 (
          run_id, request_id, call_index, call_id, name, arguments_json, started, outcome, result_json, error)
      SELECT run_id, request_id, call_index, call_id, name, arguments_json, started, outcome, result_json, error
      FROM execution_tools;
      DROP TABLE execution_tools;
      ALTER TABLE execution_tools_v9 RENAME TO execution_tools;

      CREATE TABLE execution_messages_v9 (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          role INTEGER NOT NULL CHECK (role IN (0, 1, 2)),
          message_id TEXT NOT NULL,
          input_id TEXT,
          request_id TEXT REFERENCES execution_requests(request_id),
          first_batch_index INTEGER NOT NULL DEFAULT 0 CHECK (first_batch_index >= 0),
          end_batch_index INTEGER,
          created_at TEXT,
          call_id TEXT,
          tool_name TEXT,
          UNIQUE (run_id, role, message_id),
          UNIQUE (request_id, call_id),
          FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id),
          CHECK (end_batch_index IS NULL OR end_batch_index >= first_batch_index),
          CHECK ((role = 0 AND input_id IS NOT NULL AND request_id IS NULL AND call_id IS NULL
                      AND tool_name IS NULL AND end_batch_index IS NULL)
              OR (role = 1 AND input_id IS NULL AND request_id IS NOT NULL AND call_id IS NULL
                      AND tool_name IS NULL)
              OR (role = 2 AND input_id IS NULL AND request_id IS NOT NULL AND end_batch_index IS NULL
                      AND call_id IS NOT NULL))
      ) STRICT;
      """;
    command.ExecuteNonQuery();

    command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('execution_requests') WHERE name = 'usage_json';";
    if ((long)command.ExecuteScalar()! == 0) {
      command.CommandText = "ALTER TABLE execution_requests ADD COLUMN usage_json TEXT;";
      command.ExecuteNonQuery();
    }

    CopyTranscriptEntries(connection, transaction);

    command.CommandText = """
      DROP TABLE execution_messages;
      ALTER TABLE execution_messages_v9 RENAME TO execution_messages;
      CREATE INDEX execution_messages_history_order
          ON execution_messages(session_id, history_revision, ordinal DESC);
      CREATE INDEX execution_messages_request_order
          ON execution_messages(request_id, role, first_batch_index);
      PRAGMA user_version = 9;
      """;
    command.ExecuteNonQuery();
  }

  private static void EnsureSendAttemptSchema(SqliteConnection connection, SqliteTransaction transaction, long version) {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    if (version >= 10) {
      command.CommandText = "SELECT attempt FROM execution_requests LIMIT 0;";
      command.ExecuteNonQuery();
      command.CommandText = "SELECT not_sent FROM execution_inputs LIMIT 0;";
      command.ExecuteNonQuery();
      command.CommandText = "SELECT ordinal, request_id FROM execution_messages LIMIT 0;";
      command.ExecuteNonQuery();
      return;
    }

    command.CommandText = """
      SELECT MAX(COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'execution_messages'), 0),
          COALESCE((SELECT MAX(ordinal) FROM execution_messages), 0));
      """;
    long highWater = (long)command.ExecuteScalar()!;

    // Attempt: 0 = legacy unknown, 1 = known not attempted, 2 = attempted (not proof of delivery).
    // New claims explicitly write 1; existing requests retain unknown provenance.
    command.CommandText = """
      ALTER TABLE execution_requests ADD COLUMN attempt INTEGER NOT NULL DEFAULT 0 CHECK (attempt IN (0, 1, 2));
      ALTER TABLE execution_inputs ADD COLUMN not_sent INTEGER NOT NULL DEFAULT 0 CHECK (not_sent IN (0, 1));
      CREATE INDEX execution_inputs_not_sent_order ON execution_inputs(session_id, ordinal) WHERE not_sent = 1;

      CREATE TABLE execution_messages_v10 (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          role INTEGER NOT NULL CHECK (role IN (0, 1, 2)),
          message_id TEXT NOT NULL,
          input_id TEXT,
          request_id TEXT REFERENCES execution_requests(request_id),
          first_batch_index INTEGER NOT NULL DEFAULT 0 CHECK (first_batch_index >= 0),
          end_batch_index INTEGER,
          created_at TEXT,
          call_id TEXT,
          tool_name TEXT,
          UNIQUE (run_id, role, message_id),
          UNIQUE (request_id, call_id),
          FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id),
          CHECK (end_batch_index IS NULL OR end_batch_index >= first_batch_index),
          CHECK ((role = 0 AND input_id IS NOT NULL AND call_id IS NULL
                      AND tool_name IS NULL AND end_batch_index IS NULL)
              OR (role = 1 AND input_id IS NULL AND request_id IS NOT NULL AND call_id IS NULL
                      AND tool_name IS NULL)
              OR (role = 2 AND input_id IS NULL AND request_id IS NOT NULL AND end_batch_index IS NULL
                      AND call_id IS NOT NULL))
      ) STRICT;
      INSERT INTO execution_messages_v10 (
          ordinal, session_id, history_revision, run_id, role, message_id, input_id, request_id,
          first_batch_index, end_batch_index, created_at, call_id, tool_name)
      SELECT ordinal, session_id, history_revision, run_id, role, message_id, input_id, request_id,
          first_batch_index, end_batch_index, created_at, call_id, tool_name
      FROM execution_messages ORDER BY ordinal;
      DROP TABLE execution_messages;
      ALTER TABLE execution_messages_v10 RENAME TO execution_messages;
      CREATE INDEX execution_messages_history_order
          ON execution_messages(session_id, history_revision, ordinal DESC);
      CREATE INDEX execution_messages_request_order
          ON execution_messages(request_id, role, first_batch_index);
      """;
    command.ExecuteNonQuery();

    command.CommandText = """
      UPDATE sqlite_sequence SET seq = $high_water
          WHERE name = 'execution_messages' AND seq < $high_water;
      INSERT INTO sqlite_sequence (name, seq)
          SELECT 'execution_messages', $high_water
          WHERE $high_water > 0 AND NOT EXISTS (
              SELECT 1 FROM sqlite_sequence WHERE name = 'execution_messages');
      PRAGMA user_version = 10;
      """;
    command.Parameters.AddWithValue("$high_water", highWater);
    command.ExecuteNonQuery();
  }

  private static void CopyTranscriptEntries(SqliteConnection connection, SqliteTransaction transaction) {
    using SqliteCommand read = connection.CreateCommand();
    read.Transaction = transaction;
    // Anchor tool-only requests around neighboring text requests, but keep legacy ordinals authoritative
    // when request indexes contradict the recorded message order.
    read.CommandText = """
      SELECT session_id, history_revision, run_id, role, message_id, input_id, request_id,
          first_batch_index, end_batch_index, created_at, call_id, tool_name
      FROM (
          SELECT 0 AS position_group, m.ordinal AS position_order, 0 AS position_kind,
              0 AS tool_order, 0 AS call_order,
              m.session_id, m.history_revision, m.run_id, m.role, m.message_id, m.input_id, m.request_id,
              0 AS first_batch_index,
              CASE WHEN m.role = 1 AND (q.outcome IS NOT NULL OR q.output_final = 1
                      OR r.stopping = 1 OR r.outcome IS NOT NULL)
                  THEN q.next_batch_index END AS end_batch_index,
              NULL AS created_at, NULL AS call_id, NULL AS tool_name
          FROM execution_messages m
          LEFT JOIN execution_requests q ON q.request_id = m.request_id
          LEFT JOIN execution_runs r ON r.run_id = m.run_id
          UNION ALL
          SELECT 0, m.ordinal, 1, t.call_index, 0,
              r.session_id, r.history_revision, t.run_id, 2, NULL, NULL, t.request_id,
              0, NULL, NULL, t.call_id, t.name
          FROM execution_messages m
          JOIN execution_tools t ON t.request_id = m.request_id AND t.run_id = m.run_id
          JOIN execution_runs r ON r.run_id = t.run_id
          WHERE m.role = 1
          UNION ALL
          SELECT CASE WHEN next_message.ordinal IS NOT NULL OR previous_message.ordinal IS NOT NULL
                      OR user_message.ordinal IS NOT NULL THEN 0 ELSE 1 END,
              COALESCE(next_message.ordinal, previous_message.ordinal, user_message.ordinal, r.ordinal),
              CASE WHEN next_message.ordinal IS NOT NULL THEN -1 ELSE 2 END,
              q.request_index, t.call_index,
              r.session_id, r.history_revision, t.run_id, 2, NULL, NULL, t.request_id,
              0, NULL, NULL, t.call_id, t.name
          FROM execution_tools t
          JOIN execution_requests q ON q.request_id = t.request_id AND q.run_id = t.run_id
          JOIN execution_runs r ON r.run_id = t.run_id
          LEFT JOIN execution_messages next_message ON next_message.ordinal = (
              SELECT m.ordinal
              FROM execution_messages m
              JOIN execution_requests next_request ON next_request.request_id = m.request_id
              WHERE m.role = 1 AND m.run_id = t.run_id AND next_request.request_index > q.request_index
              ORDER BY next_request.request_index
              LIMIT 1
          )
          LEFT JOIN execution_messages previous_message ON previous_message.ordinal = (
              SELECT m.ordinal
              FROM execution_messages m
              JOIN execution_requests previous_request ON previous_request.request_id = m.request_id
              WHERE m.role = 1 AND m.run_id = t.run_id AND previous_request.request_index < q.request_index
              ORDER BY previous_request.request_index DESC
              LIMIT 1
          )
          LEFT JOIN execution_messages user_message ON user_message.ordinal = (
              SELECT MAX(m.ordinal) FROM execution_messages m
              WHERE m.role = 0 AND m.run_id = t.run_id
          )
          WHERE NOT EXISTS (
              SELECT 1 FROM execution_messages m
              WHERE m.role = 1 AND m.request_id = t.request_id AND m.run_id = t.run_id
          )
      )
      ORDER BY position_group, position_order, position_kind, tool_order, call_order;
      """;

    using SqliteDataReader reader = read.ExecuteReader();
    using SqliteCommand insert = connection.CreateCommand();
    insert.Transaction = transaction;
    insert.CommandText = """
      INSERT INTO execution_messages_v9 (
          session_id, history_revision, run_id, role, message_id, input_id, request_id,
          first_batch_index, end_batch_index, created_at, call_id, tool_name)
      VALUES ($session, $revision, $run, $role, $message, $input, $request,
          $first, $end, $created, $call, $name);
      """;
    SqliteParameter session = insert.Parameters.Add("$session", SqliteType.Text);
    SqliteParameter revision = insert.Parameters.Add("$revision", SqliteType.Integer);
    SqliteParameter run = insert.Parameters.Add("$run", SqliteType.Text);
    SqliteParameter role = insert.Parameters.Add("$role", SqliteType.Integer);
    SqliteParameter message = insert.Parameters.Add("$message", SqliteType.Text);
    SqliteParameter input = insert.Parameters.Add("$input", SqliteType.Text);
    SqliteParameter request = insert.Parameters.Add("$request", SqliteType.Text);
    SqliteParameter first = insert.Parameters.Add("$first", SqliteType.Integer);
    SqliteParameter end = insert.Parameters.Add("$end", SqliteType.Integer);
    SqliteParameter created = insert.Parameters.Add("$created", SqliteType.Text);
    SqliteParameter call = insert.Parameters.Add("$call", SqliteType.Text);
    SqliteParameter name = insert.Parameters.Add("$name", SqliteType.Text);

    while (reader.Read()) {
      int kind = reader.GetInt32(3);
      session.Value = reader.GetString(0);
      revision.Value = reader.GetInt64(1);
      run.Value = reader.GetString(2);
      role.Value = kind;
      message.Value = kind == 2 ? Guid.NewGuid().ToString("D") : reader.GetString(4);
      input.Value = reader.IsDBNull(5) ? DBNull.Value : reader.GetString(5);
      request.Value = reader.IsDBNull(6) ? DBNull.Value : reader.GetString(6);
      first.Value = reader.GetInt32(7);
      end.Value = reader.IsDBNull(8) ? DBNull.Value : reader.GetInt32(8);
      created.Value = reader.IsDBNull(9) ? DBNull.Value : reader.GetString(9);
      call.Value = reader.IsDBNull(10) ? DBNull.Value : reader.GetString(10);
      name.Value = reader.IsDBNull(11) ? DBNull.Value : reader.GetString(11);
      insert.ExecuteNonQuery();
    }
  }

  private static void EnsureProviderContextSchema(SqliteCommand command, long version) {
    if (version >= 7) {
      command.CommandText = "SELECT request_id, response_json FROM execution_provider_context LIMIT 0;";
      command.ExecuteNonQuery();
      return;
    }

    command.CommandText = """
      CREATE TABLE execution_provider_context (
          request_id TEXT PRIMARY KEY NOT NULL REFERENCES execution_requests(request_id),
          response_json TEXT NOT NULL
      ) STRICT;
      PRAGMA user_version = 7;
      """;
    command.ExecuteNonQuery();
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

    // Rebuild both sides of the request/output relationship together.
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
