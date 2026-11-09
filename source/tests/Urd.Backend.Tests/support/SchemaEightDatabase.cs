namespace Urd.Backend.Tests.Support;

// Frozen schema-eight DDL and transcript evidence, independent of the current initializer.
internal static class SchemaEightDatabase {
  internal static Guid SessionId { get; } = Id(1);
  internal static Guid FirstInputId { get; } = Id(11);
  internal static Guid SecondInputId { get; } = Id(12);
  internal static Guid SettledInputId { get; } = Id(21);
  internal static Guid ToolOnlyInputId { get; } = Id(31);
  internal static Guid ActiveRunId { get; } = Id(101);
  internal static Guid SettledRunId { get; } = Id(102);
  internal static Guid ToolOnlyRunId { get; } = Id(103);
  internal static Guid FirstRequestId { get; } = Id(111);
  internal static Guid SecondRequestId { get; } = Id(112);
  internal static Guid SettledRequestId { get; } = Id(121);
  internal static Guid FirstToolOnlyRequestId { get; } = Id(131);
  internal static Guid SecondToolOnlyRequestId { get; } = Id(132);
  internal static Guid FirstOutputId { get; } = Id(141);
  internal static Guid SecondOutputId { get; } = Id(142);
  internal static Guid SettledOutputId { get; } = Id(143);
  internal static Guid InterleavingInputId { get; } = Id(41);
  internal static Guid LaterInputId { get; } = Id(42);
  internal static Guid InterleavingRunId { get; } = Id(104);
  internal static Guid LaterRunId { get; } = Id(105);
  internal static Guid InterleavingFirstRequestId { get; } = Id(151);
  internal static Guid InterleavingToolOnlyRequestId { get; } = Id(152);
  internal static Guid InterleavingLastRequestId { get; } = Id(153);
  internal static Guid InterleavingFirstOutputId { get; } = Id(161);
  internal static Guid InterleavingLastOutputId { get; } = Id(162);
  private const string EmptyObjectJson = "{}";

  internal static void Create(TemporaryDatabase database) => database.Execute($"""
    PRAGMA foreign_keys = ON;
    CREATE TABLE sessions (
        session_id TEXT NOT NULL PRIMARY KEY,
        history_revision INTEGER NOT NULL CHECK (history_revision >= 0)
    ) STRICT;
    CREATE TABLE input_decisions (
        session_id TEXT NOT NULL REFERENCES sessions(session_id),
        input_id TEXT NOT NULL,
        expected_history_revision INTEGER NOT NULL CHECK (expected_history_revision >= 0),
        original_text_utf8 BLOB NOT NULL CHECK (length(original_text_utf8) <= 16384),
        decision INTEGER NOT NULL CHECK (decision IN (1, 2)),
        run_id TEXT,
        refusal_reason INTEGER CHECK (refusal_reason IN (1, 2, 3, 4)),
        llm_selection_json TEXT,
        PRIMARY KEY (session_id, input_id),
        CHECK ((decision = 1 AND run_id IS NOT NULL
                    AND run_id <> '00000000-0000-0000-0000-000000000000' AND refusal_reason IS NULL)
            OR (decision = 2 AND run_id IS NULL AND refusal_reason IS NOT NULL))
    ) STRICT;
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
    CREATE INDEX execution_runs_history_order ON execution_runs(session_id, history_revision, ordinal DESC);
    CREATE INDEX execution_runs_terminal_order ON execution_runs(session_id, ordinal DESC) WHERE outcome IS NOT NULL;
    CREATE TABLE execution_requests (
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
    CREATE UNIQUE INDEX one_unfinished_request ON execution_requests(run_id) WHERE outcome IS NULL;
    CREATE TABLE execution_output (
        request_id TEXT NOT NULL REFERENCES execution_requests(request_id),
        batch_index INTEGER NOT NULL CHECK (batch_index >= 0),
        text_utf8 BLOB NOT NULL CHECK (length(text_utf8) <= 16384),
        is_final INTEGER NOT NULL CHECK (is_final IN (0, 1)),
        PRIMARY KEY (request_id, batch_index)
    ) STRICT;
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
    CREATE TABLE session_creation (
        ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL UNIQUE REFERENCES sessions(session_id) ON DELETE CASCADE
    ) STRICT;
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
    CREATE TABLE execution_provider_context (
        request_id TEXT PRIMARY KEY NOT NULL REFERENCES execution_requests(request_id),
        response_json TEXT NOT NULL
    ) STRICT;

    INSERT INTO sessions VALUES ('{SessionId:D}', 7);
    INSERT INTO session_creation (session_id) VALUES ('{SessionId:D}');
    INSERT INTO input_decisions (session_id, input_id, expected_history_revision, original_text_utf8,
        decision, run_id, refusal_reason)
    VALUES ('{SessionId:D}', '{FirstInputId:D}', 7, X'696E7075742031', 1, '{ActiveRunId:D}', NULL),
        ('{SessionId:D}', '{SecondInputId:D}', 7, X'696E7075742032', 1, '{ActiveRunId:D}', NULL),
        ('{SessionId:D}', '{SettledInputId:D}', 7, X'696E7075742033', 1, '{SettledRunId:D}', NULL),
        ('{SessionId:D}', '{ToolOnlyInputId:D}', 7, X'696E7075742034', 1, '{ToolOnlyRunId:D}', NULL);
    INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
    VALUES ('{SessionId:D}', '{ActiveRunId:D}', 7, '{FirstInputId:D}', NULL),
        ('{SessionId:D}', '{SettledRunId:D}', 7, '{SettledInputId:D}', 0),
        ('{SessionId:D}', '{ToolOnlyRunId:D}', 7, '{ToolOnlyInputId:D}', 0);
    INSERT INTO execution_requests
        (request_id, run_id, request_index, output_id, next_batch_index, output_final, outcome)
    VALUES ('{FirstRequestId:D}', '{ActiveRunId:D}', 0, '{FirstOutputId:D}', 3, 1, 0),
        ('{SecondRequestId:D}', '{ActiveRunId:D}', 1, '{SecondOutputId:D}', 4, 0, NULL),
        ('{SettledRequestId:D}', '{SettledRunId:D}', 0, '{SettledOutputId:D}', 2, 0, NULL),
        ('{SecondToolOnlyRequestId:D}', '{ToolOnlyRunId:D}', 1, NULL, 0, 0, 0),
        ('{FirstToolOnlyRequestId:D}', '{ToolOnlyRunId:D}', 0, NULL, 0, 0, 0);
    INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id, request_id)
    VALUES ('{SessionId:D}', 7, '{ActiveRunId:D}', 0, '{FirstInputId:D}', '{FirstInputId:D}', NULL),
        ('{SessionId:D}', 7, '{ActiveRunId:D}', 1, '{FirstOutputId:D}', NULL, '{FirstRequestId:D}'),
        ('{SessionId:D}', 7, '{ActiveRunId:D}', 0, '{SecondInputId:D}', '{SecondInputId:D}', NULL),
        ('{SessionId:D}', 7, '{ActiveRunId:D}', 1, '{SecondOutputId:D}', NULL, '{SecondRequestId:D}'),
        ('{SessionId:D}', 7, '{SettledRunId:D}', 0, '{SettledInputId:D}', '{SettledInputId:D}', NULL),
        ('{SessionId:D}', 7, '{SettledRunId:D}', 1, '{SettledOutputId:D}', NULL, '{SettledRequestId:D}'),
        ('{SessionId:D}', 7, '{ToolOnlyRunId:D}', 0, '{ToolOnlyInputId:D}', '{ToolOnlyInputId:D}', NULL);
    INSERT INTO execution_tools (run_id, request_id, call_index, call_id, name, arguments_json, result_json)
    VALUES ('{ActiveRunId:D}', '{FirstRequestId:D}', 0, 'call-a', 'alpha', '{EmptyObjectJson}', '{EmptyObjectJson}'),
        ('{ActiveRunId:D}', '{FirstRequestId:D}', 1, 'call-b', 'beta', '{EmptyObjectJson}', NULL),
        ('{ActiveRunId:D}', '{SecondRequestId:D}', 0, 'call-c', 'gamma', '{EmptyObjectJson}', NULL),
        ('{ToolOnlyRunId:D}', '{FirstToolOnlyRequestId:D}', 1, 'call-e', 'epsilon', '{EmptyObjectJson}', NULL),
        ('{ToolOnlyRunId:D}', '{FirstToolOnlyRequestId:D}', 0, 'call-d', 'delta', '{EmptyObjectJson}', NULL),
        ('{ToolOnlyRunId:D}', '{SecondToolOnlyRequestId:D}', 0, 'call-f', 'zeta', '{EmptyObjectJson}', NULL);
    PRAGMA user_version = 8;
    """);

