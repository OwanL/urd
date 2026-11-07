using System.Collections.ObjectModel;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Projects a bounded session history and its current execution state.
internal sealed class SnapshotReader {
  private const int SnapshotMessageLimit = 60;
  private const int PreviewUtf16Limit = 256;
  private readonly Database database;
  private readonly MessageContentReader content;

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

  internal SnapshotReader(Database database, MessageContentReader content) {
    this.database = database;
    this.content = content;
  }

  internal StoredSessionSnapshot Read(Guid sessionId) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    return database.Read(transaction => Read(transaction, sessionId));
  }

  private StoredSessionSnapshot Read(SqliteTransaction transaction, Guid sessionId) {
    long historyRevision = ReadHistoryRevision(transaction, sessionId);
    (Guid? unsettledRun, bool stopRequested) = ReadUnsettledRun(transaction, sessionId);
    ExecutionResult? lastSettlement = ReadLastSettlement(transaction, sessionId);
    List<(MessageRef Message, ExecutionOutcome? Settlement)> candidates =
        ReadCandidates(transaction, sessionId, historyRevision);
    bool hasOlder = candidates.Count > SnapshotMessageLimit;
    IReadOnlyList<MessagePreview> messages = ReadPreviews(transaction, sessionId, candidates);
    return new(historyRevision, unsettledRun, stopRequested, lastSettlement, messages, hasOlder);
  }

  private long ReadHistoryRevision(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand revisionRead = database.Command(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
    if (revisionRead.ExecuteScalar() is not long historyRevision)
      throw new KeyNotFoundException($"Session {sessionId} does not exist.");

    return historyRevision;
  }

  private (Guid? RunId, bool StopRequested) ReadUnsettledRun(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand activeRead = database.Command(transaction, SnapshotUnsettledSql, ("$session", sessionId));
    using SqliteDataReader reader = activeRead.ExecuteReader();
    return reader.Read() ? (reader.GetGuid(0), reader.GetBoolean(1)) : (null, false);
  }

  private ExecutionResult? ReadLastSettlement(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand terminalRead = database.Command(transaction, SnapshotSettlementSql,
        ("$session", sessionId));
    using SqliteDataReader reader = terminalRead.ExecuteReader();
    return reader.Read() ? new(reader.GetGuid(0), (ExecutionOutcome)reader.GetInt32(1)) : null;
  }

  private List<(MessageRef Message, ExecutionOutcome? Settlement)> ReadCandidates(
      SqliteTransaction transaction, Guid sessionId, long historyRevision) {
    // Newest first: assistant before user within each run. Previews reverse this order.
    using SqliteCommand runsRead = database.Command(transaction, SnapshotRunsSql,
        ("$session", sessionId),
        ("$revision", historyRevision));
    using SqliteDataReader reader = runsRead.ExecuteReader();
    return reader.Cast<DbDataRecord>()
        .SelectMany(row => MessagesFromRun(row, historyRevision))
        .Take(SnapshotMessageLimit + 1)
        .ToList();
  }

  private static IEnumerable<(MessageRef Message, ExecutionOutcome? Settlement)> MessagesFromRun(
      DbDataRecord row, long historyRevision) {
    Guid runId = Guid.Parse(row.GetString(0));
    ExecutionOutcome? outcome = row.IsDBNull(2) ? null : (ExecutionOutcome)row.GetInt64(2);
    if (!row.IsDBNull(3))
      yield return (new(runId, Guid.Parse(row.GetString(3)), MessageRole.Assistant, historyRevision), outcome);

    yield return (new(runId, Guid.Parse(row.GetString(1)), MessageRole.User, historyRevision), outcome);
  }

  private ReadOnlyCollection<MessagePreview> ReadPreviews(SqliteTransaction transaction, Guid sessionId,
      List<(MessageRef Message, ExecutionOutcome? Settlement)> candidates) {
    return candidates
        .Take(SnapshotMessageLimit)
        .Reverse()
        .Select(candidate => {
          MessageContentPage prefix = content.ReadPreview(transaction, sessionId, candidate.Message, PreviewUtf16Limit);
          return new MessagePreview(candidate.Message, prefix.Text, prefix.HasMore, prefix.AvailableThrough,
              prefix.FinalOutputCommitted, candidate.Settlement);
        })
        .ToList()
        .AsReadOnly();
  }
}
