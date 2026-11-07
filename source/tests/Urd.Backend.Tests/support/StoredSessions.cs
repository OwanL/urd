using Microsoft.Data.Sqlite;

namespace Urd.Backend.Tests.Support;

// Reads persisted session identity rows directly from a test database.
internal static class StoredSessions {
  internal static List<StoredSessionRow> Read(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT session_id, history_revision FROM sessions ORDER BY session_id;";
    using SqliteDataReader reader = command.ExecuteReader();
    var rows = new List<StoredSessionRow>();
    while (reader.Read())
      rows.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1)));

    return rows;
  }
}

// Persisted session row: identity plus committed history revision.
internal sealed record StoredSessionRow(Guid Id, long HistoryRevision);
