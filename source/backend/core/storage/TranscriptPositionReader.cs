using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Source-byte weights deliberately avoid parsing, width-dependent geometry and body reads.
// Each entry costs 256 units; text adds its exact UTF-8 byte count. Tools stay compact,
// independent of argument/result size. Streaming adds bytes only to the active tail.
internal sealed class TranscriptPositionReader(Database database, TranscriptHistoryReader history) {
  internal const string LocateSql = """
    SELECT p.rank, p.weight, p.prefix_weight, m.run_id, m.message_id, p.kind
    FROM transcript_positions p JOIN execution_messages m ON m.ordinal = p.entry_ordinal
    WHERE p.session_id = $session AND p.history_revision = $revision AND p.prefix_weight <= $offset
    ORDER BY p.prefix_weight DESC LIMIT 1;
    """;
  internal const string AnchorSql = """
    SELECT p.rank, p.weight, p.prefix_weight, m.run_id, m.message_id, p.kind
    FROM execution_messages m JOIN transcript_positions p ON p.entry_ordinal = m.ordinal
    WHERE m.run_id = $run AND m.role = 0 AND m.message_id = $entry
        AND m.session_id = $session AND m.history_revision = $revision;
    """;
  internal const string PromptsSql = """
    SELECT p.rank, p.weight, p.prefix_weight, m.run_id, m.message_id, p.kind
    FROM transcript_positions p JOIN execution_messages m ON m.ordinal = p.entry_ordinal
    WHERE p.session_id = $session AND p.history_revision = $revision AND p.kind = 0 AND p.rank > $after
    ORDER BY p.rank LIMIT 61;
    """;
  internal const string WindowSql = """
    SELECT p.rank, p.weight, p.prefix_weight, m.run_id, m.message_id, p.kind
    FROM transcript_positions p JOIN execution_messages m ON m.ordinal = p.entry_ordinal
    WHERE p.session_id = $session AND p.history_revision = $revision AND p.rank >= $first
    ORDER BY p.rank LIMIT 60;
    """;

  // Offset is a nonnegative abstract weight offset. Stale totals/end offsets clamp to
  // the current tail; state tags the result with the version actually read. Only the
  // history revision fences identity. PositionVersion changes even on same-revision appends.
  internal TranscriptPositionWindow Locate(Guid sessionId, long expectedHistoryRevision, long weightOffset) {
    Validate(sessionId, expectedHistoryRevision);
    ArgumentOutOfRangeException.ThrowIfNegative(weightOffset);
    return database.Read(transaction => {
      TranscriptPositionState state = State(transaction, sessionId, expectedHistoryRevision);
      if (state.EntryCount == 0)
        return new(state, null, [], false, false, []);
      using SqliteCommand command = database.Command(transaction, LocateSql,
          ("$session", sessionId), ("$revision", state.HistoryRevision),
          ("$offset", Math.Min(weightOffset, state.TotalWeight - 1)));
      TranscriptPosition target;
      using (SqliteDataReader reader = command.ExecuteReader()) {
        reader.Read();
        target = Position(reader, state.HistoryRevision);
      }
      return Around(transaction, state, target);
    });
  }

  internal TranscriptPromptPage Prompts(Guid sessionId, long expectedHistoryRevision, long? afterRank = null) {
    Validate(sessionId, expectedHistoryRevision);
    if (afterRank is < 0)
      throw new ArgumentOutOfRangeException(nameof(afterRank));
    return database.Read<TranscriptPromptPage>(transaction => {
      TranscriptPositionState state = State(transaction, sessionId, expectedHistoryRevision);
      using SqliteCommand command = database.Command(transaction, PromptsSql,
          ("$session", sessionId), ("$revision", state.HistoryRevision), ("$after", afterRank ?? -1));
      using SqliteDataReader reader = command.ExecuteReader();
      var prompts = new List<TranscriptPosition>(60);
      while (prompts.Count < 60 && reader.Read())
        prompts.Add(Position(reader, state.HistoryRevision));
      long? next = reader.Read() ? prompts[^1].Rank : null;
      return new(state, prompts, next);
    });
  }