  internal static void CreateWithInterleavedToolOnlyRequest(TemporaryDatabase database) {
    Create(database);
    database.Execute($"""
      INSERT INTO input_decisions (session_id, input_id, expected_history_revision, original_text_utf8,
          decision, run_id, refusal_reason)
      VALUES ('{SessionId:D}', '{InterleavingInputId:D}', 7, X'696E7075742035', 1, '{InterleavingRunId:D}', NULL),
          ('{SessionId:D}', '{LaterInputId:D}', 7, X'696E7075742036', 1, '{LaterRunId:D}', NULL);
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
      VALUES ('{SessionId:D}', '{InterleavingRunId:D}', 7, '{InterleavingInputId:D}', 0),
          ('{SessionId:D}', '{LaterRunId:D}', 7, '{LaterInputId:D}', 0);
      INSERT INTO execution_requests
          (request_id, run_id, request_index, output_id, outcome)
      VALUES ('{InterleavingFirstRequestId:D}', '{InterleavingRunId:D}', 0, '{InterleavingFirstOutputId:D}', 0),
          ('{InterleavingToolOnlyRequestId:D}', '{InterleavingRunId:D}', 1, NULL, 0),
          ('{InterleavingLastRequestId:D}', '{InterleavingRunId:D}', 2, '{InterleavingLastOutputId:D}', 0);
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id, request_id)
      VALUES ('{SessionId:D}', 7, '{InterleavingRunId:D}', 0, '{InterleavingInputId:D}', '{InterleavingInputId:D}', NULL),
          ('{SessionId:D}', 7, '{InterleavingRunId:D}', 1, '{InterleavingFirstOutputId:D}', NULL, '{InterleavingFirstRequestId:D}'),
          ('{SessionId:D}', 7, '{InterleavingRunId:D}', 1, '{InterleavingLastOutputId:D}', NULL, '{InterleavingLastRequestId:D}'),
          ('{SessionId:D}', 7, '{LaterRunId:D}', 0, '{LaterInputId:D}', '{LaterInputId:D}', NULL);
      INSERT INTO execution_tools (run_id, request_id, call_index, call_id, name, arguments_json)
      VALUES ('{InterleavingRunId:D}', '{InterleavingToolOnlyRequestId:D}', 0,
          'interleaved-call', 'interleaved', '{EmptyObjectJson}');
      """);
  }

  private static Guid Id(int value) => Guid.Parse($"{value:x8}-0000-0000-0000-000000000000");
}
