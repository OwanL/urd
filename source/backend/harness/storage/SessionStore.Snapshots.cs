using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

internal sealed record StoredSessionSnapshot(
    long HistoryRevision, Guid? UnsettledRunId, bool StopRequested,
    RunSettlement? LastSettlement, IReadOnlyList<MessagePreview> Messages, bool HasOlderMessages);

public sealed partial class SessionStore
{
    private const int SnapshotMessageLimit = 60;

    // Keep the run window before the join: its indexed seek visits at most 61 runs.
    // A possible outer sort is therefore bounded, regardless of retained history size.
    internal const string SnapshotRunsSql = """
        SELECT r.run_id, r.input_id, r.outcome, q.output_id
        FROM (
            SELECT ordinal, run_id, input_id, outcome FROM execution_runs
            WHERE session_id = $session AND history_revision = $revision
            ORDER BY ordinal DESC LIMIT 61
        ) r
        LEFT JOIN execution_requests q ON q.run_id = r.run_id
        ORDER BY r.ordinal DESC;
        """;

    internal const string SnapshotUnsettledSql = """
        SELECT run_id, stopping FROM execution_runs
        WHERE session_id = $session AND outcome IS NULL;
        """;

    internal const string SnapshotSettlementSql = """
        SELECT run_id, outcome FROM execution_runs
        WHERE session_id = $session AND outcome IS NOT NULL
        ORDER BY ordinal DESC LIMIT 1;
        """;

    internal StoredSessionSnapshot ReadSnapshot(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            using SqliteCommand revisionRead = ExecutionCommand(transaction,
                "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
            long revision = revisionRead.ExecuteScalar() is long value
                ? value : throw new KeyNotFoundException($"Session {sessionId} does not exist.");

            Guid? unsettledRun = null;
            bool stopRequested = false;
            using (SqliteCommand activeRead = ExecutionCommand(transaction, SnapshotUnsettledSql, ("$session", sessionId)))
            using (SqliteDataReader reader = activeRead.ExecuteReader())
            {
                if (reader.Read())
                {
                    unsettledRun = reader.GetGuid(0);
                    stopRequested = reader.GetBoolean(1);
                }
            }

            RunSettlement? lastSettlement = null;
            using (SqliteCommand terminalRead = ExecutionCommand(transaction, SnapshotSettlementSql, ("$session", sessionId)))
            using (SqliteDataReader reader = terminalRead.ExecuteReader())
            {
                if (reader.Read())
                {
                    lastSettlement = new(reader.GetGuid(0), (ExecutionOutcome)reader.GetInt32(1));
                }
            }

            // Newest first, with assistant after input in chronological order.
            var candidates = new List<(MessageRef Message, ExecutionOutcome? Settlement)>();
            using (SqliteCommand runsRead = ExecutionCommand(transaction, SnapshotRunsSql,
                ("$session", sessionId), ("$revision", revision)))
            using (SqliteDataReader reader = runsRead.ExecuteReader())
            {
                while (candidates.Count <= SnapshotMessageLimit && reader.Read())
                {
                    Guid run = reader.GetGuid(0);
                    ExecutionOutcome? outcome = reader.IsDBNull(2) ? null : (ExecutionOutcome)reader.GetInt32(2);
                    if (!reader.IsDBNull(3))
                    {
                        candidates.Add((new(run, reader.GetGuid(3), MessageRole.Assistant, revision), outcome));
                    }

                    if (candidates.Count <= SnapshotMessageLimit)
                    {
                        candidates.Add((new(run, reader.GetGuid(1), MessageRole.User, revision), outcome));
                    }
                }
            }

            bool hasOlder = candidates.Count > SnapshotMessageLimit;
            var messages = new List<MessagePreview>(Math.Min(candidates.Count, SnapshotMessageLimit));
            for (int index = Math.Min(candidates.Count, SnapshotMessageLimit) - 1; index >= 0; index--)
            {
                (MessageRef message, ExecutionOutcome? settlement) = candidates[index];
                RowContentPage prefix = ReadRowContent(transaction, sessionId, new(message, 0, 0),
                    maximumUtf8Bytes: 16384, maximumUtf16Units: 256, maximumSegments: 16);
                messages.Add(new(message, prefix.Text, prefix.HasMore, prefix.AvailableThrough,
                    prefix.FinalOutputCommitted, settlement));
            }

            transaction.Commit();
            return new(revision, unsettledRun, stopRequested, lastSettlement, messages.AsReadOnly(), hasOlder);
        }
    }
}
