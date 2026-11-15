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
        WHERE session_id = $session AND history_revision = $revision AND ordinal < $first) AS has_before,
        EXISTS (SELECT 1 FROM execution_messages
        WHERE session_id = $session AND history_revision = $revision AND ordinal > $last) AS has_after;
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
    long revision = database.Scalar<long?>(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId))
        ?? throw new KeyNotFoundException("The session does not exist.");
    if (revision != expectedRevision)
      throw new HistoryRevisionConflictException(expectedRevision, revision);
    return new(database, transaction, sessionId, revision);
  }

  internal long OrdinalOf(TranscriptEntryRef anchor) => database.Scalar<long?>(transaction, AnchorSql,
      ("$session", sessionId), ("$revision", revision),
      ("$run", anchor.ExecutionId), ("$kind", (int)anchor.Kind), ("$entry", anchor.EntryId))
      ?? throw new KeyNotFoundException("The entry does not belong to this session history.");

  internal List<TranscriptHistoryRow> Latest(int count) => Window(long.MaxValue, ascending: false, count);

  internal List<TranscriptHistoryRow> Before(long anchor, int count) => Window(anchor - 1, ascending: false, count);

  internal List<TranscriptHistoryRow> After(long anchor, int count) => StartingAt(anchor + 1, count);

  internal List<TranscriptHistoryRow> StartingAt(long anchor, int count) => Window(anchor, ascending: true, count);

  internal (bool Before, bool After) Neighbors(long first, long last) =>
      database.QuerySingleOrDefault(transaction, LookaheadSql, row => (row.Bool("has_before"), row.Bool("has_after")),
          ("$session", sessionId), ("$revision", revision), ("$first", first), ("$last", last));

  private List<TranscriptHistoryRow> Window(long boundary, bool ascending, int count) =>
      database.Query(transaction, WindowSql(ascending), ReadRow,
          ("$session", sessionId), ("$revision", revision), ("$boundary", boundary), ("$limit", count));

  private TranscriptHistoryRow ReadRow(SqliteDataReader reader) {
    var reference = new TranscriptEntryRef(reader.Guid("run_id"), reader.Guid("message_id"),
        (TranscriptEntryKind)reader.Int("role"), revision);
    bool isTool = reference.Kind is TranscriptEntryKind.Tool or TranscriptEntryKind.ToolCompletion;
    bool isCompaction = reference.Kind == TranscriptEntryKind.Compaction;
    TranscriptTextFacts? text = isTool || isCompaction ? null : ReadTextFacts(reader, reference);
    TranscriptToolFacts? tool = isTool ? ReadToolFacts(reader) : null;
    TranscriptCompactionFacts? compaction = isCompaction ? new(
        (CompactionReason)reader.Int("compaction_reason"), reader.Long("tokens_before"), reader.Long("tokens_after")) : null;
    var descriptor = new TranscriptEntryDescriptor(reference, reader.NullableTimestamp("created_at"), text, tool, compaction);
    return new(reader.Long("ordinal"), descriptor, (ExecutionOutcome?)reader.NullableInt("execution_outcome"),
        reader.NullableString("execution_error"));
  }

  private static TranscriptTextFacts ReadTextFacts(SqliteDataReader reader, TranscriptEntryRef entry) {
    bool isUser = entry.Kind == TranscriptEntryKind.User;
    var message = new MessageRef(entry.ExecutionId, entry.EntryId,
        isUser ? MessageRole.User : MessageRole.Assistant, entry.HistoryRevision);
    if (isUser)
      return new(new(message, 0, 0), new(message, 1, 0), IsClosed: true, FinalOutputCommitted: false);

    long firstChunk = reader.Long("first_chunk_index");
    long nextChunk = reader.Long("next_chunk_index");
    long? savedEndChunk = reader.NullableLong("end_chunk_index");
    long endChunk = savedEndChunk ?? nextChunk;
    bool final = reader.Bool("response_final") && endChunk == nextChunk;
    bool closed = savedEndChunk is not null || final || reader.NullableInt("request_outcome") is not null
        || reader.Bool("stopping") || reader.NullableInt("execution_outcome") is not null;
    return new(new(message, firstChunk, 0), new(message, endChunk, 0), closed, final);
  }

  private static TranscriptToolFacts ReadToolFacts(SqliteDataReader reader) {
    bool authorized = reader.NullableInt("started") is not null;
    bool started = authorized && reader.Bool("started");
    bool executionSettled = reader.NullableInt("execution_outcome") is not null;
    bool executionStopping = reader.Bool("stopping");
    bool requestSettled = reader.NullableInt("request_outcome") is not null;
    ExecutionOutcome? outcome = (ExecutionOutcome?)reader.NullableInt("tool_outcome");
    bool closed = outcome is not null || executionSettled || (!started && executionStopping)
        || (!authorized && requestSettled);
    return new(reader.Guid("request_id"), reader.String("call_id"), reader.NullableString("tool_name"), started,
        reader.Bool("interrupted"), outcome, closed, reader.NullableTimestamp("started_at"), reader.NullableTimestamp("ended_at"),
        reader.Bool("background"), reader.Bool("batched_with_previous"), reader.Bool("batched_with_next"),
        reader.NullableString("arguments_excerpt"));
  }
}

internal sealed record TranscriptHistoryRow(
    long Ordinal, TranscriptEntryDescriptor Descriptor, ExecutionOutcome? Settlement, string? Error);
