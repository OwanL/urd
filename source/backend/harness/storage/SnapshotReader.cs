using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Reads small session facts without loading a transcript window.
internal sealed class SnapshotReader {
  private const int PendingInputLimit = 8;
  private readonly Database database;
  private readonly MessageContentReader content;

  internal static readonly string FirstUserSql = $"""
    SELECT run_id, message_id FROM execution_messages
    WHERE session_id = $session AND history_revision = $revision AND role = {(int)MessageRole.User}
    ORDER BY ordinal LIMIT 1;
    """;

  internal const string PendingInputsSql = """
    SELECT i.input_id, coalesce(substr(d.original_text_utf8, 1, $bytes), X'') AS preview,
        i.request_id IS NULL AND r.stopping = 0 AND (r.input_id IS NULL OR r.input_id <> i.input_id) AS can_cancel
    FROM execution_inputs i
    JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
    JOIN execution_runs r ON r.run_id = i.run_id
    WHERE i.run_id = (
        SELECT run_id FROM execution_runs WHERE session_id = $session AND outcome IS NULL
    ) AND i.outcome IS NULL AND (i.request_id IS NULL OR EXISTS (
        SELECT 1 FROM execution_requests q WHERE q.request_id = i.request_id AND q.attempt = 1))
    ORDER BY i.ordinal LIMIT $limit;
    """;

  internal const string SnapshotUnsettledSql = """
    SELECT run_id, stopping, working_started_at FROM execution_runs
    WHERE session_id = $session AND outcome IS NULL;
    """;

  internal const string ContextUsageSql = """
    SELECT c.compaction_id AS latest_compaction_id, c.tokens_after,
        q.compaction_id AS request_compaction_id, q.usage_json
    FROM (SELECT 1) root
    LEFT JOIN (
        SELECT compaction_id, tokens_after FROM session_compactions
        WHERE session_id = $session AND history_revision = $revision
        ORDER BY ordinal DESC LIMIT 1
    ) c ON 1 = 1
    LEFT JOIN (
        SELECT q.compaction_id, q.usage_json FROM execution_requests q
        WHERE q.run_id = (
            SELECT r.run_id FROM execution_runs r
            WHERE r.session_id = $session AND r.history_revision = $revision
                AND EXISTS (SELECT 1 FROM execution_requests attempted
                    WHERE attempted.run_id = r.run_id AND attempted.attempt = 2)
            ORDER BY r.ordinal DESC LIMIT 1
        ) AND q.attempt = 2
        ORDER BY q.request_index DESC LIMIT 1
    ) q ON 1 = 1;
    """;

  internal static readonly string SnapshotSettlementSql = $"""
    SELECT r.run_id, r.outcome,
        CASE WHEN r.outcome = {(int)ExecutionOutcome.Failed} THEN coalesce(
            (SELECT substr(q.error, 1, 1024) FROM execution_requests q
                WHERE q.run_id = r.run_id AND q.outcome = {(int)ExecutionOutcome.Failed} ORDER BY q.request_index DESC LIMIT 1),
            substr(r.error, 1, 1024)) ELSE NULL END AS error
    FROM execution_runs r
    WHERE r.session_id = $session AND r.outcome IS NOT NULL
    ORDER BY r.ordinal DESC LIMIT 1;
    """;

  internal const string SnapshotSelectionSql = """
    SELECT COALESCE(r.llm_selection_json, d.llm_selection_json) FROM execution_runs r
    LEFT JOIN input_decisions d ON d.session_id = r.session_id AND d.input_id = r.input_id
    WHERE r.session_id = $session AND r.run_id = $run;
    """;

  internal static readonly string CanContinueSql = $"""
    SELECT EXISTS (SELECT 1 FROM execution_runs tail
        WHERE tail.session_id = $session AND tail.history_revision = $revision AND tail.outcome IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM execution_runs later
                WHERE later.session_id = tail.session_id AND later.history_revision = tail.history_revision
                    AND later.ordinal > tail.ordinal)
            AND EXISTS (SELECT 1 FROM execution_runs attempted JOIN execution_requests q ON q.run_id = attempted.run_id
                WHERE attempted.session_id = tail.session_id AND attempted.history_revision = tail.history_revision
                    AND attempted.ordinal <= tail.ordinal AND q.attempt = 2)
            AND (EXISTS (SELECT 1 FROM execution_messages m JOIN execution_requests q ON q.request_id = m.request_id
                    JOIN execution_runs source ON source.run_id = q.run_id
                WHERE m.session_id = tail.session_id AND m.history_revision = tail.history_revision AND m.role = {(int)MessageRole.User}
                    AND source.history_revision = tail.history_revision AND source.ordinal <= tail.ordinal
                    AND q.attempt = 2)
                OR EXISTS (SELECT 1 FROM session_compactions c
                    WHERE c.session_id = tail.session_id AND c.history_revision = tail.history_revision)));
    """;

  internal SnapshotReader(Database database, MessageContentReader content) {
    this.database = database;
    this.content = content;
  }

