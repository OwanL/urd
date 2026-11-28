using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Compaction;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Reads small session facts without loading a transcript window.
internal sealed class SnapshotReader {
  private const int PendingInputLimit = 8;
  private readonly Database database;
  private readonly MessageContentReader content;
  private readonly PrepassDetailsReader prepassDetails;

  internal static readonly string FirstUserSql = $"""
    SELECT run_id, message_id FROM execution_messages
    WHERE session_id = $session AND history_revision = $revision AND role = {(int)MessageRole.User}
    ORDER BY ordinal LIMIT 1;
    """;

  internal const string PendingInputsSql = """
    SELECT i.input_id, coalesce(substr(d.original_text_utf8, 1, $bytes), X'') AS preview,
        i.request_id IS NOT NULL AS is_claimed,
        i.request_id IS NULL AND r.stopping = 0 AND (r.input_id IS NULL OR r.input_id <> i.input_id) AS can_cancel
    FROM execution_inputs i
    JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
    JOIN execution_runs r ON r.run_id = i.run_id
    WHERE i.run_id = $run AND i.outcome IS NULL AND (i.request_id IS NULL OR EXISTS (
        SELECT 1 FROM execution_requests q WHERE q.request_id = i.request_id AND q.attempt = 1))
    ORDER BY i.ordinal LIMIT $limit;
    """;

  internal const string SnapshotUnsettledSql = """
    SELECT r.run_id, r.stopping, r.working_started_at, q.run_id AS queued_run
    FROM execution_runs r LEFT JOIN execution_runs q ON q.queued_after_compaction = r.run_id AND q.outcome IS NULL
    WHERE r.session_id = $session AND r.outcome IS NULL AND r.queued_after_compaction IS NULL;
    """;

  private const string PrepassTitleSql = """
    SELECT title FROM session_prepass_title WHERE session_id = $session;
    """;

  private const string PruningStatusesSql = """
    SELECT category, state, total, retained, error FROM session_pruning_status
    WHERE session_id = $session AND boundary_id = COALESCE((
        SELECT c.compaction_id FROM session_compactions c JOIN sessions s ON s.session_id = c.session_id
        WHERE c.session_id = $session AND c.history_revision = s.history_revision
        ORDER BY c.ordinal DESC LIMIT 1), '00000000-0000-0000-0000-000000000000')
    ORDER BY CASE category WHEN 'tools' THEN 0 WHEN 'skills' THEN 1 ELSE 2 END LIMIT 3;
    """;

  // History editing rebases even superseded request rows; their measurements did not change with it.
  private const string ContextRevisionFloorSql = """
    CASE WHEN $revision = 0 THEN 0 ELSE COALESCE((
        SELECT r.ordinal FROM message_edits e JOIN execution_runs r ON r.run_id = e.run_id
        WHERE e.session_id = $session AND e.history_revision = $revision LIMIT 1), 0) END
    """;

  internal const string ContextUsageSql = $"""
    SELECT c.compaction_id AS latest_compaction_id, c.tokens_after,
        q.compaction_id AS request_compaction_id, q.run_id, q.usage_json
    FROM (SELECT 1) root
    LEFT JOIN (
        SELECT compaction_id, tokens_after FROM session_compactions
        WHERE session_id = $session AND history_revision = $revision
        ORDER BY ordinal DESC LIMIT 1
    ) c ON 1 = 1
    LEFT JOIN (
        SELECT q.compaction_id, q.run_id, q.usage_json FROM execution_requests q
        WHERE q.run_id = (
            SELECT r.run_id FROM execution_runs r
            WHERE r.session_id = $session AND r.history_revision = $revision AND r.ordinal >= ({ContextRevisionFloorSql})
                AND EXISTS (SELECT 1 FROM execution_requests attempted
                    WHERE attempted.run_id = r.run_id AND attempted.attempt = 2)
            ORDER BY r.ordinal DESC LIMIT 1
        ) AND q.attempt = 2
        ORDER BY q.request_index DESC LIMIT 1
    ) q ON 1 = 1;
    """;

