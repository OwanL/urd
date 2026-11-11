using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

internal sealed class TranscriptHistoryReader(Database database) {
  internal const int PageSize = 60;

  internal TranscriptHistoryPage Read(Guid sessionId, TranscriptHistoryQuery query) {
    Validate(sessionId, query);
    return database.Read(transaction => Read(transaction, sessionId, query));
  }

  internal TranscriptHistoryPage Read(SqliteTransaction transaction, Guid sessionId, TranscriptHistoryQuery query) {
    // Check the revision before resolving an anchor, which may have been deleted.
    var history = TranscriptHistoryStorage.Open(database, transaction, sessionId, query.ExpectedHistoryRevision);
    long anchor = query.Anchor is { } entry ? history.OrdinalOf(entry) : long.MaxValue;
    List<TranscriptHistoryRow> rows = query.Mode switch {
      TranscriptHistoryMode.Latest => history.Latest(PageSize),
      TranscriptHistoryMode.Before => history.Before(anchor, PageSize),
      TranscriptHistoryMode.After => history.After(anchor, PageSize),
      _ => Around(history, anchor)
    };
    return CreatePage(history, sessionId, query, rows, anchor);
  }

  private static List<TranscriptHistoryRow> Around(TranscriptHistoryStorage history, long anchor) {
    List<TranscriptHistoryRow> before = history.Before(anchor, PageSize / 2 - 1);
    List<TranscriptHistoryRow> after = history.StartingAt(anchor, PageSize - before.Count);
    // Refill from older history when the newer side cannot fill the page.
    if (before.Count + after.Count < PageSize)
      before = history.Before(anchor, PageSize - after.Count);
    return [.. before, .. after];
  }

  private static TranscriptHistoryPage CreatePage(TranscriptHistoryStorage history, Guid sessionId,
      TranscriptHistoryQuery query, List<TranscriptHistoryRow> rows, long anchor) {
    rows.Sort((left, right) => left.Ordinal.CompareTo(right.Ordinal));
    long first = rows.Count == 0 ? anchor : rows[0].Ordinal;
    long last = rows.Count == 0 ? anchor : rows[^1].Ordinal;
    (bool hasBefore, bool hasAfter) = history.Neighbors(first, last);
    // An empty strict-neighbor page still has its anchor on the opposite side.
    if (rows.Count == 0 && query.Anchor is not null) {
      hasBefore |= query.Mode == TranscriptHistoryMode.After;
      hasAfter |= query.Mode == TranscriptHistoryMode.Before;
    }
    var settlements = rows.Where(row => row.Settlement is not null)
        .Select(row => new SessionExecutionResult(row.Descriptor.Entry.ExecutionId, row.Settlement!.Value))
        .DistinctBy(result => result.ExecutionId).ToArray();
    return new(sessionId, query.ExpectedHistoryRevision, rows.Select(row => row.Descriptor).ToArray(),
        hasBefore, hasAfter, settlements);
  }

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
}
