using System.Collections.ObjectModel;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Providers;
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
        WHERE session_id = $session AND history_revision = $revision AND role IN (0, 1)
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

  internal const string SnapshotSelectionSql = """
    SELECT d.llm_selection_json FROM execution_runs r
    JOIN input_decisions d ON d.session_id = r.session_id AND d.input_id = r.input_id
    WHERE r.session_id = $session AND r.run_id = $run;
    """;

  internal SnapshotReader(Database database, MessageContentReader content) {
    this.database = database;
    this.content = content;
  }

  // Reads stored facts only. SessionBase supplies live admission and Stop state before exposure.
  internal SessionSnapshot ReadStored(Guid sessionId) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    return database.Read(transaction => Read(transaction, sessionId));
  }

  private SessionSnapshot Read(SqliteTransaction transaction, Guid sessionId) {
    long historyRevision = ReadHistoryRevision(transaction, sessionId);
    (Guid? unsettledExecution, bool stopRequested) = ReadUnsettledExecution(transaction, sessionId);
    SessionExecutionResult? lastSettlement = ReadLastSettlement(transaction, sessionId);
    LlmSelection? latestSelection = ReadLatestSelection(
        transaction, sessionId, unsettledExecution ?? lastSettlement?.ExecutionId);
    List<(MessageRef Message, ExecutionOutcome? Settlement)> candidates =
        ReadCandidates(transaction, sessionId, historyRevision);
    bool hasOlder = candidates.Count > SnapshotMessageLimit;
    IReadOnlyList<MessagePreview> messages = ReadPreviews(transaction, sessionId, candidates);
    // Storage cannot establish live admission eligibility; the session replaces this fence.
    return new(sessionId, historyRevision, AdmissionFenced: true,
        unsettledExecution, stopRequested, lastSettlement, messages, hasOlder, latestSelection);
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

  private LlmSelection? ReadLatestSelection(SqliteTransaction transaction, Guid sessionId, Guid? executionId) {
    if (executionId is null)
      return null;

    // The initiating submission owns selection even when it is outside the message window.
    using SqliteCommand command = database.Command(transaction, SnapshotSelectionSql,
        ("$session", sessionId), ("$run", executionId));
    return LlmSelectionJson.Read(command.ExecuteScalar() as string);
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
