using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

public sealed partial class SessionStore
{
    private const int MaximumOutputBytes = 16 * 1024;

    // This first fake-text increment admits one input per run, not a follow-up queue.
    internal (InputReceipt Receipt, bool NewlyAccepted) AdmitInput(
        Guid sessionId, long historyRevision, InputCommand command, bool accepting, Guid proposedRunId)
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
                return (retained, false);
            }

            using SqliteCommand revisionRead = ExecutionCommand(transaction,
                "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
            object? revisionValue = revisionRead.ExecuteScalar();
            if (revisionValue is null)
            {
                throw new KeyNotFoundException($"Session {sessionId} does not exist.");
            }

            long actualRevision = (long)revisionValue;
            using SqliteCommand activeRead = ExecutionCommand(transaction,
                "SELECT stopping FROM execution_runs WHERE session_id = $session AND outcome IS NULL;", ("$session", sessionId));
            object? active = activeRead.ExecuteScalar();
            InputRefusalReason? refusal = !accepting || active is 1L ? InputRefusalReason.Stopping
                : actualRevision != historyRevision || command.ExpectedHistoryRevision != actualRevision ? InputRefusalReason.StaleRevision
                : active is not null ? InputRefusalReason.Capacity : null;
            InputReceipt receipt;
            if (refusal is { } reason)
            {
                receipt = new(sessionId, command, InputDecision.Refused, reason, null);
                InsertInputReceipt(transaction, receipt);
            }
            else
            {
                if (proposedRunId == Guid.Empty)
                {
                    throw new ArgumentException("Run ID cannot be empty.", nameof(proposedRunId));
                }

                receipt = new(sessionId, command, InputDecision.Accepted, null, proposedRunId);
                InsertInputReceipt(transaction, receipt);
                using SqliteCommand insert = ExecutionCommand(transaction, """
                    INSERT INTO execution_runs (session_id, run_id, history_revision, input_id)
                    VALUES ($session, $run, $revision, $input);
                    """, ("$session", sessionId), ("$run", proposedRunId), ("$revision", actualRevision), ("$input", command.InputId));
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return (receipt, refusal is null);
        }
    }

    internal LlmRequest? ClaimNextRequest(Guid sessionId, Guid runId, long historyRevision)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteTransaction transaction = _connection.BeginTransaction();
            Guid requestId = Guid.NewGuid();
            using SqliteCommand claim = ExecutionCommand(transaction, """
                INSERT INTO execution_requests (request_id, run_id)
                SELECT $request, r.run_id FROM execution_runs r
                JOIN sessions s ON s.session_id = r.session_id
                WHERE r.session_id = $session AND r.run_id = $run
                    AND r.history_revision = $revision AND s.history_revision = $revision
                    AND r.stopping = 0 AND r.outcome IS NULL
                    AND NOT EXISTS (SELECT 1 FROM execution_requests q WHERE q.run_id = r.run_id);
                """, ("$session", sessionId), ("$run", runId), ("$revision", historyRevision), ("$request", requestId));
            if (claim.ExecuteNonQuery() == 0)
            {
                return null;
            }

            IReadOnlyList<ChatMessage> messages = ReadRequestContext(transaction, sessionId, runId, historyRevision);
            transaction.Commit();
            return new LlmRequest(requestId, messages);
        }
    }

    internal bool AppendOutput(Guid sessionId, Guid runId, long historyRevision, LlmResponseChunk batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Text);
        ArgumentOutOfRangeException.ThrowIfNegative(batch.ChunkIndex);
        if (batch.RequestId == Guid.Empty || batch.ResponseId == Guid.Empty || StrictUtf8.GetByteCount(batch.Text) > MaximumOutputBytes)
        {
            throw new ArgumentException("Output requires identities and at most 16 KiB of valid UTF-8 text.", nameof(batch));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using SqliteCommand advance = ExecutionCommand(transaction, """
                UPDATE execution_requests SET next_batch_index = next_batch_index + 1,
                    output_id = $output, output_final = $final
                WHERE request_id = $request AND run_id = $run AND outcome IS NULL
                    AND next_batch_index = $index AND output_final = 0
                    AND (output_id IS NULL OR output_id = $output)
                    AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
                        WHERE r.run_id = $run AND r.session_id = $session AND r.history_revision = $revision
                            AND s.history_revision = $revision AND r.stopping = 0 AND r.outcome IS NULL);
                """, ("$session", sessionId), ("$run", runId), ("$revision", historyRevision),
                ("$request", batch.RequestId), ("$output", batch.ResponseId), ("$index", batch.ChunkIndex), ("$final", batch.IsFinal ? 1 : 0));
            if (advance.ExecuteNonQuery() == 0)
            {
                return false;
            }

            using SqliteCommand append = ExecutionCommand(transaction, """
                INSERT INTO execution_output (request_id, batch_index, text_utf8, is_final)
                VALUES ($request, $index, $text, $final);
                """, ("$request", batch.RequestId), ("$index", batch.ChunkIndex),
                ("$text", StrictUtf8.GetBytes(batch.Text)), ("$final", batch.IsFinal ? 1 : 0));
            append.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }
    }

    internal bool CompleteRequest(Guid sessionId, Guid runId, long historyRevision, LlmRequestOutcome completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (!Enum.IsDefined(completion.Outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(completion));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using SqliteCommand read = ExecutionCommand(transaction, """
                SELECT q.outcome, q.error, q.output_final, r.stopping, r.outcome
                FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
                JOIN sessions s ON s.session_id = r.session_id
                WHERE r.session_id = $session AND r.run_id = $run AND q.request_id = $request
                    AND r.history_revision = $revision AND s.history_revision = $revision;
                """, ("$session", sessionId), ("$run", runId), ("$revision", historyRevision), ("$request", completion.RequestId));
            using (SqliteDataReader reader = read.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (!reader.IsDBNull(0))
                {
                    return (ExecutionOutcome)reader.GetInt32(0) == completion.Outcome
                        && (reader.IsDBNull(1) ? null : reader.GetString(1)) == completion.Error;
                }

                if (!reader.IsDBNull(4) || (completion.Outcome == ExecutionOutcome.Completed
                    && (reader.GetInt32(3) != 0 || reader.GetInt32(2) == 0)))
                {
                    return false;
                }
            }

            using SqliteCommand update = ExecutionCommand(transaction,
                "UPDATE execution_requests SET outcome = $outcome, error = $error WHERE request_id = $request;",
                ("$outcome", (int)completion.Outcome), ("$error", completion.Error), ("$request", completion.RequestId));
            update.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }
    }

    internal void RequestStop(Guid sessionId, Guid runId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteCommand command = ExecutionCommand(null,
                "UPDATE execution_runs SET stopping = 1 WHERE session_id = $session AND run_id = $run AND outcome IS NULL;",
                ("$session", sessionId), ("$run", runId));
            command.ExecuteNonQuery();
        }
    }

    // The caller has joined all owned work before entering this transaction.
    internal ExecutionResult? TrySettle(Guid sessionId, Guid runId, ExecutionOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using SqliteCommand read = ExecutionCommand(transaction, """
                SELECT r.outcome, r.stopping, q.outcome FROM execution_runs r
                LEFT JOIN execution_requests q ON q.run_id = r.run_id
                WHERE r.session_id = $session AND r.run_id = $run;
                """, ("$session", sessionId), ("$run", runId));
            using (SqliteDataReader reader = read.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                if (!reader.IsDBNull(0))
                {
                    return new(runId, (ExecutionOutcome)reader.GetInt32(0));
                }

                if (reader.GetInt32(1) != 0)
                {
                    outcome = ExecutionOutcome.Cancelled;
                }
                else if (outcome == ExecutionOutcome.Completed)
                {
                    // Invocation exit or text alone never manufactures request success.
                    if (reader.IsDBNull(2))
                    {
                        return null;
                    }

                    outcome = (ExecutionOutcome)reader.GetInt32(2);
                }
            }

            using SqliteCommand settle = ExecutionCommand(transaction, """
                UPDATE execution_requests SET outcome = $outcome
                WHERE run_id = $run AND outcome IS NULL;
                UPDATE execution_runs SET outcome = $outcome WHERE session_id = $session AND run_id = $run;
                """, ("$session", sessionId), ("$run", runId), ("$outcome", (int)outcome));
            settle.ExecuteNonQuery();
            transaction.Commit();
            return new(runId, outcome);
        }
    }

    internal ExecutionResult? ReadSettlement(Guid sessionId, Guid runId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteCommand read = ExecutionCommand(null,
                "SELECT outcome FROM execution_runs WHERE session_id = $session AND run_id = $run;",
                ("$session", sessionId), ("$run", runId));
            object? outcome = read.ExecuteScalar();
            return outcome is null or DBNull ? null : new(runId, (ExecutionOutcome)(long)outcome);
        }
    }

    internal bool RunExists(Guid sessionId, Guid runId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteCommand read = ExecutionCommand(null,
                "SELECT EXISTS(SELECT 1 FROM execution_runs WHERE session_id = $session AND run_id = $run);",
                ("$session", sessionId), ("$run", runId));
            return (long)read.ExecuteScalar()! != 0;
        }
    }

    private IReadOnlyList<ChatMessage> ReadRequestContext(SqliteTransaction transaction, Guid sessionId, Guid runId, long revision)
    {
        using SqliteCommand read = ExecutionCommand(transaction, """
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
            """, ("$session", sessionId), ("$run", runId), ("$revision", revision));
        using SqliteDataReader reader = read.ExecuteReader();
        var messages = new List<ChatMessage>();
        StringBuilder? assistant = null;
        while (reader.Read())
        {
            string text = StrictUtf8.GetString((byte[])reader.GetValue(3));
            if (reader.GetInt32(1) == 0)
            {
                if (assistant is not null)
                {
                    messages.Add(new(ChatRole.Assistant, assistant.ToString()));
                    assistant = null;
                }

                messages.Add(new(ChatRole.User, text));
            }
            else
            {
                (assistant ??= new()).Append(text);
            }
        }

        if (assistant is not null)
        {
            messages.Add(new(ChatRole.Assistant, assistant.ToString()));
        }

        return messages.AsReadOnly();
    }

    private SqliteCommand ExecutionCommand(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value is Guid id ? id.ToString("D") : value ?? DBNull.Value);
        }

        return command;
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
