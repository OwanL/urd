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
        q.next_chunk_index, q.response_final, q.outcome, r.stopping, r.outcome,
        m.call_id, m.tool_name, t.started, t.outcome, t.started_at, t.ended_at
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

  private TranscriptHistoryPage Read(SqliteTransaction transaction, Guid sessionId, TranscriptHistoryQuery query) {
    using SqliteCommand revisionRead = database.Command(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
    if (revisionRead.ExecuteScalar() is not long revision)
      throw new KeyNotFoundException("The session does not exist.");
    // Revision fencing always precedes anchor resolution, even when the anchor was deleted.
    if (revision != query.ExpectedHistoryRevision)
      throw new HistoryRevisionConflictException(query.ExpectedHistoryRevision, revision);

    long anchor = query.Anchor is { } reference ? ReadAnchor(transaction, sessionId, reference) : long.MaxValue;
    List<HistoryRow> rows = query.Mode switch {
      TranscriptHistoryMode.Latest => Window(transaction, sessionId, revision, long.MaxValue, false, PageSize),
      TranscriptHistoryMode.Before => Window(transaction, sessionId, revision, anchor - 1, false, PageSize),
      TranscriptHistoryMode.After => Window(transaction, sessionId, revision, anchor + 1, true, PageSize),
      _ => Around(transaction, sessionId, revision, anchor)
    };
    rows.Sort((left, right) => left.Ordinal.CompareTo(right.Ordinal));
    // For empty strict-neighbor windows, the anchor still defines the side with history.
    long first = rows.Count == 0 ? anchor : rows[0].Ordinal;
    long last = rows.Count == 0 ? anchor : rows[^1].Ordinal;
    (bool hasBefore, bool hasAfter) = Lookahead(transaction, sessionId, revision, first, last);
    if (rows.Count == 0 && query.Anchor is not null) {
      hasBefore |= query.Mode == TranscriptHistoryMode.After;
      hasAfter |= query.Mode == TranscriptHistoryMode.Before;
    }
    var settlements = rows.Where(row => row.Settlement is not null)
        .Select(row => new SessionExecutionResult(row.Entry.Entry.ExecutionId, row.Settlement!.Value))
        .DistinctBy(result => result.ExecutionId).ToArray();
    return new(sessionId, revision, rows.Select(row => row.Entry).ToArray(), hasBefore, hasAfter, settlements);
  }

  private long ReadAnchor(SqliteTransaction transaction, Guid sessionId, TranscriptEntryRef anchor) {
    using SqliteCommand command = database.Command(transaction, AnchorSql,
        ("$run", anchor.ExecutionId), ("$kind", (int)anchor.Kind), ("$entry", anchor.EntryId),
        ("$session", sessionId), ("$revision", anchor.HistoryRevision));
    return command.ExecuteScalar() is long ordinal ? ordinal
        : throw new KeyNotFoundException("The entry does not belong to this session history.");
  }

  private List<HistoryRow> Around(SqliteTransaction transaction, Guid session, long revision, long anchor) {
    List<HistoryRow> before = Window(transaction, session, revision, anchor - 1, false, 29);
    List<HistoryRow> after = Window(transaction, session, revision, anchor, true, PageSize - before.Count);
    if (after.Count < PageSize - before.Count)
      before = Window(transaction, session, revision, anchor - 1, false, PageSize - after.Count);
    before.AddRange(after);
    return before;
  }

  private List<HistoryRow> Window(SqliteTransaction transaction, Guid session, long revision,
      long boundary, bool ascending, int limit) {
    using SqliteCommand command = database.Command(transaction, WindowSql(ascending),
        ("$session", session), ("$revision", revision), ("$boundary", boundary), ("$limit", limit));
    using SqliteDataReader reader = command.ExecuteReader();
    var rows = new List<HistoryRow>(limit);
    while (reader.Read()) {
      long ordinal = reader.GetInt64(0);
      var reference = new TranscriptEntryRef(reader.GetGuid(1), reader.GetGuid(3),
          (TranscriptEntryKind)reader.GetInt32(2), revision);
      DateTimeOffset? created = Time(reader, 4);
      bool settled = !reader.IsDBNull(12);
      ExecutionOutcome? settlement = settled ? (ExecutionOutcome)reader.GetInt32(12) : null;
      bool stopping = reader.GetBoolean(11);
      TranscriptTextFacts? text = null;
      TranscriptToolFacts? tool = null;
      if (reference.Kind == TranscriptEntryKind.Tool) {
        bool authorized = !reader.IsDBNull(15);
        bool started = authorized && reader.GetBoolean(15);
        ExecutionOutcome? outcome = reader.IsDBNull(16) ? null : (ExecutionOutcome)reader.GetInt32(16);
        bool closed = outcome is not null || settled || (!started && stopping)
            || (!authorized && !reader.IsDBNull(10));
        tool = new(reader.GetGuid(5), reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
            started, outcome, closed, Time(reader, 17), Time(reader, 18));
      } else {
        bool user = reference.Kind == TranscriptEntryKind.User;
        var message = new MessageRef(reference.ExecutionId, reference.EntryId,
            user ? MessageRole.User : MessageRole.Assistant, revision);
        long first = user ? 0 : reader.GetInt64(6);
        long end = 1;
        if (!user)
          end = reader.IsDBNull(7) ? reader.GetInt64(8) : reader.GetInt64(7);
        bool final = !user && reader.GetBoolean(9) && end == reader.GetInt64(8);
        bool closed = user || !reader.IsDBNull(7) || final || !reader.IsDBNull(10) || stopping || settled;
        text = new(new(message, first, 0), new(message, end, 0), closed, final);
      }
      rows.Add(new(ordinal, new(reference, created, text, tool), settlement));
    }
    return rows;
  }

  private (bool Before, bool After) Lookahead(SqliteTransaction transaction, Guid session, long revision,
      long first, long last) {
    using SqliteCommand command = database.Command(transaction, LookaheadSql,
        ("$session", session), ("$revision", revision), ("$first", first), ("$last", last));
    using SqliteDataReader reader = command.ExecuteReader();
    reader.Read();
    return (reader.GetBoolean(0), reader.GetBoolean(1));
  }

  private static DateTimeOffset? Time(SqliteDataReader reader, int column) => reader.IsDBNull(column)
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
