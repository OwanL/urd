namespace Urd.Backend.Tests.Support;

// Keeps current execution/provider evidence while restoring the schema-seven shapes that migrations alter.
internal static class SchemaSevenDatabase {
  internal static void Downgrade(TemporaryDatabase database) => database.Execute("""
    PRAGMA foreign_keys = OFF;
    BEGIN;
    UPDATE execution_messages SET request_id = NULL WHERE role = 0;
    DROP INDEX execution_inputs_not_sent_order;
    ALTER TABLE execution_inputs DROP COLUMN not_sent;
    ALTER TABLE execution_requests DROP COLUMN attempt;
    CREATE TABLE input_decisions_v7 (
        session_id TEXT NOT NULL REFERENCES sessions(session_id), input_id TEXT NOT NULL,
        expected_history_revision INTEGER NOT NULL CHECK (expected_history_revision >= 0),
        original_text_utf8 BLOB NOT NULL CHECK (length(original_text_utf8) <= 16384),
        decision INTEGER NOT NULL CHECK (decision IN (1, 2)), run_id TEXT,
        refusal_reason INTEGER CHECK (refusal_reason IN (1, 2, 3)), PRIMARY KEY (session_id, input_id),
        CHECK ((decision = 1 AND run_id IS NOT NULL AND run_id <> '00000000-0000-0000-0000-000000000000'
            AND refusal_reason IS NULL) OR (decision = 2 AND run_id IS NULL AND refusal_reason IS NOT NULL))
    ) STRICT;
    INSERT INTO input_decisions_v7
    SELECT session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason
    FROM input_decisions;
    DROP TABLE input_decisions;
    ALTER TABLE input_decisions_v7 RENAME TO input_decisions;
    PRAGMA user_version = 7;
    COMMIT;
    PRAGMA foreign_keys = ON;
    """);
}
