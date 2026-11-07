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

  // Select identities before joining lifecycle facts or reading any message bodies.
  // One execution can contain arbitrarily many messages; only this index window is bounded.
  internal const string SnapshotMessagesSql = """
    SELECT m.run_id, m.role, m.message_id, r.outcome
    FROM (
        SELECT ordinal, run_id, role, message_id FROM execution_messages
        WHERE session_id = $session AND history_revision = $revision
        ORDER BY ordinal DESC LIMIT 61
    ) m
    JOIN execution_runs r ON r.run_id = m.run_id
    ORDER BY m.ordinal DESC;
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
    (Guid? unsettledExecution, bool stopRequested) = ReadUnsettledExecution(transaction, sessionId);
    SessionExecutionResult? lastSettlement = ReadLastSettlement(transaction, sessionId);
    List<(MessageRef Message, ExecutionOutcome? Settlement)> candidates =
        ReadCandidates(transaction, sessionId, historyRevision);
    bool hasOlder = candidates.Count > SnapshotMessageLimit;
    IReadOnlyList<MessagePreview> messages = ReadPreviews(transaction, sessionId, candidates);
    return new(historyRevision, unsettledExecution, stopRequested, lastSettlement, messages, hasOlder);
  }

  private long ReadHistoryRevision(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand revisionRead = database.Command(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
    if (revisionRead.ExecuteScalar() is not long historyRevision)
      throw new KeyNotFoundException($"Session {sessionId} does not exist.");

    return historyRevision;
  }

  private (Guid? ExecutionId, bool StopRequested) ReadUnsettledExecution(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand activeRead = database.Command(transaction, SnapshotUnsettledSql, ("$session", sessionId));
    using SqliteDataReader reader = activeRead.ExecuteReader();
    return reader.Read() ? (reader.GetGuid(0), reader.GetBoolean(1)) : (null, false);
  }

  private SessionExecutionResult? ReadLastSettlement(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand terminalRead = database.Command(transaction, SnapshotSettlementSql,
        ("$session", sessionId));
    using SqliteDataReader reader = terminalRead.ExecuteReader();
    return reader.Read() ? new(reader.GetGuid(0), (ExecutionOutcome)reader.GetInt32(1)) : null;
  }

  private List<(MessageRef Message, ExecutionOutcome? Settlement)> ReadCandidates(
      SqliteTransaction transaction, Guid sessionId, long historyRevision) {
    // Newest admission/response first. Previews reverse this bounded window.
    using SqliteCommand messagesRead = database.Command(transaction, SnapshotMessagesSql,
        ("$session", sessionId),
        ("$revision", historyRevision));
    using SqliteDataReader reader = messagesRead.ExecuteReader();
    return reader.Cast<DbDataRecord>()
        .Select(row => (
            Message: new MessageRef(Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(2)),
                (MessageRole)row.GetInt64(1), historyRevision),
            Settlement: row.IsDBNull(3) ? (ExecutionOutcome?)null : (ExecutionOutcome)row.GetInt64(3)))
        .ToList();
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
