using System.Text.Json;
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

  // Read attempted human input and answered questions visible at the invocation boundary, including the compacted prefix.
  private List<SubagentContextEntry> ReadUserContext(SqliteTransaction transaction, ExecutionIdentity parent,
      Invocation invocation) => database.Query(transaction, """
        WITH requests AS (
          SELECT r.ordinal AS run_ordinal, q.request_index, q.request_id
          FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
          WHERE r.session_id = $session AND r.history_revision = $revision AND q.attempt = 2
              AND (r.ordinal, q.request_index) <= ($ordinal, $index)
        )
        SELECT q.run_ordinal, q.request_index, 1 AS kind, i.ordinal AS item_order, q.request_id,
            d.original_text_utf8, d.image_ids_json IS NOT NULL AS has_images, i.provenance,
            NULL AS call_id, NULL AS arguments_json, NULL AS result_json
        FROM requests q JOIN execution_inputs i ON i.request_id = q.request_id AND i.session_id = $session
        JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
        WHERE i.provenance IN (0, 2) AND i.not_sent = 0 AND d.superseded = 0
        UNION ALL
        SELECT q.run_ordinal, q.request_index, 0, c.ordinal, t.request_id,
            NULL, 0, NULL, t.call_id, t.arguments_json, t.result_json
        FROM requests q JOIN execution_tool_completions c ON c.claimed_request_id = q.request_id
        JOIN execution_tools t ON t.request_id = c.request_id AND t.call_id = c.call_id
        WHERE c.session_id = $session AND c.history_revision = $revision AND t.background = 1
            AND t.name = 'ask_user' AND t.started = 1 AND t.outcome = $completed AND t.result_json IS NOT NULL
        UNION ALL
        SELECT q.run_ordinal, q.request_index, 2, t.call_index, t.request_id,
            NULL, 0, NULL, t.call_id, t.arguments_json, t.result_json
        FROM requests q JOIN execution_tools t ON t.request_id = q.request_id
        WHERE (q.run_ordinal, q.request_index) < ($ordinal, $index) AND t.background = 0
            AND t.name = 'ask_user' AND t.started = 1 AND t.outcome = $completed AND t.result_json IS NOT NULL
        ORDER BY run_ordinal, request_index, kind, item_order;
        """, ReadUserEntry, ("$session", parent.SessionId), ("$revision", parent.HistoryRevision),
      ("$completed", (int)ExecutionOutcome.Completed),
      ("$ordinal", invocation.RunOrdinal), ("$index", invocation.RequestIndex)).SelectMany(entries => entries).ToList();

  private static IEnumerable<SubagentContextEntry> ReadUserEntry(SqliteDataReader row) {
    if (row.Int("kind") != 1)
      return ReadAnsweredQuestion(row);
    string text = StorageText.StrictUtf8.GetString((byte[])row["original_text_utf8"]);
    if ((InputProvenance)row.Int("provenance") == InputProvenance.InheritedContext)
      return SubagentContext.ReadInherited(text).Where(entry => entry.Source is "human input" or "answered user question");
    Guid requestId = row.Guid("request_id");
    if (!row.Bool("has_images"))
      return [new("human input", text, requestId)];
    return string.IsNullOrWhiteSpace(text) ? [new("human input", SubagentContext.UserImageOmitted, requestId)]
        : [new("human input", text, requestId), new("human input", SubagentContext.UserImageOmitted, requestId)];
  }

  private static IEnumerable<SubagentContextEntry> ReadAnsweredQuestion(SqliteDataReader row) {
    using JsonDocument arguments = JsonDocument.Parse(row.String("arguments_json"));
    using JsonDocument result = JsonDocument.Parse(row.String("result_json"));
    JsonElement output = result.RootElement;
    if ((output.TryGetProperty("isError", out JsonElement error) && error.GetBoolean())
        || !output.TryGetProperty("text", out JsonElement answer) || answer.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(answer.GetString()))
      return [];
    JsonElement input = arguments.RootElement;
    string exchange = JsonSerializer.Serialize(new {
      agent = new {
        question = input.GetProperty("question"),
        context = input.TryGetProperty("context", out JsonElement context) ? context : (JsonElement?)null,
        choices = input.GetProperty("choices")
      },
      human = new { answer }
    });
    return [new("answered user question", exchange, row.Guid("request_id"), row.String("call_id"), "ask_user")];
  }

  private sealed record Invocation(long RunOrdinal, int RequestIndex);
}
