using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Compaction;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Metadata-only queries within one transaction, session and verified history revision.
internal sealed class TranscriptHistoryStorage(
    Database database, SqliteTransaction transaction, Guid sessionId, long revision) {
  internal const string AnchorSql = """
    SELECT ordinal FROM execution_messages
    WHERE run_id = $run AND role = $kind AND message_id = $entry
        AND session_id = $session AND history_revision = $revision;
    """;
  internal const string LookaheadSql = """
    SELECT EXISTS (SELECT 1 FROM execution_messages
        WHERE session_id = $session AND history_revision = $revision AND ordinal < $first),
        EXISTS (SELECT 1 FROM execution_messages
        WHERE session_id = $session AND history_revision = $revision AND ordinal > $last);
    """;

  // Select identities before joining metadata; never select body columns.
  internal static string WindowSql(bool ascending) => $$"""
    WITH window AS (
        SELECT ordinal, run_id, role, message_id, request_id,
            first_chunk_index, end_chunk_index, created_at, call_id, tool_name
        FROM execution_messages
        WHERE session_id = $session AND history_revision = $revision
            AND ordinal {{(ascending ? ">=" : "<=")}} $boundary
        ORDER BY ordinal {{(ascending ? "ASC" : "DESC")}} LIMIT $limit
    )
    SELECT m.ordinal, m.run_id, m.role, m.message_id, m.created_at,
        m.request_id, m.first_chunk_index, m.end_chunk_index,
        q.next_chunk_index, q.response_final, q.outcome AS request_outcome,
        r.stopping, r.outcome AS execution_outcome,
        m.call_id, CASE WHEN m.role = 3 THEN t.name ELSE m.tool_name END AS tool_name,
        t.started, CASE WHEN t.call_id IS NOT NULL THEN t.interrupted ELSE 0 END AS interrupted,
        t.outcome AS tool_outcome, t.started_at, t.ended_at,
        CASE WHEN t.call_id IS NOT NULL THEN t.background ELSE 0 END AS background,
        CASE WHEN m.role = 2 AND t.call_id IS NOT NULL AND previous.role = 2
            AND previous.request_id = m.request_id AND previous_tool.call_id IS NOT NULL THEN 1 ELSE 0 END
            AS batched_with_previous,
        CASE WHEN m.role = 2 AND t.call_id IS NOT NULL AND next.role = 2
            AND next.request_id = m.request_id AND next_tool.call_id IS NOT NULL THEN 1 ELSE 0 END
            AS batched_with_next,
        CASE WHEN t.call_id IS NOT NULL THEN t.arguments_excerpt ELSE NULL END AS arguments_excerpt,
        c.reason AS compaction_reason, c.tokens_before, c.tokens_after,
        CASE WHEN r.outcome = 2 THEN coalesce(
            (SELECT substr(f.error, 1, 1024) FROM execution_requests f
                WHERE f.run_id = r.run_id AND f.outcome = 2 ORDER BY f.request_index DESC LIMIT 1),
            substr(r.error, 1, 1024)) ELSE NULL END AS execution_error
    FROM window m
    JOIN execution_runs r ON r.run_id = m.run_id
    LEFT JOIN execution_requests q ON q.request_id = m.request_id
    LEFT JOIN execution_tools t ON t.request_id = m.request_id AND t.call_id = m.call_id
    LEFT JOIN execution_messages previous ON previous.ordinal = (
        SELECT neighbor.ordinal FROM execution_messages neighbor
        WHERE neighbor.session_id = $session AND neighbor.history_revision = $revision
            AND neighbor.ordinal < m.ordinal ORDER BY neighbor.ordinal DESC LIMIT 1)
    LEFT JOIN execution_tools previous_tool
        ON previous_tool.request_id = previous.request_id AND previous_tool.call_id = previous.call_id
    LEFT JOIN execution_messages next ON next.ordinal = (
        SELECT neighbor.ordinal FROM execution_messages neighbor
        WHERE neighbor.session_id = $session AND neighbor.history_revision = $revision
            AND neighbor.ordinal > m.ordinal ORDER BY neighbor.ordinal LIMIT 1)
    LEFT JOIN execution_tools next_tool ON next_tool.request_id = next.request_id AND next_tool.call_id = next.call_id
    LEFT JOIN session_compactions c ON c.compaction_id = m.message_id AND m.role = 4
    ORDER BY m.ordinal {{(ascending ? "ASC" : "DESC")}};
    """;

  internal static TranscriptHistoryStorage Open(Database database, SqliteTransaction transaction,
      Guid sessionId, long expectedRevision) {
    using SqliteCommand command = database.Command(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
    if (command.ExecuteScalar() is not long revision)
      throw new KeyNotFoundException("The session does not exist.");
    if (revision != expectedRevision)
      throw new HistoryRevisionConflictException(expectedRevision, revision);
    return new(database, transaction, sessionId, revision);
  }

  internal long OrdinalOf(TranscriptEntryRef anchor) {
    using SqliteCommand command = Command(AnchorSql,
        ("$run", anchor.ExecutionId), ("$kind", (int)anchor.Kind), ("$entry", anchor.EntryId));
    return command.ExecuteScalar() is long ordinal ? ordinal
        : throw new KeyNotFoundException("The entry does not belong to this session history.");
  }

  internal List<TranscriptHistoryRow> Latest(int count) => Window(long.MaxValue, ascending: false, count);

  internal List<TranscriptHistoryRow> Before(long anchor, int count) => Window(anchor - 1, ascending: false, count);

  internal List<TranscriptHistoryRow> After(long anchor, int count) => StartingAt(anchor + 1, count);

  internal List<TranscriptHistoryRow> StartingAt(long anchor, int count) => Window(anchor, ascending: true, count);

  internal (bool Before, bool After) Neighbors(long first, long last) {
    using SqliteCommand command = Command(LookaheadSql, ("$first", first), ("$last", last));
    using SqliteDataReader reader = command.ExecuteReader();
    reader.Read();
    return (reader.GetBoolean(0), reader.GetBoolean(1));
  }

  private List<TranscriptHistoryRow> Window(long boundary, bool ascending, int count) {
    using SqliteCommand command = Command(WindowSql(ascending), ("$boundary", boundary), ("$limit", count));
    using SqliteDataReader reader = command.ExecuteReader();
    var rows = new List<TranscriptHistoryRow>(count);
    while (reader.Read())
      rows.Add(ReadRow(reader));
    return rows;
  }

  private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters) =>
      database.Command(transaction, sql, [("$session", sessionId), ("$revision", revision), .. parameters]);

  private TranscriptHistoryRow ReadRow(SqliteDataReader reader) {
    var reference = new TranscriptEntryRef(reader.GetGuid("run_id"), reader.GetGuid("message_id"),
        (TranscriptEntryKind)reader.GetInt32("role"), revision);
    bool isTool = reference.Kind is TranscriptEntryKind.Tool or TranscriptEntryKind.ToolCompletion;
    bool isCompaction = reference.Kind == TranscriptEntryKind.Compaction;
    TranscriptTextFacts? text = isTool || isCompaction ? null : ReadTextFacts(reader, reference);
    TranscriptToolFacts? tool = isTool ? ReadToolFacts(reader) : null;
    TranscriptCompactionFacts? compaction = isCompaction ? new(
        (CompactionReason)reader.GetInt32("compaction_reason"), reader.GetInt64("tokens_before"), reader.GetInt64("tokens_after")) : null;
    var descriptor = new TranscriptEntryDescriptor(reference, ReadTime(reader, "created_at"), text, tool, compaction);
    return new(reader.GetInt64("ordinal"), descriptor, ReadOutcome(reader, "execution_outcome"),
        reader.IsDBNull("execution_error") ? null : reader.GetString("execution_error"));
  }

  private static TranscriptTextFacts ReadTextFacts(SqliteDataReader reader, TranscriptEntryRef entry) {
    bool isUser = entry.Kind == TranscriptEntryKind.User;
    var message = new MessageRef(entry.ExecutionId, entry.EntryId,
        isUser ? MessageRole.User : MessageRole.Assistant, entry.HistoryRevision);
    if (isUser)
      return new(new(message, 0, 0), new(message, 1, 0), IsClosed: true, FinalOutputCommitted: false);

    long firstChunk = reader.GetInt64("first_chunk_index");
    long nextChunk = reader.GetInt64("next_chunk_index");
    bool hasEndChunk = !reader.IsDBNull("end_chunk_index");
    long endChunk = hasEndChunk ? reader.GetInt64("end_chunk_index") : nextChunk;
    bool final = reader.GetBoolean("response_final") && endChunk == nextChunk;
    bool closed = hasEndChunk || final || !reader.IsDBNull("request_outcome")
        || reader.GetBoolean("stopping") || !reader.IsDBNull("execution_outcome");
    return new(new(message, firstChunk, 0), new(message, endChunk, 0), closed, final);
  }

  private static TranscriptToolFacts ReadToolFacts(SqliteDataReader reader) {
    bool authorized = !reader.IsDBNull("started");
    bool started = authorized && reader.GetBoolean("started");
    bool executionSettled = !reader.IsDBNull("execution_outcome");
    bool executionStopping = reader.GetBoolean("stopping");
    bool requestSettled = !reader.IsDBNull("request_outcome");
    ExecutionOutcome? outcome = ReadOutcome(reader, "tool_outcome");
    bool closed = outcome is not null || executionSettled || (!started && executionStopping)
        || (!authorized && requestSettled);
    string? name = reader.IsDBNull("tool_name") ? null : reader.GetString("tool_name");
    return new(reader.GetGuid("request_id"), reader.GetString("call_id"), name, started,
        reader.GetBoolean("interrupted"), outcome, closed, ReadTime(reader, "started_at"), ReadTime(reader, "ended_at"),
        reader.GetBoolean("background"),
        reader.GetBoolean("batched_with_previous"), reader.GetBoolean("batched_with_next"),
        reader.IsDBNull("arguments_excerpt") ? null : reader.GetString("arguments_excerpt"));
  }

  private static ExecutionOutcome? ReadOutcome(SqliteDataReader reader, string column) => reader.IsDBNull(column)
      ? null : (ExecutionOutcome)reader.GetInt32(column);

  private static DateTimeOffset? ReadTime(SqliteDataReader reader, string column) => reader.IsDBNull(column)
      ? null : DateTimeOffset.Parse(reader.GetString(column), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

internal sealed record TranscriptHistoryRow(
    long Ordinal, TranscriptEntryDescriptor Descriptor, ExecutionOutcome? Settlement, string? Error);
