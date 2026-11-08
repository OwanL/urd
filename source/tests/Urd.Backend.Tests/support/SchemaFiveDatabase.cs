using Urd.Core.Storage;

namespace Urd.Backend.Tests.Support;

// Frozen schema-five DDL and synthetic execution evidence, independent of the current initializer.
internal static class SchemaFiveDatabase {
  internal static Guid SessionId { get; } = Id(1);
  internal static Guid InputId { get; } = Id(11);
  internal static Guid RunId { get; } = Id(12);
  internal const string Original = "old\0 🙂";
  private const string EmptyObjectJson = "{}";

  internal static void Create(TemporaryDatabase database) => database.Execute($"""
    PRAGMA foreign_keys = ON;
    CREATE TABLE sessions (
        session_id TEXT NOT NULL PRIMARY KEY,
        history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
    ) STRICT;
    CREATE TABLE input_decisions (
        session_id TEXT NOT NULL REFERENCES sessions(session_id), input_id TEXT NOT NULL,
        expected_history_revision INTEGER NOT NULL CHECK (expected_history_revision >= 0),
        original_text_utf8 BLOB NOT NULL CHECK (length(original_text_utf8) <= 16384),
        decision INTEGER NOT NULL CHECK (decision IN (1, 2)), run_id TEXT,
        refusal_reason INTEGER CHECK (refusal_reason IN (1, 2, 3)), PRIMARY KEY (session_id, input_id),
        CHECK ((decision = 1 AND run_id IS NOT NULL AND run_id <> '00000000-0000-0000-0000-000000000000'
            AND refusal_reason IS NULL) OR (decision = 2 AND run_id IS NULL AND refusal_reason IS NOT NULL))
    ) STRICT;
    CREATE TABLE execution_runs (
        ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL REFERENCES sessions(session_id), run_id TEXT NOT NULL UNIQUE,
        history_revision INTEGER NOT NULL CHECK (history_revision >= 0), input_id TEXT NOT NULL,
        stopping INTEGER NOT NULL DEFAULT 0 CHECK (stopping IN (0, 1)), outcome INTEGER CHECK (outcome IN (0, 1, 2)),
        UNIQUE (session_id, input_id), FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id)
    ) STRICT;
    CREATE UNIQUE INDEX one_active_execution ON execution_runs(session_id) WHERE outcome IS NULL;
    CREATE INDEX execution_runs_history_order ON execution_runs(session_id, history_revision, ordinal DESC);
    CREATE INDEX execution_runs_terminal_order ON execution_runs(session_id, ordinal DESC) WHERE outcome IS NOT NULL;
    CREATE TABLE execution_requests (
        request_id TEXT PRIMARY KEY NOT NULL, run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
        request_index INTEGER NOT NULL DEFAULT 0 CHECK (request_index >= 0), output_id TEXT,
        next_batch_index INTEGER NOT NULL DEFAULT 0 CHECK (next_batch_index >= 0),
        output_final INTEGER NOT NULL DEFAULT 0 CHECK (output_final IN (0, 1)),
        outcome INTEGER CHECK (outcome IN (0, 1, 2)), error TEXT, UNIQUE (run_id, request_index)
    ) STRICT;
    CREATE UNIQUE INDEX one_unfinished_request ON execution_requests(run_id) WHERE outcome IS NULL;
    CREATE TABLE execution_output (
        request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
        batch_index INTEGER NOT NULL CHECK (batch_index >= 0),
        text_utf8 BLOB NOT NULL CHECK (length(text_utf8) <= 16384),
        is_final INTEGER NOT NULL CHECK (is_final IN (0, 1)), PRIMARY KEY (request_id, batch_index)
    ) STRICT;
    CREATE TABLE execution_inputs (
        ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL REFERENCES sessions(session_id), input_id TEXT NOT NULL,
        run_id TEXT NOT NULL REFERENCES execution_runs(run_id), request_id TEXT REFERENCES execution_requests(request_id),
        outcome INTEGER CHECK (outcome IN (0, 1, 2)), UNIQUE (session_id, input_id),
        FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id)
    ) STRICT;
    CREATE INDEX pending_execution_inputs ON execution_inputs(run_id, ordinal) WHERE request_id IS NULL AND outcome IS NULL;
    CREATE INDEX nonterminal_execution_inputs ON execution_inputs(run_id) WHERE outcome IS NULL;
    CREATE INDEX execution_inputs_request_order ON execution_inputs(request_id, ordinal) WHERE request_id IS NOT NULL;
    CREATE TABLE execution_messages (
        ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL REFERENCES sessions(session_id),
        history_revision INTEGER NOT NULL CHECK (history_revision >= 0), run_id TEXT NOT NULL REFERENCES execution_runs(run_id),
        role INTEGER NOT NULL CHECK (role IN (0, 1)), message_id TEXT NOT NULL, input_id TEXT,
        request_id TEXT REFERENCES execution_requests(request_id), UNIQUE (run_id, role, message_id),
        FOREIGN KEY (session_id, input_id) REFERENCES input_decisions(session_id, input_id),
        CHECK ((role = 0 AND input_id IS NOT NULL AND request_id IS NULL)
            OR (role = 1 AND input_id IS NULL AND request_id IS NOT NULL))
    ) STRICT;
    CREATE INDEX execution_messages_history_order ON execution_messages(session_id, history_revision, ordinal DESC);
    CREATE TABLE execution_tools (
        run_id TEXT NOT NULL REFERENCES execution_runs(run_id), request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
        call_index INTEGER NOT NULL CHECK (call_index >= 0), call_id TEXT NOT NULL, name TEXT NOT NULL,
        arguments_json TEXT NOT NULL CHECK (length(CAST(arguments_json AS BLOB)) <= 16384),
        started INTEGER NOT NULL DEFAULT 0 CHECK (started IN (0, 1)), outcome INTEGER CHECK (outcome IN (0, 1, 2)),
        result_json TEXT CHECK (length(CAST(result_json AS BLOB)) <= 16384), error TEXT,
        PRIMARY KEY (request_id, call_id), UNIQUE (request_id, call_index), UNIQUE (run_id, call_id)
    ) STRICT;
    CREATE TABLE session_creation (
        ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL UNIQUE REFERENCES sessions(session_id) ON DELETE CASCADE
    ) STRICT;
    INSERT INTO sessions VALUES ('{SessionId:D}', 7);
    INSERT INTO session_creation (session_id) VALUES ('{SessionId:D}');
    INSERT INTO input_decisions VALUES ('{SessionId:D}', '{InputId:D}', 7,
        X'{Convert.ToHexString(StorageText.StrictUtf8.GetBytes(Original))}', 1, '{RunId:D}', NULL);
    INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, stopping, outcome)
    VALUES ('{SessionId:D}', '{RunId:D}', 7, '{InputId:D}', 1, 2);
    INSERT INTO execution_requests VALUES ('{Id(13):D}', '{RunId:D}', 0, '{Id(14):D}', 1, 1, 2, 'old error');
    INSERT INTO execution_output VALUES ('{Id(13):D}', 0, X'00F09F9982', 1);
    INSERT INTO execution_inputs (session_id, input_id, run_id, request_id, outcome)
    VALUES ('{SessionId:D}', '{InputId:D}', '{RunId:D}', '{Id(13):D}', 2);
    INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id, request_id)
    VALUES ('{SessionId:D}', 7, '{RunId:D}', 0, '{InputId:D}', '{InputId:D}', NULL),
        ('{SessionId:D}', 7, '{RunId:D}', 1, '{Id(14):D}', NULL, '{Id(13):D}');
    INSERT INTO execution_tools VALUES ('{RunId:D}', '{Id(13):D}', 0, 'call', 'fake',
        '{EmptyObjectJson}', 1, 2, '{EmptyObjectJson}', 'old tool error');
    PRAGMA user_version = 5;
    """);

  private static Guid Id(int value) => Guid.Parse($"{value:x8}-0000-0000-0000-000000000000");
}
