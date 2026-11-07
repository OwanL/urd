using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Urd.Core.Storage;

// Admission order is readable history; only claimed inputs belong in model context.
internal sealed class RequestContextReader(Database database) {
  internal ReadOnlyCollection<ChatMessage> Read(SqliteTransaction transaction, Guid sessionId,
      Guid runId, long historyRevision, Guid requestId) {
    using SqliteCommand read = database.Command(transaction, """
      WITH requests AS (
          SELECT r.ordinal AS run_ordinal, q.request_index, q.request_id FROM execution_runs r
          JOIN execution_requests q ON q.run_id = r.run_id
          WHERE r.session_id = $session AND r.history_revision = $revision
              AND r.ordinal <= (SELECT ordinal FROM execution_runs WHERE run_id = $run)
              AND (r.run_id <> $run OR q.request_index <=
                  (SELECT request_index FROM execution_requests WHERE request_id = $request))
      ), contents AS (
          SELECT q.run_ordinal, q.request_index, q.request_id, 0 AS kind, i.ordinal AS content_index,
              d.original_text_utf8 AS text_utf8, NULL AS call_id, NULL AS name, NULL AS json
          FROM requests q JOIN execution_inputs i ON i.request_id = q.request_id
          JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, 1, o.batch_index, o.text_utf8, NULL, NULL, NULL
          FROM requests q JOIN execution_output o ON o.request_id = q.request_id
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, 2, t.call_index, NULL,
              t.call_id, t.name, t.arguments_json
          FROM requests q JOIN execution_tools t ON t.request_id = q.request_id
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, 3, t.call_index, NULL,
              t.call_id, t.name, t.result_json
          FROM requests q JOIN execution_tools t ON t.request_id = q.request_id
          WHERE t.result_json IS NOT NULL
      ) SELECT request_id, kind, text_utf8, call_id, name, json FROM contents
      ORDER BY run_ordinal, request_index, kind, content_index;
      """,
        ("$session", sessionId), ("$run", runId), ("$revision", historyRevision), ("$request", requestId));
    using SqliteDataReader reader = read.ExecuteReader();
    var messages = new List<ChatMessage>();
    string? previousRequest = null;
    StringBuilder? text = null;
    var calls = new List<AIContent>();
    var results = new List<AIContent>();
    while (reader.Read()) {
      string currentRequest = reader.GetString(0);
      if (previousRequest != currentRequest) {
        AddResponse(messages, text, calls, results);
        text = null;
        calls.Clear();
        results.Clear();
        previousRequest = currentRequest;
      }

      switch (reader.GetInt32(1)) {
        case 0:
          messages.Add(new(ChatRole.User, StorageText.StrictUtf8.GetString((byte[])reader.GetValue(2))));
          break;
        case 1:
          (text ??= new()).Append(StorageText.StrictUtf8.GetString((byte[])reader.GetValue(2)));
          break;
        case 2:
          var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(5))!;
          calls.Add(new FunctionCallContent(reader.GetString(3), reader.GetString(4), arguments));
          break;
        case 3:
          results.Add(new FunctionResultContent(reader.GetString(3),
              JsonSerializer.Deserialize<JsonElement>(reader.GetString(5))));
          break;
      }
    }

    AddResponse(messages, text, calls, results);
    return messages.AsReadOnly();
  }

  private static void AddResponse(List<ChatMessage> messages, StringBuilder? text,
      List<AIContent> calls, List<AIContent> results) {
    if (text is not null || calls.Count > 0) {
      var contents = new List<AIContent>();
      if (text is not null)
        contents.Add(new TextContent(text.ToString()));
      contents.AddRange(calls);
      messages.Add(new(ChatRole.Assistant, contents));
    }

    if (results.Count > 0)
      messages.Add(new(ChatRole.Tool, results.ToList()));
  }
}
