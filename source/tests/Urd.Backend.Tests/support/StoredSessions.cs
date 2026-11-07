using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Urd.Backend.Tests.Support;

// Reads persisted session identity rows directly from a test database.
internal static class StoredSessions {
  internal static List<StoredSessionRow> Read(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT session_id, history_revision FROM sessions ORDER BY session_id;";
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Cast<DbDataRecord>()
        .Select(row => new StoredSessionRow(Guid.Parse(row.GetString(0)), row.GetInt64(1)))
        .ToList();
  }

  internal static List<StoredSessionCreation> ReadCreation(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT session_id, ordinal FROM session_creation ORDER BY ordinal;";
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Cast<DbDataRecord>()
        .Select(row => new StoredSessionCreation(Guid.Parse(row.GetString(0)), row.GetInt64(1)))
        .ToList();
  }
}

internal sealed record StoredSessionCreation(Guid Id, long Ordinal);

// Persisted session row: identity plus committed history revision.
internal sealed record StoredSessionRow(Guid Id, long HistoryRevision);
