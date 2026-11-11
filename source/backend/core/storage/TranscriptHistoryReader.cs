using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Keyset windows select entry identities before joining metadata. No body columns are read.
internal sealed class TranscriptHistoryReader(Database database) {
  internal const int PageSize = 60;
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

  internal static string WindowSql(bool ascending) => $$"""
    WITH window AS (
        SELECT ordinal, run_id, role, message_id, input_id, request_id,
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
        t.started, t.outcome AS tool_outcome, t.started_at, t.ended_at
    FROM window m
    JOIN execution_runs r ON r.run_id = m.run_id
    LEFT JOIN execution_requests q ON q.request_id = m.request_id
    LEFT JOIN execution_tools t ON t.request_id = m.request_id AND t.call_id = m.call_id
    ORDER BY m.ordinal {{(ascending ? "ASC" : "DESC")}};
    """;

  internal TranscriptHistoryPage Read(Guid sessionId, TranscriptHistoryQuery query) {
    Validate(sessionId, query);
    return database.Read(transaction => Read(transaction, sessionId, query));
  }

  internal TranscriptHistoryPage Read(SqliteTransaction transaction, Guid sessionId, TranscriptHistoryQuery query) {
    // Revision fencing always precedes anchor resolution, even when the anchor was deleted.
    long revision = ReadRevision(transaction, sessionId, query.ExpectedHistoryRevision);
    long anchor = query.Anchor is { } reference ? ReadAnchor(transaction, sessionId, reference) : long.MaxValue;
    List<HistoryRow> rows = query.Mode switch {
      TranscriptHistoryMode.Latest => ReadWindow(transaction, sessionId, revision, long.MaxValue, ascending: false, PageSize),
      TranscriptHistoryMode.Before => ReadWindow(transaction, sessionId, revision, anchor - 1, ascending: false, PageSize),
      TranscriptHistoryMode.After => ReadWindow(transaction, sessionId, revision, anchor + 1, ascending: true, PageSize),
      _ => ReadAround(transaction, sessionId, revision, anchor)
    };
    rows.Sort((left, right) => left.Ordinal.CompareTo(right.Ordinal));
    (bool hasBefore, bool hasAfter) = ReadNeighbors(transaction, sessionId, query, rows, anchor);
    var settlements = rows.Where(row => row.Settlement is not null)
        .Select(row => new SessionExecutionResult(row.Entry.Entry.ExecutionId, row.Settlement!.Value))
        .DistinctBy(result => result.ExecutionId).ToArray();
    return new(sessionId, revision, rows.Select(row => row.Entry).ToArray(), hasBefore, hasAfter, settlements);
  }

