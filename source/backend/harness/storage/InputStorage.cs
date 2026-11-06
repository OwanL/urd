using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Retains idempotent input decisions and admits each accepted input with its run.
internal sealed class InputStorage(Database database) {
    // The current text workflow admits one input per run; there is no follow-up queue.
    internal (InputReceipt Receipt, bool NewlyAccepted) Admit(
        Guid sessionId, long historyRevision, InputCommand command, bool accepting, Guid proposedRunId) {
        Validate(sessionId, command);
        return database.Write(transaction => {
            InputReceipt? retained = ReadReceipt(sessionId, command.InputId, transaction);
            if (retained is not null) {
                EnsureSameCommand(retained, command);
                return (retained, false);
            }

            long actualRevision = ReadRevision(transaction, sessionId);
            InputRefusalReason? refusal = DecideRefusal(
                transaction, sessionId, actualRevision, historyRevision, command, accepting);
            if (refusal is { } reason) {
                var refused = new InputReceipt(sessionId, command, InputDecision.Refused, reason, null);
                InsertReceipt(transaction, refused);
                return (refused, false);
            }

#pragma warning disable S3928 // Captured method argument; validation must follow retained decisions.
            if (proposedRunId == Guid.Empty)
                throw new ArgumentException("Run ID cannot be empty.", nameof(proposedRunId));
#pragma warning restore S3928

            var accepted = new InputReceipt(sessionId, command, InputDecision.Accepted, null, proposedRunId);
            InsertReceipt(transaction, accepted);
            using SqliteCommand insert = database.Command(transaction, """
                INSERT INTO execution_runs (session_id, run_id, history_revision, input_id)
                VALUES ($session, $run, $revision, $input);
                """,
                ("$session", sessionId),
                ("$run", proposedRunId),
                ("$revision", actualRevision),
                ("$input", command.InputId));
            insert.ExecuteNonQuery();
            return (accepted, true);
        });
    }

    // An identical retry retains its original decision rather than today's refusal reason.
    internal InputReceipt RecordRefusal(Guid sessionId, InputCommand command, InputRefusalReason reason) {
        Validate(sessionId, command);
        return database.Write(transaction => {
            InputReceipt? retained = ReadReceipt(sessionId, command.InputId, transaction);
            if (retained is not null) {
                EnsureSameCommand(retained, command);
                return retained;
            }

#pragma warning disable S3928 // Captured method argument; an exact retry retains its original reason.
            if (!Enum.IsDefined(reason))
                throw new ArgumentOutOfRangeException(nameof(reason));
#pragma warning restore S3928

            var receipt = new InputReceipt(sessionId, command, InputDecision.Refused, reason, null);
            InsertReceipt(transaction, receipt);
            return receipt;
        });
    }

    internal InputReceipt? ReadReceipt(Guid sessionId, Guid inputId)
        => database.Guarded(() => ReadReceipt(sessionId, inputId, transaction: null));

    private InputReceipt? ReadReceipt(Guid sessionId, Guid inputId, SqliteTransaction? transaction) {
        using SqliteCommand command = database.Command(transaction, """
            SELECT expected_history_revision, original_text_utf8, decision, run_id, refusal_reason FROM input_decisions
            WHERE session_id = $session AND input_id = $input;
            """,
            ("$session", sessionId),
            ("$input", inputId));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        // Locals name the SELECT columns in order.
        long expectedRevision = reader.GetInt64(0);
        string originalText = StorageText.StrictUtf8.GetString((byte[])reader.GetValue(1));
        var decision = (InputDecision)reader.GetInt32(2);
        Guid? runId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
        InputRefusalReason? refusalReason = reader.IsDBNull(4) ? null : (InputRefusalReason)reader.GetInt32(4);
        var original = new InputCommand(inputId, expectedRevision, originalText);
        return new InputReceipt(sessionId, original, decision, refusalReason, runId);
    }

    private long ReadRevision(SqliteTransaction transaction, Guid sessionId) {
        using SqliteCommand read = database.Command(transaction,
            "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
        object? value = read.ExecuteScalar();
        if (value is null)
            throw new KeyNotFoundException($"Session {sessionId} does not exist.");

        return (long)value;
    }

    private InputRefusalReason? DecideRefusal(SqliteTransaction transaction, Guid sessionId, long actualRevision,
        long historyRevision, InputCommand command, bool accepting) {
        using SqliteCommand read = database.Command(transaction,
            "SELECT stopping FROM execution_runs WHERE session_id = $session AND outcome IS NULL;",
            ("$session", sessionId));
        object? stopFlag = read.ExecuteScalar();
        if (!accepting || stopFlag is 1L)
            return InputRefusalReason.Stopping;

        if (actualRevision != historyRevision || command.ExpectedHistoryRevision != actualRevision)
            return InputRefusalReason.StaleRevision;

        return stopFlag is not null ? InputRefusalReason.Capacity : null;
    }

    private void InsertReceipt(SqliteTransaction transaction, InputReceipt receipt) {
        // Length-bearing UTF-8 bytes preserve embedded NUL as well as exact Unicode.
        using SqliteCommand insert = database.Command(transaction, """
            INSERT INTO input_decisions (
                session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason)
            SELECT session_id, $input, $revision, $text, $decision, $run, $reason
            FROM sessions WHERE session_id = $session;
            """,
            ("$session", receipt.SessionId),
            ("$input", receipt.Command.InputId),
            ("$revision", receipt.Command.ExpectedHistoryRevision),
            ("$text", StorageText.StrictUtf8.GetBytes(receipt.Command.Text)),
            ("$decision", (int)receipt.Decision),
            ("$run", receipt.RunId),
            ("$reason", receipt.RefusalReason is { } reason ? (int)reason : null));
        if (insert.ExecuteNonQuery() == 0)
            throw new KeyNotFoundException($"Session {receipt.SessionId} does not exist.");
    }

    private static void EnsureSameCommand(InputReceipt retained, InputCommand command) {
        if (retained.Command != command)
            throw new InvalidOperationException("The input ID already belongs to a different original command.");
    }

    private static void Validate(Guid sessionId, InputCommand command) {
        // Commands come from public session APIs.
        ArgumentNullException.ThrowIfNull(command);
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

        if (command.InputId == Guid.Empty)
            throw new ArgumentException("Input ID cannot be empty.", nameof(command));

        ArgumentOutOfRangeException.ThrowIfNegative(command.ExpectedHistoryRevision);
        if (StorageText.StrictUtf8.GetByteCount(command.Text) > StorageText.MaximumBytes) {
            throw new ArgumentException(
                $"Input text cannot exceed {StorageText.MaximumBytes} UTF-8 bytes.", nameof(command));
        }
    }
}