  // Reads stored facts only. SessionBase supplies live admission and Stop state before exposure.
  internal SessionSnapshot ReadPersisted(Guid sessionId) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    return database.Read(transaction => Read(transaction, sessionId));
  }

  private SessionSnapshot Read(SqliteTransaction transaction, Guid sessionId) {
    long historyRevision = ReadHistoryRevision(transaction, sessionId);
    (Guid? unsettledExecution, bool stopRequested, DateTimeOffset? startedAt) =
        ReadUnsettledExecution(transaction, sessionId);
    SessionExecutionResult? lastSettlement = ReadLastSettlement(transaction, sessionId);
    LlmSelection? latestSelection = ReadLatestSelection(
        transaction, sessionId, unsettledExecution ?? lastSettlement?.ExecutionId);
    string? firstUserPreview = ReadFirstUserPreview(transaction, sessionId, historyRevision);
    PendingInputPreview[] pendingInputs = ReadPendingInputs(transaction, sessionId);

    // Storage cannot establish live admission eligibility; the session replaces this fence.
    return new(sessionId, historyRevision, AdmissionFenced: true, CanChangeWorkingDirectory: false,
        unsettledExecution, stopRequested, lastSettlement, firstUserPreview, pendingInputs, latestSelection,
        WorkingTime: ReadWorkingTime(transaction, sessionId, startedAt),
        ContextUsage: ReadContextUsage(transaction, sessionId, historyRevision),
        CanContinue: ReadCanContinue(transaction, sessionId, historyRevision));
  }

  private long ReadHistoryRevision(SqliteTransaction transaction, Guid sessionId) =>
      database.Scalar<long?>(transaction,
          "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId))
          ?? throw new KeyNotFoundException($"Session {sessionId} does not exist.");

  private (Guid? ExecutionId, bool StopRequested, DateTimeOffset? StartedAt) ReadUnsettledExecution(
      SqliteTransaction transaction, Guid sessionId) =>
      database.QuerySingleOrDefault(transaction, SnapshotUnsettledSql,
          row => ((Guid?)row.Guid("run_id"), row.Bool("stopping"), row.NullableTimestamp("working_started_at")),
          ("$session", sessionId));

  private bool ReadCanContinue(SqliteTransaction transaction, Guid sessionId, long historyRevision) =>
      database.Scalar<bool>(transaction, CanContinueSql,
          ("$session", sessionId), ("$revision", historyRevision));

  private SessionContextUsage? ReadContextUsage(SqliteTransaction transaction, Guid sessionId, long historyRevision) {
    using SqliteCommand read = database.Command(transaction, ContextUsageSql,
        ("$session", sessionId), ("$revision", historyRevision));
    using SqliteDataReader reader = read.ExecuteReader();
    if (!reader.Read())
      throw new InvalidOperationException("The context usage query did not return its single result row.");

    Guid? latestCompaction = reader.NullableGuid("latest_compaction_id");
    long? compactionTokensAfter = reader.NullableLong("tokens_after");
    Guid? requestCompaction = reader.NullableGuid("request_compaction_id");
    string? usageJson = reader.NullableString("usage_json");
    if (latestCompaction != requestCompaction)
      return compactionTokensAfter is { } tokens ? new(tokens, IsEstimate: true) : null;
    if (usageJson is null)
      return null;

    UsageDetails? usage;
    try {
      usage = JsonSerializer.Deserialize<UsageDetails>(usageJson);
    } catch (JsonException) {
      return null;
    }
    if (usage?.InputTokenCount is not >= 0 || usage.OutputTokenCount is not >= 0)
      return null;

    return new(ContextTokenBudget.AddSaturating(usage.InputTokenCount.Value, usage.OutputTokenCount.Value), IsEstimate: false);
  }

  private SessionWorkingTime ReadWorkingTime(SqliteTransaction transaction, Guid sessionId, DateTimeOffset? startedAt) =>
      database.QuerySingleOrDefault(transaction, """
        SELECT completed_working_ms, working_time_incomplete FROM sessions WHERE session_id = $session;
        """, row => new SessionWorkingTime(row.Long("completed_working_ms"), startedAt, row.Bool("working_time_incomplete")),
          ("$session", sessionId)) ?? throw new KeyNotFoundException($"Session {sessionId} does not exist.");

  private SessionExecutionResult? ReadLastSettlement(SqliteTransaction transaction, Guid sessionId) =>
      database.QuerySingleOrDefault(transaction, SnapshotSettlementSql,
          row => new SessionExecutionResult(row.Guid("run_id"), (ExecutionOutcome)row.Int("outcome"), row.NullableString("error")),
          ("$session", sessionId));

  private LlmSelection? ReadLatestSelection(SqliteTransaction transaction, Guid sessionId, Guid? executionId) {
    if (executionId is null)
      return null;

    // The initiating submission owns selection even when it is outside the message window.
    return LlmSelectionJson.Read(database.Scalar<string?>(transaction, SnapshotSelectionSql,
        ("$session", sessionId), ("$run", executionId)));
  }

  private string? ReadFirstUserPreview(SqliteTransaction transaction, Guid sessionId, long historyRevision) {
    MessageRef? message = database.QuerySingleOrDefault(transaction, FirstUserSql,
        row => new MessageRef(row.Guid("run_id"), row.Guid("message_id"), MessageRole.User, historyRevision),
        ("$session", sessionId), ("$revision", historyRevision));
    return message is null ? null : content.ReadPreview(transaction, sessionId, message, StorageText.PreviewUtf16Limit).Text;
  }

  private PendingInputPreview[] ReadPendingInputs(SqliteTransaction transaction, Guid sessionId) =>
      database.Query(transaction, PendingInputsSql,
          row => new PendingInputPreview(row.Guid("input_id"),
              StorageText.ReadPreview((byte[])row.GetValue(row.GetOrdinal("preview"))), row.Bool("can_cancel")),
          ("$session", sessionId), ("$limit", PendingInputLimit), ("$bytes", StorageText.PreviewByteLimit)).ToArray();
}