  private long ReadRevision(SqliteTransaction transaction, Guid sessionId, long expectedRevision) {
    using SqliteCommand command = database.Command(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
    if (command.ExecuteScalar() is not long revision)
      throw new KeyNotFoundException("The session does not exist.");
    if (revision != expectedRevision)
      throw new HistoryRevisionConflictException(expectedRevision, revision);
    return revision;
  }

  private long ReadAnchor(SqliteTransaction transaction, Guid sessionId, TranscriptEntryRef anchor) {
    using SqliteCommand command = database.Command(transaction, AnchorSql,
        ("$run", anchor.ExecutionId), ("$kind", (int)anchor.Kind), ("$entry", anchor.EntryId),
        ("$session", sessionId), ("$revision", anchor.HistoryRevision));
    return command.ExecuteScalar() is long ordinal ? ordinal
        : throw new KeyNotFoundException("The entry does not belong to this session history.");
  }

  private List<HistoryRow> ReadAround(SqliteTransaction transaction, Guid session, long revision, long anchor) {
    const int entriesBeforeAnchor = PageSize / 2 - 1;
    List<HistoryRow> before = ReadWindow(transaction, session, revision, anchor - 1, ascending: false, entriesBeforeAnchor);
    List<HistoryRow> after = ReadWindow(transaction, session, revision, anchor, ascending: true, PageSize - before.Count);
    // If the newer side is short, refill from older history to keep the page full.
    if (after.Count < PageSize - before.Count)
      before = ReadWindow(transaction, session, revision, anchor - 1, ascending: false, PageSize - after.Count);
    before.AddRange(after);
    return before;
  }

  private List<HistoryRow> ReadWindow(SqliteTransaction transaction, Guid session, long revision,
      long boundary, bool ascending, int limit) {
    using SqliteCommand command = database.Command(transaction, WindowSql(ascending),
        ("$session", session), ("$revision", revision), ("$boundary", boundary), ("$limit", limit));
    using SqliteDataReader reader = command.ExecuteReader();
    var rows = new List<HistoryRow>(limit);
    while (reader.Read())
      rows.Add(ReadRow(reader, revision));
    return rows;
  }

  private static HistoryRow ReadRow(SqliteDataReader reader, long revision) {
    var reference = new TranscriptEntryRef(reader.GetGuid("run_id"), reader.GetGuid("message_id"),
        (TranscriptEntryKind)reader.GetInt32("role"), revision);
    bool isTool = reference.Kind is TranscriptEntryKind.Tool or TranscriptEntryKind.ToolCompletion;
    TranscriptTextFacts? text = isTool ? null : ReadTextFacts(reader, reference);
    TranscriptToolFacts? tool = isTool ? ReadToolFacts(reader) : null;
    var entry = new TranscriptEntryDescriptor(reference, ReadTime(reader, "created_at"), text, tool);
    return new(reader.GetInt64("ordinal"), entry, ReadOutcome(reader, "execution_outcome"));
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
    return new(reader.GetGuid("request_id"), reader.GetString("call_id"), name, started, outcome, closed,
        ReadTime(reader, "started_at"), ReadTime(reader, "ended_at"));
  }

  private (bool Before, bool After) ReadNeighbors(SqliteTransaction transaction, Guid session,
      TranscriptHistoryQuery query, List<HistoryRow> rows, long anchor) {
    // For empty strict-neighbor windows, the anchor still defines the side with history.
    long first = rows.Count == 0 ? anchor : rows[0].Ordinal;
    long last = rows.Count == 0 ? anchor : rows[^1].Ordinal;
    using SqliteCommand command = database.Command(transaction, LookaheadSql,
        ("$session", session), ("$revision", query.ExpectedHistoryRevision), ("$first", first), ("$last", last));
    using SqliteDataReader reader = command.ExecuteReader();
    reader.Read();
    bool hasBefore = reader.GetBoolean(0);
    bool hasAfter = reader.GetBoolean(1);
    if (rows.Count == 0 && query.Anchor is not null) {
      hasBefore |= query.Mode == TranscriptHistoryMode.After;
      hasAfter |= query.Mode == TranscriptHistoryMode.Before;
    }
    return (hasBefore, hasAfter);
  }

  private static ExecutionOutcome? ReadOutcome(SqliteDataReader reader, string column) => reader.IsDBNull(column)
      ? null : (ExecutionOutcome)reader.GetInt32(column);

  private static DateTimeOffset? ReadTime(SqliteDataReader reader, string column) => reader.IsDBNull(column)
      ? null : DateTimeOffset.Parse(reader.GetString(column), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

  private static void Validate(Guid session, TranscriptHistoryQuery query) {
    ArgumentNullException.ThrowIfNull(query);
    if (session == Guid.Empty || !Enum.IsDefined(query.Mode))
      throw new ArgumentException("History requires a session and a valid read mode.", nameof(query));
    ArgumentOutOfRangeException.ThrowIfNegative(query.ExpectedHistoryRevision);
    if (query.Mode == TranscriptHistoryMode.Latest) {
      if (query.Anchor is not null)
        throw new ArgumentException("Latest history does not accept an anchor.", nameof(query));
      return;
    }
    if (query.Anchor is not { } anchor || anchor.ExecutionId == Guid.Empty || anchor.EntryId == Guid.Empty
        || !Enum.IsDefined(anchor.Kind) || anchor.HistoryRevision != query.ExpectedHistoryRevision)
      throw new ArgumentException("History requires an exact anchor in the expected revision.", nameof(query));
  }

  private sealed record HistoryRow(long Ordinal, TranscriptEntryDescriptor Entry, ExecutionOutcome? Settlement);
}
