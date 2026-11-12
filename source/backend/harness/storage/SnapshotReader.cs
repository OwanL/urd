using System.Buffers;
using System.Globalization;
using System.Text;
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
  private const int PreviewUtf16Limit = 256;
  private const int PendingInputLimit = 8;
  private readonly Database database;
  private readonly MessageContentReader content;

  internal const string FirstUserSql = """
    SELECT run_id, message_id FROM execution_messages
    WHERE session_id = $session AND history_revision = $revision AND role = 0
    ORDER BY ordinal LIMIT 1;
    """;

  internal const string PendingInputsSql = """
    SELECT i.input_id, coalesce(substr(d.original_text_utf8, 1, $bytes), X'')
    FROM execution_inputs i
    JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
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
    SELECT c.compaction_id, c.tokens_after, q.compaction_id, q.usage_json
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

  internal const string SnapshotSettlementSql = """
    SELECT r.run_id, r.outcome,
        CASE WHEN r.outcome = 2 THEN coalesce(
            (SELECT substr(q.error, 1, 1024) FROM execution_requests q
                WHERE q.run_id = r.run_id AND q.outcome = 2 ORDER BY q.request_index DESC LIMIT 1),
            substr(r.error, 1, 1024)) ELSE NULL END
    FROM execution_runs r
    WHERE r.session_id = $session AND r.outcome IS NOT NULL
    ORDER BY r.ordinal DESC LIMIT 1;
    """;

  internal const string SnapshotSelectionSql = """
    SELECT COALESCE(d.llm_selection_json, r.compaction_selection_json) FROM execution_runs r
    LEFT JOIN input_decisions d ON d.session_id = r.session_id AND d.input_id = r.input_id
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
    (Guid? unsettledExecution, bool stopRequested, DateTimeOffset? startedAt) =
        ReadUnsettledExecution(transaction, sessionId);
    SessionExecutionResult? lastSettlement = ReadLastSettlement(transaction, sessionId);
    LlmSelection? latestSelection = ReadLatestSelection(
        transaction, sessionId, unsettledExecution ?? lastSettlement?.ExecutionId);
    string? firstUserPreview = ReadFirstUserPreview(transaction, sessionId, historyRevision);
    PendingInputPreview[] pendingInputs = ReadPendingInputs(transaction, sessionId);

    // Storage cannot establish live admission eligibility; the session replaces this fence.
    return new(sessionId, historyRevision, AdmissionFenced: true,
        unsettledExecution, stopRequested, lastSettlement, firstUserPreview, pendingInputs, latestSelection,
        WorkingTime: ReadWorkingTime(transaction, sessionId, startedAt),
        ContextUsage: ReadContextUsage(transaction, sessionId, historyRevision));
  }

  private long ReadHistoryRevision(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand revisionRead = database.Command(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
    if (revisionRead.ExecuteScalar() is not long historyRevision)
      throw new KeyNotFoundException($"Session {sessionId} does not exist.");

    return historyRevision;
  }

  private (Guid? ExecutionId, bool StopRequested, DateTimeOffset? StartedAt) ReadUnsettledExecution(
      SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand activeRead = database.Command(transaction, SnapshotUnsettledSql, ("$session", sessionId));
    using SqliteDataReader reader = activeRead.ExecuteReader();
    if (!reader.Read())
      return (null, false, null);

    DateTimeOffset? startedAt = reader.IsDBNull(2) ? null
        : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    return (reader.GetGuid(0), reader.GetBoolean(1), startedAt);
  }

  private SessionContextUsage? ReadContextUsage(SqliteTransaction transaction, Guid sessionId, long historyRevision) {
    using SqliteCommand read = database.Command(transaction, ContextUsageSql,
        ("$session", sessionId), ("$revision", historyRevision));
    using SqliteDataReader reader = read.ExecuteReader();
    if (!reader.Read())
      throw new InvalidOperationException("The context usage query did not return its single result row.");

    Guid? latestCompaction = reader.IsDBNull(0) ? null : reader.GetGuid(0);
    long? compactionTokensAfter = reader.IsDBNull(1) ? null : reader.GetInt64(1);
    Guid? requestCompaction = reader.IsDBNull(2) ? null : reader.GetGuid(2);
    string? usageJson = reader.IsDBNull(3) ? null : reader.GetString(3);
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

    return new(ContextBudget.Add(usage.InputTokenCount.Value, usage.OutputTokenCount.Value), IsEstimate: false);
  }

  private SessionWorkingTime ReadWorkingTime(SqliteTransaction transaction, Guid sessionId, DateTimeOffset? startedAt) {
    using SqliteCommand read = database.Command(transaction, """
      SELECT completed_working_ms, working_time_incomplete FROM sessions WHERE session_id = $session;
      """, ("$session", sessionId));
    using SqliteDataReader reader = read.ExecuteReader();
    if (!reader.Read())
      throw new KeyNotFoundException($"Session {sessionId} does not exist.");

    return new(reader.GetInt64(0), startedAt, reader.GetBoolean(1));
  }

  private SessionExecutionResult? ReadLastSettlement(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand terminalRead = database.Command(transaction, SnapshotSettlementSql,
        ("$session", sessionId));
    using SqliteDataReader reader = terminalRead.ExecuteReader();
    if (!reader.Read())
      return null;

    string? error = reader.IsDBNull(2) ? null : reader.GetString(2);
    return new(reader.GetGuid(0), (ExecutionOutcome)reader.GetInt32(1), error);
  }

  private LlmSelection? ReadLatestSelection(SqliteTransaction transaction, Guid sessionId, Guid? executionId) {
    if (executionId is null)
      return null;

    // The initiating submission owns selection even when it is outside the message window.
    using SqliteCommand command = database.Command(transaction, SnapshotSelectionSql,
        ("$session", sessionId), ("$run", executionId));
    return LlmSelectionJson.Read(command.ExecuteScalar() as string);
  }

  private string? ReadFirstUserPreview(SqliteTransaction transaction, Guid sessionId, long historyRevision) {
    using SqliteCommand firstUser = database.Command(transaction, FirstUserSql,
        ("$session", sessionId), ("$revision", historyRevision));
    using SqliteDataReader reader = firstUser.ExecuteReader();
    if (!reader.Read())
      return null;

    var message = new MessageRef(reader.GetGuid(0), reader.GetGuid(1), MessageRole.User, historyRevision);
    reader.Close();
    return content.ReadPreview(transaction, sessionId, message, PreviewUtf16Limit).Text;
  }

  private PendingInputPreview[] ReadPendingInputs(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand pendingRead = database.Command(transaction, PendingInputsSql,
        ("$session", sessionId), ("$limit", PendingInputLimit),
        ("$bytes", PreviewUtf16Limit * 3 + 3));
    using SqliteDataReader reader = pendingRead.ExecuteReader();
    var inputs = new List<PendingInputPreview>(PendingInputLimit);
    while (reader.Read())
      inputs.Add(new(reader.GetGuid(0), ReadPreview((byte[])reader.GetValue(1))));

    return inputs.ToArray();
  }

  internal static string ReadPreview(byte[] utf8) {
    var text = new StringBuilder(Math.Min(PreviewUtf16Limit, utf8.Length));
    Span<char> scalar = stackalloc char[2];
    int offset = 0;
    while (offset < utf8.Length && text.Length < PreviewUtf16Limit) {
      OperationStatus status = Rune.DecodeFromUtf8(utf8.AsSpan(offset), out Rune rune, out int bytes);
      if (status == OperationStatus.NeedMoreData)
        break;
      if (status != OperationStatus.Done)
        throw new InvalidDataException("A pending input contains invalid UTF-8.");
      if (rune.Utf16SequenceLength > PreviewUtf16Limit - text.Length)
        break;

      int units = rune.EncodeToUtf16(scalar);
      text.Append(scalar[..units]);
      offset += bytes;
    }

    return text.ToString();
  }
}
