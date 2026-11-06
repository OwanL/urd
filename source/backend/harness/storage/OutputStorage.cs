using Microsoft.Data.Sqlite;
using Urd.Core.Execution;

namespace Urd.Core.Storage;

// Appends ordered output segments and records explicit request completion.
internal sealed class OutputStorage(Database database) {
    internal bool Append(Guid sessionId, Guid runId, long historyRevision, LlmResponseChunk chunk) {
        ArgumentOutOfRangeException.ThrowIfNegative(chunk.ChunkIndex);
        if (chunk.RequestId == Guid.Empty || chunk.ResponseId == Guid.Empty
            || StorageText.StrictUtf8.GetByteCount(chunk.Text) > StorageText.MaximumBytes) {
            throw new ArgumentException("Output requires identities and at most 16 KiB of valid UTF-8 text.", nameof(chunk));
        }

        return database.Write(transaction => {
            using SqliteCommand advance = database.Command(transaction, """
                UPDATE execution_requests SET next_batch_index = next_batch_index + 1,
                    output_id = $output, output_final = $final
                WHERE request_id = $request AND run_id = $run AND outcome IS NULL
                    AND next_batch_index = $index AND output_final = 0
                    AND (output_id IS NULL OR output_id = $output)
                    AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
                        WHERE r.run_id = $run AND r.session_id = $session AND r.history_revision = $revision
                            AND s.history_revision = $revision AND r.stopping = 0 AND r.outcome IS NULL);
                """,
                ("$session", sessionId),
                ("$run", runId),
                ("$revision", historyRevision),
                ("$request", chunk.RequestId),
                ("$output", chunk.ResponseId),
                ("$index", chunk.ChunkIndex),
                ("$final", chunk.IsFinal ? 1 : 0));
            if (advance.ExecuteNonQuery() == 0)
                return false;

            using SqliteCommand append = database.Command(transaction, """
                INSERT INTO execution_output (request_id, batch_index, text_utf8, is_final)
                VALUES ($request, $index, $text, $final);
                """,
                ("$request", chunk.RequestId),
                ("$index", chunk.ChunkIndex),
                ("$text", StorageText.StrictUtf8.GetBytes(chunk.Text)),
                ("$final", chunk.IsFinal ? 1 : 0));
            append.ExecuteNonQuery();
            return true;
        });
    }

    internal bool CompleteRequest(Guid sessionId, Guid runId, long historyRevision, LlmRequestOutcome completion) {
        if (!Enum.IsDefined(completion.Outcome))
            throw new ArgumentOutOfRangeException(nameof(completion));

        return database.Write(transaction => CompleteRequest(transaction, sessionId, runId, historyRevision, completion));
    }

    private bool CompleteRequest(SqliteTransaction transaction, Guid sessionId, Guid runId,
        long historyRevision, LlmRequestOutcome completion) {
        using (SqliteCommand read = database.Command(transaction, """
            SELECT q.outcome, q.error, q.output_final, r.stopping, r.outcome
            FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
            JOIN sessions s ON s.session_id = r.session_id
            WHERE r.session_id = $session AND r.run_id = $run AND q.request_id = $request
                AND r.history_revision = $revision AND s.history_revision = $revision;
            """,
            ("$session", sessionId),
            ("$run", runId),
            ("$revision", historyRevision),
            ("$request", completion.RequestId)))
        using (SqliteDataReader reader = read.ExecuteReader()) {
            if (!reader.Read())
                return false;

            // Locals name the SELECT columns in order.
            ExecutionOutcome? savedOutcome = reader.IsDBNull(0) ? null : (ExecutionOutcome)reader.GetInt32(0);
            string? savedError = reader.IsDBNull(1) ? null : reader.GetString(1);
            bool hasFinalOutput = reader.GetInt32(2) != 0;
            bool stopRequested = reader.GetInt32(3) != 0;
            bool runSettled = !reader.IsDBNull(4);

            // An exact retry succeeds without rewriting the original evidence, even after settlement.
            if (savedOutcome is not null)
                return savedOutcome == completion.Outcome && savedError == completion.Error;

            if (runSettled)
                return false;

            if (completion.Outcome == ExecutionOutcome.Completed && (stopRequested || !hasFinalOutput))
                return false;
        }

        using SqliteCommand update = database.Command(transaction,
            "UPDATE execution_requests SET outcome = $outcome, error = $error WHERE request_id = $request;",
            ("$outcome", (int)completion.Outcome),
            ("$error", completion.Error),
            ("$request", completion.RequestId));
        update.ExecuteNonQuery();
        return true;
    }
}
