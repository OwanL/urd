using System.Globalization;
using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Creates and reads durable session identities, creation order and history revisions.
internal sealed class SessionStorage(Database database) {
  internal SessionRecord CreatePrimary(Guid sessionId, string? workingDirectory = null) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    return database.Write(transaction => database.QuerySingleOrDefault(transaction, """
        INSERT INTO sessions (session_id, history_revision, working_directory) VALUES ($id, 0, $directory);
        INSERT INTO session_creation (session_id) VALUES ($id);
        SELECT session_id, history_revision, working_directory FROM sessions WHERE session_id = $id;
        """, ReadRecord, ("$id", sessionId), ("$directory", workingDirectory))!);
  }

  internal SessionIdPage ListPrimaryIds(string? cursor) {
    long boundary = 0;
    if (cursor is not null && (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out boundary)
        || boundary <= 0))
      throw new ArgumentException("Session cursor must be a positive Int64 decimal string.", nameof(cursor));

    return database.RunLocked(() => {
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
        ids.Add(reader.Guid("session_id"));
        lastOrdinal = reader.Long("ordinal");
      }

      string? nextCursor = reader.Read() ? lastOrdinal.ToString(CultureInfo.InvariantCulture) : null;
      return new SessionIdPage(ids, nextCursor);
    });
  }

  internal SessionRecord? Read(Guid sessionId) => database.RunLocked(() => database.QuerySingleOrDefault(null,
      "SELECT session_id, history_revision, working_directory FROM sessions WHERE session_id = $id;",
      ReadRecord, ("$id", sessionId)));

  internal bool HasAcceptedInput(Guid sessionId) => database.RunLocked(() => database.Scalar<bool>(null, """
      SELECT EXISTS(SELECT 1 FROM input_decisions WHERE session_id = $session AND decision = 1);
      """, ("$session", sessionId)));

  internal void SetWorkingDirectory(Guid sessionId, string workingDirectory) => database.Write(transaction => {
    if (database.Execute(transaction, """
        UPDATE sessions SET working_directory = $directory WHERE session_id = $session;
        """, ("$directory", workingDirectory), ("$session", sessionId)) == 0)
      throw new KeyNotFoundException($"Session {sessionId} does not exist.");
    return true;
  });

  private static SessionRecord ReadRecord(SqliteDataReader row)
      => new(row.Guid("session_id"), row.Long("history_revision"), row.NullableString("working_directory"));
}
