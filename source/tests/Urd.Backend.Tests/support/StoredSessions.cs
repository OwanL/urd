namespace Urd.Backend.Tests.Support;

// Reads persisted session identity rows directly from a test database.
internal static class StoredSessions {
  internal static List<StoredSessionRow> Read(TemporaryDatabase database) => database.ReadRows(
      "SELECT session_id, history_revision FROM sessions ORDER BY session_id;",
      row => new StoredSessionRow(Guid.Parse(row.GetString(0)), row.GetInt64(1)));

}

// Persisted session row: identity plus committed history revision.
internal sealed record StoredSessionRow(Guid Id, long HistoryRevision);
