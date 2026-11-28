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
      Create(sessionId, workingDirectory, SessionKind.Primary, null, null, null, subagentProviders);

  internal SessionRecord CreateSubagent(Guid sessionId, Guid parentSessionId, string owningCallId,
      string? workingDirectory = null, Guid? owningRequestId = null) {
    if (parentSessionId == Guid.Empty || parentSessionId == sessionId)
      throw new ArgumentException("Parent session ID must identify a different session.", nameof(parentSessionId));
    if (owningRequestId == Guid.Empty)
      throw new ArgumentException("Owning request ID cannot be empty.", nameof(owningRequestId));
    ArgumentException.ThrowIfNullOrWhiteSpace(owningCallId);
    return Create(sessionId, workingDirectory, SessionKind.Subagent, parentSessionId, owningCallId, owningRequestId, null);
  }

  private SessionRecord Create(Guid sessionId, string? workingDirectory, SessionKind kind, Guid? parentSessionId,
      string? owningCallId, Guid? owningRequestId, IReadOnlyList<Guid>? subagentProviders) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    return database.Write(transaction => database.QuerySingleOrDefault(transaction, """
        INSERT INTO sessions (session_id, history_revision, working_directory, kind, parent_session_id,
            owning_call_id, owning_request_id, subagent_providers_json)
        VALUES ($id, 0, $directory, $kind, $parent, $call, $request, $providers);
        INSERT INTO session_creation (session_id) VALUES ($id);
        SELECT session_id, history_revision, working_directory, kind, parent_session_id, owning_call_id,
            owning_request_id, snoozed_until_ms
        FROM sessions WHERE session_id = $id;
        """, ReadRecord, ("$id", sessionId), ("$directory", workingDirectory), ("$kind", (int)kind),
        ("$parent", parentSessionId), ("$call", owningCallId), ("$request", owningRequestId),
        ("$providers", SerializeProviders(subagentProviders)))!);
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
      """
      SELECT session_id, history_revision, working_directory, kind, parent_session_id, owning_call_id,
          owning_request_id, snoozed_until_ms
      FROM sessions WHERE session_id = $id;
      """, ReadRecord, ("$id", sessionId)));

  internal IReadOnlyList<SnoozedSession> ReadSnoozed(DateTimeOffset now) => database.RunLocked(() =>
      database.Query(null, """
          SELECT s.session_id, s.snoozed_until_ms, t.title
          FROM sessions s LEFT JOIN session_prepass_title t ON t.session_id = s.session_id
          WHERE s.snoozed_until_ms > $now
          ORDER BY s.snoozed_until_ms, s.session_id;
          """, row => new SnoozedSession(row.Guid("session_id"),
              DateTimeOffset.FromUnixTimeMilliseconds(row.Long("snoozed_until_ms")), row.NullableString("title")),
          ("$now", now.ToUnixTimeMilliseconds())));

  internal void SetSnoozedUntil(Guid sessionId, DateTimeOffset? until) => database.Write(transaction => {
    if (database.Execute(transaction, """
        UPDATE sessions SET snoozed_until_ms = $until WHERE session_id = $session AND kind = $kind;
        """, ("$until", until?.ToUnixTimeMilliseconds()), ("$session", sessionId), ("$kind", (int)SessionKind.Primary)) == 0)
      throw new KeyNotFoundException($"Primary session '{sessionId}' does not exist.");
    return true;
  });

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

  internal IReadOnlyList<SubagentDiscovery> ReadSubagentDiscoveries(IReadOnlyList<Guid> childIds) => childIds.Count == 0
      ? [] : database.RunLocked(() => database.Query(null, """
        SELECT s.session_id, s.parent_session_id, t.title
        FROM json_each($ids) ids CROSS JOIN sessions s ON s.session_id = ids.value
        LEFT JOIN session_prepass_title t ON t.session_id = s.session_id
        ORDER BY ids.key;
        """, ReadDiscovery, ("$ids", JsonSerializer.Serialize(childIds))));

  internal SubagentDiscovery? FindSubagentByCall(Guid parentId, Guid requestId, string callId) => database.Read(transaction => {
    var exact = database.QuerySingleOrDefault(transaction, """
        SELECT s.session_id, s.parent_session_id, t.title
        FROM sessions s LEFT JOIN session_prepass_title t ON t.session_id = s.session_id
        WHERE s.parent_session_id = $parent AND s.owning_call_id = $call AND s.owning_request_id = $request;
        """, ReadDiscovery, ("$parent", parentId), ("$call", callId), ("$request", requestId));
    if (exact is not null)
      return exact;

    // A legacy link needs one possible child and one started delegation in unedited history.
    // An edit can erase the competing call while leaving its child record behind.
    var children = database.Query(transaction, """
        SELECT s.session_id, s.parent_session_id, s.owning_request_id, t.title
        FROM sessions p JOIN sessions s ON s.parent_session_id = p.session_id
        LEFT JOIN session_prepass_title t ON t.session_id = s.session_id
        WHERE p.session_id = $parent AND p.history_revision = 0 AND s.owning_call_id = $call
        LIMIT 2;
        """, row => (Child: ReadDiscovery(row), RequestId: row.NullableGuid("owning_request_id")),
        ("$parent", parentId), ("$call", callId));
    if (children.Count != 1 || children[0].RequestId is not null)
      return null;

    List<Guid> requests = database.Query(transaction, """
        SELECT t.request_id FROM execution_tools t JOIN execution_runs r ON r.run_id = t.run_id
        WHERE r.session_id = $parent AND r.history_revision = 0
            AND t.call_id = $call AND t.name = 'subagent' AND t.started = 1
        LIMIT 2;
        """, row => row.Guid("request_id"), ("$parent", parentId), ("$call", callId));
    return requests.Count == 1 && requests[0] == requestId ? children[0].Child : null;
  });

  private static SubagentDiscovery ReadDiscovery(SqliteDataReader row) {
    string? title = row.NullableString("title");
    return new(row.Guid("session_id"), row.Guid("parent_session_id"), title ?? "Subagent", TitlePending: title is null);
  }

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
          (SessionKind)row.Int("kind"), row.NullableGuid("parent_session_id"), row.NullableString("owning_call_id"),
          row.NullableGuid("owning_request_id"), row.NullableLong("snoozed_until_ms") is long until
              ? DateTimeOffset.FromUnixTimeMilliseconds(until) : null);
}
