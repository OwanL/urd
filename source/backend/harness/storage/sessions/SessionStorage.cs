using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Creates and reads durable session identities, creation order and history revisions.
internal sealed class SessionStorage(Database database) {
  internal SessionRecord CreatePrimary(Guid sessionId, string? workingDirectory = null,
      IReadOnlyList<Guid>? subagentProviders = null) =>
      Create(sessionId, workingDirectory, SessionKind.Primary, null, null, subagentProviders);

  internal SessionRecord CreateSubagent(Guid sessionId, Guid parentSessionId, string owningCallId, string? workingDirectory = null) {
    if (parentSessionId == Guid.Empty || parentSessionId == sessionId)
      throw new ArgumentException("Parent session ID must identify a different session.", nameof(parentSessionId));
    ArgumentException.ThrowIfNullOrWhiteSpace(owningCallId);
    return Create(sessionId, workingDirectory, SessionKind.Subagent, parentSessionId, owningCallId, null);
  }

  private SessionRecord Create(Guid sessionId, string? workingDirectory, SessionKind kind, Guid? parentSessionId,
      string? owningCallId, IReadOnlyList<Guid>? subagentProviders) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    return database.Write(transaction => database.QuerySingleOrDefault(transaction, """
        INSERT INTO sessions (session_id, history_revision, working_directory, kind, parent_session_id,
            owning_call_id, subagent_providers_json)
        VALUES ($id, 0, $directory, $kind, $parent, $call, $providers);
        INSERT INTO session_creation (session_id) VALUES ($id);
        SELECT session_id, history_revision, working_directory, kind, parent_session_id, owning_call_id
        FROM sessions WHERE session_id = $id;
        """, ReadRecord, ("$id", sessionId), ("$directory", workingDirectory), ("$kind", (int)kind),
        ("$parent", parentSessionId), ("$call", owningCallId), ("$providers", SerializeProviders(subagentProviders)))!);
  }

  internal SessionIdPage ListPrimaryIds(string? cursor) {
    long boundary = 0;
    if (cursor is not null && (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out boundary)
        || boundary <= 0))
      throw new ArgumentException("Session cursor must be a positive Int64 decimal string.", nameof(cursor));

    return database.RunLocked(() => {
      LlmSelection? latestSelection = cursor is null ? ReadLatestHumanSelection() : null;
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
      return new SessionIdPage(ids, nextCursor, latestSelection);
    });
  }

  private LlmSelection? ReadLatestHumanSelection() => LlmSelectionJson.Read(database.Scalar<string?>(null, """
      SELECT COALESCE(r.llm_selection_json, d.llm_selection_json)
      FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
      LEFT JOIN input_decisions d ON d.session_id = r.session_id AND d.input_id = r.input_id
      WHERE s.kind = $primary
          AND (r.kind = $continuation OR (r.kind = $userInput AND EXISTS (
              SELECT 1 FROM execution_inputs i
              WHERE i.session_id = r.session_id AND i.run_id = r.run_id AND i.input_id = r.input_id
                  AND i.provenance = $human)))
      ORDER BY r.ordinal DESC LIMIT 1;
      """, ("$primary", (int)SessionKind.Primary), ("$continuation", (int)ExecutionRunKind.Continuation),
      ("$userInput", (int)ExecutionRunKind.UserInput), ("$human", (int)InputProvenance.Human)));

  internal SessionRecord? Read(Guid sessionId) => database.RunLocked(() => database.QuerySingleOrDefault(null,
      "SELECT session_id, history_revision, working_directory, kind, parent_session_id, owning_call_id FROM sessions WHERE session_id = $id;",
      ReadRecord, ("$id", sessionId)));

  // All descendants consult the primary's current policy, not a stale copy taken at delegation.
  internal Guid[]? ReadSubagentProviders(Guid sessionId) => database.RunLocked(() => {
    string? json = database.QuerySingleOrDefault(null, """
        WITH RECURSIVE ancestors (session_id, parent_session_id, subagent_providers_json) AS (
          SELECT session_id, parent_session_id, subagent_providers_json FROM sessions WHERE session_id = $session
          UNION ALL
          SELECT s.session_id, s.parent_session_id, s.subagent_providers_json
          FROM sessions s JOIN ancestors a ON s.session_id = a.parent_session_id
        )
        SELECT subagent_providers_json FROM ancestors WHERE parent_session_id IS NULL;
        """, row => row.NullableString("subagent_providers_json"), ("$session", sessionId));
    return json is null ? null : JsonSerializer.Deserialize<Guid[]>(json);
  });

  internal void SetSubagentProviders(Guid sessionId, Guid[]? connectionIds) => database.Write(transaction => {
    if (database.Execute(transaction, """
        UPDATE sessions SET subagent_providers_json = $providers WHERE session_id = $session AND kind = $kind;
        """, ("$providers", SerializeProviders(connectionIds)), ("$session", sessionId), ("$kind", (int)SessionKind.Primary)) == 0)
      throw new KeyNotFoundException($"Primary session {sessionId} does not exist.");
    return true;
  });

  private static string? SerializeProviders(IReadOnlyList<Guid>? ids) => ids is null ? null : JsonSerializer.Serialize(ids);

  // Read title and generation state together so a completed attempt is never shown as a placeholder.
  internal (string? Title, bool Generating) ReadAgentTitle(Guid sessionId) => database.RunLocked(() =>
      database.QuerySingleOrDefault(null, """
        SELECT t.title, EXISTS(SELECT 1 FROM session_prepass_details d
            WHERE d.session_id = s.session_id AND d.category = 'title' AND d.state = 'running') AS generating
        FROM sessions s LEFT JOIN session_prepass_title t ON t.session_id = s.session_id
        WHERE s.session_id = $id;
        """, row => (row.NullableString("title"), row.Bool("generating")), ("$id", sessionId)));

  internal bool HasAcceptedInput(Guid sessionId) => database.RunLocked(() => database.Scalar<bool>(null, """
      SELECT EXISTS(SELECT 1 FROM input_decisions WHERE session_id = $session AND decision = 1);
      """, ("$session", sessionId)));

  // Finished children keep their final title in storage; only subagent records match, because
  // primary titles from other runs are not live addresses for this manager.
  internal IReadOnlyList<Guid> ReadSubagentIdsByTitle(string title) => database.RunLocked(() => database.Query(null,
      """
      SELECT s.session_id FROM sessions s JOIN session_prepass_title t ON t.session_id = s.session_id
      WHERE s.kind = $kind AND t.title = $title;
      """, row => row.Guid("session_id"), ("$kind", (int)SessionKind.Subagent), ("$title", title)).ToArray());

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