  // Exact identity resolution, state, weights and descriptors share one read snapshot.
  // Marker approximation never substitutes a neighboring entry for the requested user.
  internal TranscriptPositionWindow ActivatePrompt(Guid sessionId, long expectedHistoryRevision,
      TranscriptEntryRef prompt) {
    Validate(sessionId, expectedHistoryRevision);
    ArgumentNullException.ThrowIfNull(prompt);
    if (prompt.Kind != TranscriptEntryKind.User || prompt.ExecutionId == Guid.Empty || prompt.EntryId == Guid.Empty
        || prompt.HistoryRevision != expectedHistoryRevision)
      throw new ArgumentException("Prompt activation requires an exact user entry in the expected revision.", nameof(prompt));
    return database.Read(transaction => {
      TranscriptPositionState state = State(transaction, sessionId, expectedHistoryRevision);
      using SqliteCommand command = database.Command(transaction, AnchorSql,
          ("$run", prompt.ExecutionId), ("$entry", prompt.EntryId),
          ("$session", sessionId), ("$revision", state.HistoryRevision));
      TranscriptPosition target;
      using (SqliteDataReader reader = command.ExecuteReader()) {
        if (!reader.Read())
          throw new KeyNotFoundException("The prompt does not belong to this session history.");
        target = Position(reader, state.HistoryRevision);
      }
      return Around(transaction, state, target);
    });
  }

  private TranscriptPositionWindow Around(SqliteTransaction transaction, TranscriptPositionState state,
      TranscriptPosition target) {
    TranscriptHistoryPage page = history.Read(transaction, state.SessionId,
        new(TranscriptHistoryMode.Around, state.HistoryRevision, target.Entry));
    long first = Math.Max(0, Math.Min(target.Rank - 29, state.EntryCount - 60));
    using SqliteCommand command = database.Command(transaction, WindowSql,
        ("$session", state.SessionId), ("$revision", state.HistoryRevision), ("$first", first));
    using SqliteDataReader reader = command.ExecuteReader();
    var entries = new List<TranscriptPositionedEntry>(page.Entries.Count);
    foreach (TranscriptEntryDescriptor descriptor in page.Entries) {
      reader.Read();
      TranscriptPosition position = Position(reader, state.HistoryRevision);
      if (position.Entry != descriptor.Entry)
        throw new InvalidOperationException("Transcript positions do not match history order.");
      entries.Add(new(descriptor, position));
    }
    return new(state, target, entries, page.HasBefore, page.HasAfter, page.Settlements);
  }

  private TranscriptPositionState State(SqliteTransaction transaction, Guid session, long expectedRevision) {
    using SqliteCommand command = database.Command(transaction, """
      SELECT s.history_revision, s.position_version, COALESCE(t.entry_count, 0), COALESCE(t.total_weight, 0)
      FROM sessions s LEFT JOIN transcript_position_totals t
          ON t.session_id = s.session_id AND t.history_revision = s.history_revision
      WHERE s.session_id = $session;
      """, ("$session", session));
    using SqliteDataReader reader = command.ExecuteReader();
    if (!reader.Read())
      throw new KeyNotFoundException("The session does not exist.");
    long revision = reader.GetInt64(0);
    if (revision != expectedRevision)
      throw new HistoryRevisionConflictException(expectedRevision, revision);
    return new(session, revision, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
  }

  private static TranscriptPosition Position(SqliteDataReader reader, long revision) => new(
      new(reader.GetGuid(3), reader.GetGuid(4), (TranscriptEntryKind)reader.GetInt32(5), revision),
      reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));

  private static void Validate(Guid sessionId, long expectedRevision) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("A session identity is required.", nameof(sessionId));
    ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
  }
}
