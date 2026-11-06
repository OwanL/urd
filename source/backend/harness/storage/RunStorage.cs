using System.Collections.ObjectModel;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;

namespace Urd.Core.Storage;

// Claims run work, reads its context, and retains stop and settlement facts.
internal sealed class RunStorage(Database database) {
    internal LlmRequest? ClaimNextRequest(Guid sessionId, Guid runId, long historyRevision) {
        return database.Write<LlmRequest?>(transaction => {
            Guid requestId = Guid.NewGuid();
            using SqliteCommand claim = database.Command(transaction, """
                INSERT INTO execution_requests (request_id, run_id)
                SELECT $request, r.run_id FROM execution_runs r
                JOIN sessions s ON s.session_id = r.session_id
                WHERE r.session_id = $session AND r.run_id = $run
                    AND r.history_revision = $revision AND s.history_revision = $revision
                    AND r.stopping = 0 AND r.outcome IS NULL
                    AND NOT EXISTS (SELECT 1 FROM execution_requests q WHERE q.run_id = r.run_id);
                """,
                ("$session", sessionId),
                ("$run", runId),
                ("$revision", historyRevision),
                ("$request", requestId));
            if (claim.ExecuteNonQuery() == 0)
                return null;

            IReadOnlyList<ChatMessage> messages = ReadRequestContext(transaction, sessionId, runId, historyRevision);
            return new LlmRequest(requestId, messages);
        });
    }

    internal void RequestStop(Guid sessionId, Guid runId) {
        database.WithoutTransaction(() => {
            using SqliteCommand command = database.Command(null,
                "UPDATE execution_runs SET stopping = 1 WHERE session_id = $session AND run_id = $run AND outcome IS NULL;",
                ("$session", sessionId),
                ("$run", runId));
            return command.ExecuteNonQuery();
        });
    }

    // The caller has joined all owned work before entering this transaction.
    internal ExecutionResult? TrySettle(Guid sessionId, Guid runId, ExecutionOutcome driverOutcome) {
        if (!Enum.IsDefined(driverOutcome))
            throw new ArgumentOutOfRangeException(nameof(driverOutcome));

        return database.Write<ExecutionResult?>(transaction => {
            using SqliteCommand read = database.Command(transaction, """
                SELECT r.outcome, r.stopping, q.outcome FROM execution_runs r
                LEFT JOIN execution_requests q ON q.run_id = r.run_id
                WHERE r.session_id = $session AND r.run_id = $run;
                """,
                ("$session", sessionId),
                ("$run", runId));
            Settlement? settlement = DecideSettlement(read, driverOutcome);
            if (settlement is null)
                return null;

            if (settlement.AlreadySettled)
                return new(runId, settlement.Outcome);

            using SqliteCommand settle = database.Command(transaction, """
                UPDATE execution_requests SET outcome = $outcome
                WHERE run_id = $run AND outcome IS NULL;
                UPDATE execution_runs SET outcome = $outcome WHERE session_id = $session AND run_id = $run;
                """,
                ("$session", sessionId),
                ("$run", runId),
                ("$outcome", (int)settlement.Outcome));
            settle.ExecuteNonQuery();
            return new(runId, settlement.Outcome);
        });
    }

    internal ExecutionResult? ReadSettlement(Guid sessionId, Guid runId) {
        return database.WithoutTransaction<ExecutionResult?>(() => {
            using SqliteCommand read = database.Command(null,
                "SELECT outcome FROM execution_runs WHERE session_id = $session AND run_id = $run;",
                ("$session", sessionId),
                ("$run", runId));
            object? outcome = read.ExecuteScalar();
            return outcome is null or DBNull ? null : new(runId, (ExecutionOutcome)(long)outcome);
        });
    }

    private static Settlement? DecideSettlement(SqliteCommand read, ExecutionOutcome driverOutcome) {
        using SqliteDataReader reader = read.ExecuteReader();
        if (!reader.Read())
            return null;

        if (!reader.IsDBNull(0))
            return new((ExecutionOutcome)reader.GetInt32(0), AlreadySettled: true);

        if (reader.GetInt32(1) != 0)
            return new(ExecutionOutcome.Cancelled, AlreadySettled: false);

        if (driverOutcome != ExecutionOutcome.Completed)
            return new(driverOutcome, AlreadySettled: false);

        // Invocation exit or text alone never manufactures request success.
        return reader.IsDBNull(2)
            ? null : new((ExecutionOutcome)reader.GetInt32(2), AlreadySettled: false);
    }

    private ReadOnlyCollection<ChatMessage> ReadRequestContext(
        SqliteTransaction transaction, Guid sessionId, Guid runId, long historyRevision) {
        using SqliteCommand read = database.Command(transaction, """
            SELECT r.ordinal, 0 AS role_order, 0 AS batch_index, i.original_text_utf8
            FROM execution_runs r JOIN input_decisions i ON i.session_id = r.session_id AND i.input_id = r.input_id
            WHERE r.session_id = $session AND r.history_revision = $revision
                AND r.ordinal <= (SELECT ordinal FROM execution_runs WHERE run_id = $run)
            UNION ALL
            SELECT r.ordinal, 1, o.batch_index, o.text_utf8
            FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
                JOIN execution_output o ON o.request_id = q.request_id
            WHERE r.session_id = $session AND r.history_revision = $revision
                AND r.ordinal <= (SELECT ordinal FROM execution_runs WHERE run_id = $run)
            ORDER BY 1, 2, 3;
            """,
            ("$session", sessionId),
            ("$run", runId),
            ("$revision", historyRevision));
        using SqliteDataReader reader = read.ExecuteReader();
        var messages = new List<ChatMessage>();
        StringBuilder? assistant = null;
        while (reader.Read()) {
            string text = StorageText.StrictUtf8.GetString((byte[])reader.GetValue(3));
            if (reader.GetInt32(1) != 0) {
                (assistant ??= new()).Append(text);
                continue;
            }

            AddAssistant(messages, assistant);
            assistant = null;
            messages.Add(new(ChatRole.User, text));
        }

        AddAssistant(messages, assistant);
        return messages.AsReadOnly();
    }

    private static void AddAssistant(List<ChatMessage> messages, StringBuilder? assistant) {
        if (assistant is not null)
            messages.Add(new(ChatRole.Assistant, assistant.ToString()));
    }

    // Distinguishes retained settlement from a new outcome to persist.
    private sealed record Settlement(ExecutionOutcome Outcome, bool AlreadySettled);
}
