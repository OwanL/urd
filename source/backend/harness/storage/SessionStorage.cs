using System.Globalization;
using Microsoft.Data.Sqlite;
using Urd.Core.Context;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Creates and reads durable session identities, creation order and history revisions.
internal sealed class SessionStorage(Database database) {
  internal SessionRecord CreatePrimary(Guid sessionId, string? workingDirectory = null)
      => Create(sessionId, workingDirectory, SessionKind.Primary, null, null);

  internal SessionRecord CreateSubagent(Guid sessionId, Guid parentSessionId, string owningCallId, string? workingDirectory = null) {
    if (parentSessionId == Guid.Empty || parentSessionId == sessionId)
      throw new ArgumentException("Parent session ID must identify a different session.", nameof(parentSessionId));
    ArgumentException.ThrowIfNullOrWhiteSpace(owningCallId);
    return Create(sessionId, workingDirectory, SessionKind.Subagent, parentSessionId, owningCallId);
  }

  private SessionRecord Create(Guid sessionId, string? workingDirectory, SessionKind kind, Guid? parentSessionId, string? owningCallId) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    return database.Write(transaction => database.QuerySingleOrDefault(transaction, """
        INSERT INTO sessions (session_id, history_revision, working_directory, kind, parent_session_id, owning_call_id)
        VALUES ($id, 0, $directory, $kind, $parent, $call);
        INSERT INTO session_creation (session_id) VALUES ($id);
        SELECT session_id, history_revision, working_directory, kind, parent_session_id, owning_call_id
        FROM sessions WHERE session_id = $id;
        """, ReadRecord, ("$id", sessionId), ("$directory", workingDirectory), ("$kind", (int)kind),
        ("$parent", parentSessionId), ("$call", owningCallId))!);
  }

  internal SessionIdPage ListPrimaryIds(string? cursor) {
    long boundary = 0;
    if (cursor is not null && (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out boundary)
        || boundary <= 0))
      throw new ArgumentException("Session cursor must be a positive Int64 decimal string.", nameof(cursor));

    return database.RunLocked(() => {
      using SqliteCommand command = cursor is null
          ? database.Command(null, """
              SELECT c.session_id, c.ordinal FROM session_creation c JOIN sessions s ON s.session_id = c.session_id
              WHERE s.kind = $kind ORDER BY c.ordinal DESC LIMIT 61;
              """, ("$kind", (int)SessionKind.Primary))
          : database.Command(null, """
              SELECT c.session_id, c.ordinal FROM session_creation c JOIN sessions s ON s.session_id = c.session_id
              WHERE s.kind = $kind AND c.ordinal < $cursor ORDER BY c.ordinal DESC LIMIT 61;
              """, ("$kind", (int)SessionKind.Primary), ("$cursor", boundary));
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
      "SELECT session_id, history_revision, working_directory, kind, parent_session_id, owning_call_id FROM sessions WHERE session_id = $id;",
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
      => new(row.Guid("session_id"), row.Long("history_revision"), row.NullableString("working_directory"),
          (SessionKind)row.Int("kind"), row.NullableGuid("parent_session_id"), row.NullableString("owning_call_id"));
}