  // Bound the fallback even when a compatible provider has never supplied usage.
  private const string RecentContextUsageSql = $"""
    SELECT r.run_id, q.usage_json
    FROM (SELECT run_id, ordinal FROM execution_runs WHERE session_id = $session AND history_revision = $revision
        AND ordinal >= ({ContextRevisionFloorSql}) ORDER BY ordinal DESC LIMIT 16) r
    JOIN execution_requests q ON q.request_id IN (
        SELECT request_id FROM execution_requests WHERE run_id = r.run_id AND attempt = 2
        ORDER BY request_index DESC LIMIT 16)
    WHERE q.compaction_id IS $boundary AND q.usage_json IS NOT NULL
    ORDER BY r.ordinal DESC, q.request_index DESC;
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

  internal SnapshotReader(Database database, MessageContentReader content, PrepassDetailsReader prepassDetails) {
    this.database = database;
    this.content = content;
    this.prepassDetails = prepassDetails;
  }

  // Reads stored facts only. SessionBase supplies live admission and Stop state before exposure.
  internal SessionSnapshot ReadPersisted(Guid sessionId) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    return database.Read(transaction => Read(transaction, sessionId));
  }

  // Child footer facts only: no transcript previews, pending inputs or prepass reads.
  internal (SubagentView? View, Guid? Unsettled, bool Stopping) ReadSubagentView(Guid sessionId, bool live) =>
      database.Read(transaction => {
        var child = database.QuerySingleOrDefault(transaction, """
            SELECT s.history_revision, s.parent_session_id, t.title
            FROM sessions s LEFT JOIN session_prepass_title t ON t.session_id = s.session_id
            WHERE s.session_id = $id AND s.kind = $kind;
            """, row => (Found: true, Revision: row.Long("history_revision"),
                Parent: row.Guid("parent_session_id"), Title: row.NullableString("title") ?? "Subagent"),
            ("$id", sessionId), ("$kind", (int)SessionKind.Subagent));
        if (!child.Found)
          return (null, null, false);
        var (unsettled, stopping, startedAt, _) = ReadUnsettledExecution(transaction, sessionId);
        Guid? latest = unsettled ?? ReadLastSettlement(transaction, sessionId)?.ExecutionId;
        LlmSelection? selection = ReadLatestSelection(transaction, sessionId, latest);
        var view = new SubagentView(child.Revision, live, child.Title, child.Parent,
            selection?.ModelId, selection is null ? null : PrepassDetailsReader.ReadReasoningEffort(selection),
            selection?.ContextWindow, ReadContextUsage(transaction, sessionId, child.Revision, latest, selection),
            ReadWorkingTime(transaction, sessionId, startedAt), AdmissionFenced: true);
        return ((SubagentView?)view, unsettled, stopping);
      });

  private SessionSnapshot Read(SqliteTransaction transaction, Guid sessionId) {
    long historyRevision = ReadHistoryRevision(transaction, sessionId);
    var (unsettledExecution, stopRequested, startedAt, queuedInputExecution) = ReadUnsettledExecution(transaction, sessionId);
    SessionExecutionResult? lastSettlement = ReadLastSettlement(transaction, sessionId);
    Guid? latestExecution = unsettledExecution ?? lastSettlement?.ExecutionId;
    LlmSelection? latestSelection = ReadLatestSelection(transaction, sessionId, latestExecution);
    string? firstUserPreview = ReadFirstUserPreview(transaction, sessionId, historyRevision);
    PendingInputPreview[] pendingInputs = (queuedInputExecution ?? unsettledExecution) is { } pendingRun
        ? ReadPendingInputs(transaction, pendingRun) : [];
    PendingResponse? pendingResponse = ReadPendingResponse(transaction, sessionId, historyRevision,
        unsettledExecution, latestSelection);

    // Storage cannot establish live admission eligibility; the session replaces this fence.
    return new(sessionId, historyRevision, AdmissionFenced: true, CanChangeWorkingDirectory: false,
        unsettledExecution, stopRequested, lastSettlement, firstUserPreview, pendingInputs, latestSelection,
        WorkingTime: ReadWorkingTime(transaction, sessionId, startedAt),
        ContextUsage: ReadContextUsage(transaction, sessionId, historyRevision, latestExecution, latestSelection),
        CanContinue: ReadCanContinue(transaction, sessionId, historyRevision),
        Title: database.Scalar<string?>(transaction, PrepassTitleSql, ("$session", sessionId)),
        Pruning: ReadPruningStatuses(transaction, sessionId), PendingResponse: pendingResponse,
        QueuedInputExecutionId: queuedInputExecution);
  }

  private long ReadHistoryRevision(SqliteTransaction transaction, Guid sessionId) =>
      database.Scalar<long?>(transaction,
          "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId))
          ?? throw new KeyNotFoundException($"Session {sessionId} does not exist.");

  private (Guid? ExecutionId, bool StopRequested, DateTimeOffset? StartedAt, Guid? QueuedInputExecutionId)
      ReadUnsettledExecution(SqliteTransaction transaction, Guid sessionId) =>
      database.QuerySingleOrDefault(transaction, SnapshotUnsettledSql,
          row => ((Guid?)row.Guid("run_id"), row.Bool("stopping"), row.NullableTimestamp("working_started_at"),
              row.NullableGuid("queued_run")),
          ("$session", sessionId));

  private PendingResponse? ReadPendingResponse(SqliteTransaction transaction, Guid sessionId, long historyRevision,
      Guid? unsettledExecution, LlmSelection? selection) {
    if (unsettledExecution is not Guid runId || selection is null || database.Scalar<bool>(transaction, """
        SELECT EXISTS (SELECT 1 FROM execution_messages
            WHERE session_id = $session AND history_revision = $revision AND run_id = $run);
        """, ("$session", sessionId), ("$revision", historyRevision), ("$run", runId)))
      return null;

    Guid boundary = database.QuerySingleOrDefault(transaction, """
        SELECT compaction_id FROM session_compactions
        WHERE session_id = $session AND history_revision = $revision ORDER BY ordinal DESC LIMIT 1;
        """, row => row.Guid("compaction_id"), ("$session", sessionId), ("$revision", historyRevision));
    IReadOnlyList<PrepassSummary> preparation = prepassDetails.ReadRunSummaries(
        transaction, sessionId, boundary, runId);
    if (preparation.Count == 0)
      return null;

    DateTimeOffset? startedAt = database.QuerySingleOrDefault(transaction, """
        SELECT created_at FROM execution_runs WHERE session_id = $session AND run_id = $run;
        """, row => row.NullableTimestamp("created_at"), ("$session", sessionId), ("$run", runId));
    var header = new ResponseHeaderInfo(selection.ModelId,
        PrepassDetailsReader.ReadReasoningEffort(selection), startedAt);
    return new PendingResponse(header, preparation);
  }

  private bool ReadCanContinue(SqliteTransaction transaction, Guid sessionId, long historyRevision) =>
      database.Scalar<bool>(transaction, CanContinueSql,
          ("$session", sessionId), ("$revision", historyRevision));

  private SessionContextUsage? ReadContextUsage(SqliteTransaction transaction, Guid sessionId, long historyRevision,
      Guid? latestExecution, LlmSelection? selection) {
    var latest = database.QuerySingleOrDefault(transaction, ContextUsageSql, row => (
        Boundary: row.NullableGuid("latest_compaction_id"), CompactedTokens: row.NullableLong("tokens_after"),
        RequestBoundary: row.NullableGuid("request_compaction_id"), Run: row.NullableGuid("run_id"),
        Tokens: ReadReportedTokens(row.NullableString("usage_json"))), ("$session", sessionId), ("$revision", historyRevision));
    SessionContextUsage? compacted = latest.CompactedTokens is long estimate
        ? new(estimate, SessionContextUsageSource.Estimated) : null;
    if (latest.Run is not Guid latestRun || latest.Boundary != latest.RequestBoundary || selection is null)
      return compacted;

    if (latest.Tokens is long measured) {
      if (latestRun == latestExecution)
        return new(measured, SessionContextUsageSource.Measured);
      return CanReuse(latestRun) ? new(measured, SessionContextUsageSource.LastMeasured) : compacted;
    }

    (Guid Run, long? Tokens) compatible = RecentMeasurement(transaction, sessionId, historyRevision, latest.Boundary);
    return compatible.Tokens is long earlier && CanReuse(compatible.Run)
        ? new(earlier, SessionContextUsageSource.LastMeasured) : compacted;

    bool CanReuse(Guid run) => run == latestExecution || (ReadLatestSelection(transaction, sessionId, run) is { } earlierSelection
        && earlierSelection.ModelId == selection.ModelId && earlierSelection.EffectiveApi == selection.EffectiveApi
        && ProviderManager.SameBinding(earlierSelection.Connection, selection.Connection));
  }

  private (Guid Run, long? Tokens) RecentMeasurement(SqliteTransaction transaction, Guid sessionId, long revision, Guid? boundary) {
    using SqliteCommand read = database.Command(transaction, RecentContextUsageSql,
        ("$session", sessionId), ("$revision", revision), ("$boundary", boundary));
    using SqliteDataReader reader = read.ExecuteReader();
    while (reader.Read()) {
      if (ReadReportedTokens(reader.NullableString("usage_json")) is long tokens)
        return (reader.Guid("run_id"), tokens);
    }
    return default;
  }

  private static long? ReadReportedTokens(string? json) {
    if (json is null) return null;

    try {
      UsageDetails? usage = JsonSerializer.Deserialize<UsageDetails>(json);
      return usage?.InputTokenCount is >= 0 && usage.OutputTokenCount is >= 0
          ? ContextTokenBudget.AddSaturating(usage.InputTokenCount.Value, usage.OutputTokenCount.Value) : null;
    } catch (JsonException) {
      return null;
    }
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

  private PendingInputPreview[] ReadPendingInputs(SqliteTransaction transaction, Guid runId) =>
      database.Query(transaction, PendingInputsSql,
          row => new PendingInputPreview(row.Guid("input_id"),
              StorageText.ReadPreview((byte[])row.GetValue(row.GetOrdinal("preview"))),
              row.Bool("can_cancel"), row.Bool("is_claimed")),
          ("$run", runId), ("$limit", PendingInputLimit), ("$bytes", StorageText.PreviewByteLimit)).ToArray();

  private System.Collections.ObjectModel.ReadOnlyCollection<StartingResourcePruningStatus>? ReadPruningStatuses(
      SqliteTransaction transaction, Guid sessionId) {
    StartingResourcePruningStatus[] statuses = database.Query(transaction, PruningStatusesSql,
        row => new StartingResourcePruningStatus(row.String("category"), row.String("state"),
            row.Int("total"), row.Int("retained"), row.NullableString("error")),
        ("$session", sessionId)).ToArray();
    return statuses.Length == 0 ? null : Array.AsReadOnly(statuses);
  }
}
