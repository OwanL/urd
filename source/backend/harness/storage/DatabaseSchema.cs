using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Creates the current pre-release schema; incompatible databases require a manual reset.
internal static class DatabaseSchema {
  private const int SchemaVersion = 17;

  internal static void Initialize(SqliteConnection connection) {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "PRAGMA foreign_keys = ON;";
    command.ExecuteNonQuery();
    command.CommandText = "PRAGMA user_version;";
    long version = (long)command.ExecuteScalar()!;
    if (version == 0)
      CreateCurrentSchema(connection);
    else if (version != SchemaVersion)
      throw new NotSupportedException(
          $"Session database schema version {version} is not supported (expected {SchemaVersion}). "
          + "To reset: stop Urd, delete urd.sqlite and its urd.sqlite-wal/urd.sqlite-shm files, then restart Urd.");

    command.CommandText = "PRAGMA journal_mode = WAL;";
    if (!string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("The session database could not enable WAL mode.");

    command.CommandText = "PRAGMA synchronous = NORMAL;";
    command.ExecuteNonQuery();
  }

  private static void CreateCurrentSchema(SqliteConnection connection) {
    using SqliteTransaction transaction = connection.BeginTransaction();
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*');";
    if ((long)command.ExecuteScalar()! != 0)
      throw new NotSupportedException(
          "An unversioned, nonempty database cannot be initialized as a session store. "
          + "To reset: stop Urd, delete urd.sqlite and its urd.sqlite-wal/urd.sqlite-shm files, then restart Urd.");

    command.CommandText = """
      CREATE TABLE sessions (
          session_id TEXT NOT NULL PRIMARY KEY,
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
          working_directory TEXT,
          position_version INTEGER NOT NULL DEFAULT 0 CHECK (position_version >= 0)
      ) STRICT;
      CREATE TABLE session_starting_context (
          session_id TEXT NOT NULL PRIMARY KEY REFERENCES sessions(session_id) ON DELETE CASCADE,
          context_json TEXT NOT NULL
      ) STRICT;
      CREATE TABLE session_creation (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL UNIQUE REFERENCES sessions(session_id) ON DELETE CASCADE
      ) STRICT;
      CREATE TABLE input_decisions (
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          input_id TEXT NOT NULL,
          expected_history_revision INTEGER NOT NULL CHECK (expected_history_revision >= 0),
          original_text_utf8 BLOB NOT NULL,
          decision INTEGER NOT NULL CHECK (decision IN (1, 2)),
          run_id TEXT,
          refusal_reason INTEGER CHECK (refusal_reason IN (1, 2, 3, 4)),
          llm_selection_json TEXT NOT NULL,
          PRIMARY KEY (session_id, input_id),
          CHECK (
              (decision = 1 AND run_id IS NOT NULL
                  AND run_id <> '00000000-0000-0000-0000-000000000000' AND refusal_reason IS NULL)
              OR (decision = 2 AND run_id IS NULL AND refusal_reason IS NOT NULL)
          )
      ) STRICT;
      CREATE TABLE execution_runs (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          run_id TEXT NOT NULL UNIQUE,
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
          input_id TEXT,
          compaction_selection_json TEXT,
          compaction_focus TEXT,
          error TEXT,
          stopping INTEGER NOT NULL DEFAULT 0 CHECK (stopping IN (0, 1)),
          outcome INTEGER CHECK (outcome IN (0, 1, 2)),
          UNIQUE (session_id, input_id),
          FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id),
          CHECK ((input_id IS NOT NULL AND compaction_selection_json IS NULL)
              OR (input_id IS NULL AND compaction_selection_json IS NOT NULL))
      ) STRICT;
      CREATE TABLE execution_requests (
          request_id TEXT PRIMARY KEY NOT NULL,
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          request_index INTEGER NOT NULL DEFAULT 0 CHECK (request_index >= 0),
          attempt INTEGER NOT NULL DEFAULT 1 CHECK (attempt IN (1, 2)),
          usage_json TEXT,
          compaction_id TEXT REFERENCES session_compactions(compaction_id),
          response_id TEXT,
          next_chunk_index INTEGER NOT NULL DEFAULT 0 CHECK (next_chunk_index >= 0),
          response_final INTEGER NOT NULL DEFAULT 0 CHECK (response_final IN (0, 1)),
          outcome INTEGER CHECK (outcome IN (0, 1, 2)),
          error TEXT,
          UNIQUE (run_id, request_index)
      ) STRICT;
      CREATE TABLE execution_output (
          request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
          chunk_index INTEGER NOT NULL CHECK (chunk_index >= 0),
          text_utf8 BLOB NOT NULL CHECK (length(text_utf8) <= 16384),
          is_final INTEGER NOT NULL CHECK (is_final IN (0, 1)),
          PRIMARY KEY (request_id, chunk_index)
      ) STRICT;
      CREATE TABLE execution_inputs (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          input_id TEXT NOT NULL,
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          request_id TEXT REFERENCES execution_requests(request_id),
          outcome INTEGER CHECK (outcome IN (0, 1, 2)),
          not_sent INTEGER NOT NULL DEFAULT 0 CHECK (not_sent IN (0, 1)),
          UNIQUE (session_id, input_id),
          FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id)
      ) STRICT;
      CREATE TABLE execution_messages (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          role INTEGER NOT NULL CHECK (role IN (0, 1, 2, 3, 4)),
          message_id TEXT NOT NULL,
          input_id TEXT,
          request_id TEXT REFERENCES execution_requests(request_id),
          first_chunk_index INTEGER NOT NULL DEFAULT 0 CHECK (first_chunk_index >= 0),
          end_chunk_index INTEGER,
          created_at TEXT,
          call_id TEXT,
          tool_name TEXT,
          layout_weight INTEGER NOT NULL DEFAULT 256 CHECK (layout_weight > 0),
          UNIQUE (run_id, role, message_id),
          UNIQUE (role, request_id, call_id),
          FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id),
          CHECK (end_chunk_index IS NULL OR end_chunk_index >= first_chunk_index),
          CHECK ((role = 0 AND input_id IS NOT NULL AND request_id IS NOT NULL AND call_id IS NULL
                      AND tool_name IS NULL AND end_chunk_index IS NULL)
              OR (role = 1 AND input_id IS NULL AND request_id IS NOT NULL AND call_id IS NULL
                      AND tool_name IS NULL)
              OR (role IN (2, 3) AND input_id IS NULL AND request_id IS NOT NULL AND end_chunk_index IS NULL
                      AND call_id IS NOT NULL)
              OR (role = 4 AND input_id IS NULL AND request_id IS NULL
                      AND call_id IS NULL AND tool_name IS NULL AND end_chunk_index IS NULL))
      ) STRICT;
      CREATE TABLE transcript_position_totals (
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
          entry_count INTEGER NOT NULL CHECK (entry_count >= 0),
          total_weight INTEGER NOT NULL CHECK (total_weight >= 0),
          PRIMARY KEY (session_id, history_revision)
      ) STRICT;
      CREATE TABLE transcript_positions (
          entry_ordinal INTEGER PRIMARY KEY REFERENCES execution_messages(ordinal) ON DELETE CASCADE,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
          rank INTEGER NOT NULL CHECK (rank >= 0),
          kind INTEGER NOT NULL CHECK (kind IN (0, 1, 2, 3, 4)),
          weight INTEGER NOT NULL CHECK (weight > 0),
          prefix_weight INTEGER NOT NULL CHECK (prefix_weight >= 0),
          UNIQUE (session_id, history_revision, rank)
      ) STRICT;
      CREATE INDEX transcript_positions_weight ON transcript_positions(session_id, history_revision, prefix_weight);
      CREATE INDEX transcript_positions_prompts ON transcript_positions(session_id, history_revision, rank) WHERE kind = 0;
      -- All entry writers, including multi-input placement, publish positions atomically.
      CREATE TRIGGER transcript_position_append AFTER INSERT ON execution_messages BEGIN
          INSERT INTO transcript_position_totals (session_id, history_revision, entry_count, total_weight)
          VALUES (NEW.session_id, NEW.history_revision, 0, 0) ON CONFLICT DO NOTHING;
          INSERT INTO transcript_positions (entry_ordinal, session_id, history_revision, rank, kind, weight, prefix_weight)
          SELECT NEW.ordinal, NEW.session_id, NEW.history_revision, entry_count, NEW.role, NEW.layout_weight, total_weight
          FROM transcript_position_totals
          WHERE session_id = NEW.session_id AND history_revision = NEW.history_revision;
          UPDATE transcript_position_totals SET entry_count = entry_count + 1, total_weight = total_weight + NEW.layout_weight
          WHERE session_id = NEW.session_id AND history_revision = NEW.history_revision;
          UPDATE sessions SET position_version = position_version + 1 WHERE session_id = NEW.session_id;
      END;
      -- Streaming changes only the active tail: no suffix maintenance or historical correction API.
      CREATE TRIGGER transcript_position_tail AFTER UPDATE OF layout_weight ON execution_messages BEGIN
          SELECT RAISE(ABORT, 'Only the transcript tail weight can change.')
          WHERE EXISTS (SELECT 1 FROM transcript_positions p JOIN transcript_position_totals t
              ON t.session_id = p.session_id AND t.history_revision = p.history_revision
              WHERE p.entry_ordinal = NEW.ordinal AND p.rank <> t.entry_count - 1);
          UPDATE transcript_positions SET weight = NEW.layout_weight WHERE entry_ordinal = NEW.ordinal;
          UPDATE transcript_position_totals SET total_weight = total_weight + NEW.layout_weight - OLD.layout_weight
          WHERE session_id = NEW.session_id AND history_revision = NEW.history_revision;
          UPDATE sessions SET position_version = position_version + 1 WHERE session_id = NEW.session_id;
      END;
      CREATE TRIGGER transcript_position_revision AFTER UPDATE OF history_revision ON sessions
      WHEN NEW.history_revision <> OLD.history_revision BEGIN
          UPDATE sessions SET position_version = position_version + 1 WHERE session_id = NEW.session_id;
      END;
      CREATE TABLE execution_tools (
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
          call_index INTEGER NOT NULL CHECK (call_index >= 0),
          call_id TEXT NOT NULL,
          name TEXT NOT NULL,
          arguments_json TEXT NOT NULL,
          arguments_excerpt TEXT NOT NULL DEFAULT '{}',
          started INTEGER NOT NULL DEFAULT 0 CHECK (started IN (0, 1)),
          background INTEGER NOT NULL DEFAULT 0 CHECK (background IN (0, 1)),
          outcome INTEGER CHECK (outcome IN (0, 1, 2)),
          result_json TEXT,
          error TEXT,
          started_at TEXT,
          ended_at TEXT,
          PRIMARY KEY (request_id, call_id),
          UNIQUE (request_id, call_index),
          UNIQUE (run_id, call_id)
      ) STRICT;
      CREATE TABLE execution_tool_completions (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          completion_id TEXT NOT NULL UNIQUE,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
          request_id TEXT NOT NULL,
          call_id TEXT NOT NULL,
          created_at TEXT NOT NULL,
          claimed_request_id TEXT REFERENCES execution_requests(request_id),
          published INTEGER NOT NULL DEFAULT 0 CHECK (published IN (0, 1)),
          UNIQUE (request_id, call_id),
          FOREIGN KEY (request_id, call_id) REFERENCES execution_tools(request_id, call_id)
      ) STRICT;
      CREATE INDEX execution_tool_completions_undelivered
          ON execution_tool_completions(session_id, history_revision, ordinal) WHERE claimed_request_id IS NULL;
      CREATE INDEX execution_tool_completions_unpublished
          ON execution_tool_completions(session_id, history_revision, ordinal) WHERE published = 0;
      CREATE TABLE execution_provider_context (
          request_id TEXT PRIMARY KEY NOT NULL REFERENCES execution_requests(request_id),
          response_json TEXT NOT NULL
      ) STRICT;
      CREATE TABLE session_compactions (
          ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
          compaction_id TEXT NOT NULL UNIQUE,
          session_id TEXT NOT NULL REFERENCES sessions(session_id),
          history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
          run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
          first_kept_request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
          first_kept_message_index INTEGER NOT NULL CHECK (first_kept_message_index >= 0),
          first_kept_run_ordinal INTEGER NOT NULL,
          first_kept_request_index INTEGER NOT NULL CHECK (first_kept_request_index >= 0),
          reason INTEGER NOT NULL CHECK (reason IN (0, 1, 2)),
          tokens_before INTEGER NOT NULL CHECK (tokens_before >= 0),
          tokens_after INTEGER NOT NULL CHECK (tokens_after >= 0 AND tokens_after < tokens_before),
          created_at TEXT NOT NULL,
          summary_json TEXT NOT NULL
      ) STRICT;
      CREATE INDEX session_compactions_latest ON session_compactions(session_id, history_revision, ordinal DESC);
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
      CREATE TABLE model_settings (
          connection_id TEXT NOT NULL REFERENCES provider_connections(connection_id) ON DELETE CASCADE,
          model_id TEXT NOT NULL,
          family INTEGER NOT NULL CHECK (family IN (0, 1, 2, 3)),
          supports_images INTEGER NOT NULL CHECK (supports_images IN (0, 1)),
          context_window INTEGER CHECK (context_window IS NULL OR context_window > 0),
          PRIMARY KEY (connection_id, model_id)
      ) STRICT;
      CREATE TABLE provider_host (
          singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
          host_id TEXT NOT NULL
      ) STRICT;
      CREATE INDEX execution_runs_history_order ON execution_runs(session_id, history_revision, ordinal DESC);
      CREATE INDEX execution_runs_terminal_order ON execution_runs(session_id, ordinal DESC) WHERE outcome IS NOT NULL;
      CREATE UNIQUE INDEX one_active_execution ON execution_runs(session_id) WHERE outcome IS NULL;
      CREATE UNIQUE INDEX one_unfinished_request ON execution_requests(run_id) WHERE outcome IS NULL;
      CREATE INDEX pending_execution_inputs ON execution_inputs(run_id, ordinal) WHERE request_id IS NULL AND outcome IS NULL;
      CREATE INDEX nonterminal_execution_inputs ON execution_inputs(run_id) WHERE outcome IS NULL;
      CREATE INDEX execution_inputs_request_order ON execution_inputs(request_id, ordinal) WHERE request_id IS NOT NULL;
      CREATE INDEX execution_inputs_not_sent_order ON execution_inputs(session_id, ordinal) WHERE not_sent = 1;
      CREATE INDEX execution_messages_history_order ON execution_messages(session_id, history_revision, ordinal DESC);
      CREATE INDEX execution_messages_request_order ON execution_messages(request_id, role, first_chunk_index);
      PRAGMA user_version = 17;
      """;
    command.ExecuteNonQuery();
    transaction.Commit();
  }
}
