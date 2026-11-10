using System.Globalization;
using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Creates and reads durable session identities, creation order and history revisions.
internal sealed class SessionStorage(Database database) {
  internal SessionRecord CreatePrimary(Guid sessionId, string? workingDirectory = null) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    return database.Write(transaction => {
      using SqliteCommand command = database.Command(transaction, """
        INSERT INTO sessions (session_id, history_revision, working_directory) VALUES ($id, 0, $directory);
        INSERT INTO session_creation (session_id) VALUES ($id);
        SELECT session_id, history_revision, working_directory FROM sessions WHERE session_id = $id;
        """, ("$id", sessionId), ("$directory", workingDirectory));
      using SqliteDataReader reader = command.ExecuteReader();
      reader.Read();
      return ReadRecord(reader);
    });
  }

  internal SessionIdPage ListPrimaryIds(string? cursor) {
    long boundary = 0;
    if (cursor is not null && (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out boundary)
        || boundary <= 0))
      throw new ArgumentException("Session cursor must be a positive Int64 decimal string.", nameof(cursor));

    return database.Guarded(() => {
      using SqliteCommand command = cursor is null
          ? database.Command(null, "SELECT session_id, ordinal FROM session_creation ORDER BY ordinal DESC LIMIT 61;")
          : database.Command(null, """
              SELECT session_id, ordinal FROM session_creation
              WHERE ordinal < $cursor ORDER BY ordinal DESC LIMIT 61;
              """, ("$cursor", boundary));
      using SqliteDataReader reader = command.ExecuteReader();
      var ids = new List<Guid>(60);
      long lastOrdinal = 0;
      while (ids.Count < 60 && reader.Read()) {
        ids.Add(reader.GetGuid(0));
        lastOrdinal = reader.GetInt64(1);
      }

      string? nextCursor = reader.Read() ? lastOrdinal.ToString(CultureInfo.InvariantCulture) : null;
      return new SessionIdPage(ids, nextCursor);
    });
  }

  internal SessionRecord? Read(Guid sessionId) {
    return database.Guarded<SessionRecord?>(() => {
      using SqliteCommand command = database.Command(null,
          "SELECT session_id, history_revision, working_directory FROM sessions WHERE session_id = $id;", ("$id", sessionId));
      using SqliteDataReader reader = command.ExecuteReader();
      return reader.Read() ? ReadRecord(reader) : null;
    });
  }

  private static SessionRecord ReadRecord(SqliteDataReader reader)
      => new(reader.GetGuid(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2));
}
