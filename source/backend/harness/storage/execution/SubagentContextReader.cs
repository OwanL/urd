using Microsoft.Data.Sqlite;
using Urd.Core.Context;
using Urd.Core.Execution;

namespace Urd.Core.Storage;

// Captures parent context at the invoking request's input boundary, not the live transcript tail.
internal sealed class SubagentContextReader(Database database) {
  internal string? Read(ExecutionIdentity parent, Guid requestId, string callId) {
    var user = database.Read(transaction => {
      Invocation invocation = ReadInvocation(transaction, parent, requestId, callId);
      return ReadUserContext(transaction, parent, invocation);
    });
    return SubagentContext.Build(user);
  }

  private Invocation ReadInvocation(SqliteTransaction transaction, ExecutionIdentity parent, Guid requestId, string callId) =>
      database.QuerySingleOrDefault(transaction, """
        SELECT r.ordinal, q.request_index
        FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
        JOIN sessions s ON s.session_id = r.session_id AND s.history_revision = r.history_revision
        JOIN execution_tools t ON t.request_id = q.request_id AND t.run_id = r.run_id
        WHERE r.session_id = $session AND r.run_id = $run AND r.history_revision = $revision
            AND q.request_id = $request AND q.attempt = 2 AND t.call_id = $call
            AND t.name = 'subagent' AND t.started = 1;
        """, row => new Invocation(row.Long("ordinal"), row.Int("request_index")),
        ExecutionSql.Parameters(parent, ("$request", requestId), ("$call", callId)))
      ?? throw new InvalidOperationException("The invoking subagent call is not in the parent history revision.");

  // User mode reads attempted human input, including humans in inherited context and the compacted prefix.
  private List<SubagentContextEntry> ReadUserContext(SqliteTransaction transaction, ExecutionIdentity parent,
      Invocation invocation) => database.Query(transaction, """
        WITH requests AS (
          SELECT r.ordinal AS run_ordinal, q.request_index, q.request_id
          FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
          WHERE r.session_id = $session AND r.history_revision = $revision AND q.attempt = 2
              AND (r.ordinal, q.request_index) <= ($ordinal, $index)
        )
        SELECT q.request_id, d.original_text_utf8, d.image_ids_json IS NOT NULL AS has_images, i.provenance
        FROM requests q JOIN execution_inputs i ON i.request_id = q.request_id AND i.session_id = $session
        JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
        WHERE i.provenance IN (0, 2) AND i.not_sent = 0 AND d.superseded = 0
        ORDER BY q.run_ordinal, q.request_index, i.ordinal;
        """, ReadUserEntry, ("$session", parent.SessionId), ("$revision", parent.HistoryRevision),
      ("$ordinal", invocation.RunOrdinal), ("$index", invocation.RequestIndex)).SelectMany(entries => entries).ToList();

  private static IEnumerable<SubagentContextEntry> ReadUserEntry(SqliteDataReader row) {
    string text = StorageText.StrictUtf8.GetString((byte[])row["original_text_utf8"]);
    if ((InputProvenance)row.Int("provenance") == InputProvenance.InheritedContext)
      return SubagentContext.ReadInherited(text).Where(entry => entry.Source == "human input");
    Guid requestId = row.Guid("request_id");
    if (!row.Bool("has_images"))
      return [new("human input", text, requestId)];
    return string.IsNullOrWhiteSpace(text) ? [new("human input", SubagentContext.UserImageOmitted, requestId)]
        : [new("human input", text, requestId), new("human input", SubagentContext.UserImageOmitted, requestId)];
  }

  private sealed record Invocation(long RunOrdinal, int RequestIndex);
}
