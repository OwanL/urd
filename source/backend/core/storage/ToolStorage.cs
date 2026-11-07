using System.Text.Json;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;

namespace Urd.Core.Storage;

// Claims authorized calls before invocation and retains actual results without replay.
internal sealed class ToolStorage(Database database) {
  internal bool TryStart(Guid sessionId, Guid runId, long revision, Guid requestId, string callId) {
    return database.Write(transaction => {
      using SqliteCommand start = database.Command(transaction, """
        UPDATE execution_tools SET started = 1
        WHERE request_id = $request AND run_id = $run AND call_id = $call
            AND started = 0 AND outcome IS NULL AND result_json IS NULL
            AND NOT EXISTS (SELECT 1 FROM execution_tools earlier
                WHERE earlier.request_id = $request AND earlier.call_index < execution_tools.call_index
                    AND (earlier.outcome IS NULL OR earlier.result_json IS NULL))
            AND EXISTS (SELECT 1 FROM execution_requests q
                JOIN execution_runs r ON r.run_id = q.run_id
                JOIN sessions s ON s.session_id = r.session_id
                WHERE q.request_id = $request AND q.run_id = $run AND q.outcome = $completed
                    AND r.session_id = $session AND r.history_revision = $revision
                    AND s.history_revision = $revision AND r.stopping = 0 AND r.outcome IS NULL
                    AND NOT EXISTS (SELECT 1 FROM execution_requests later
                        WHERE later.run_id = $run AND later.request_index > q.request_index));
        """,
          ("$session", sessionId), ("$run", runId), ("$revision", revision),
          ("$request", requestId), ("$call", callId), ("$completed", (int)ExecutionOutcome.Completed));
      return start.ExecuteNonQuery() == 1;
    });
  }

  internal bool Complete(Guid sessionId, Guid runId, long revision, Guid requestId, LlmToolResult result) {
    if (!Enum.IsDefined(result.Outcome))
      throw new ArgumentOutOfRangeException(nameof(result));
    string json = JsonText(result.Result);

    return database.Write(transaction => {
      using (SqliteCommand read = database.Command(transaction, """
        SELECT t.started, t.outcome, t.result_json, t.error, r.outcome, q.outcome,
            EXISTS (SELECT 1 FROM execution_requests later
                WHERE later.run_id = $run AND later.request_index > q.request_index)
        FROM execution_tools t JOIN execution_requests q ON q.request_id = t.request_id
        JOIN execution_runs r ON r.run_id = q.run_id JOIN sessions s ON s.session_id = r.session_id
        WHERE t.run_id = $run AND t.request_id = $request AND t.call_id = $call
            AND r.session_id = $session AND r.history_revision = $revision AND s.history_revision = $revision;
        """,
          ("$session", sessionId), ("$run", runId), ("$revision", revision),
          ("$request", requestId), ("$call", result.CallId)))
      using (SqliteDataReader reader = read.ExecuteReader()) {
        if (!reader.Read() || reader.GetInt32(0) == 0)
          return false;

        if (!reader.IsDBNull(1))
          return reader.GetInt32(1) == (int)result.Outcome && !reader.IsDBNull(2)
              && reader.GetString(2) == json && (reader.IsDBNull(3) ? null : reader.GetString(3)) == result.Error;

        // Stop does not discard a real result, but settlement or a newer request fences new writes.
        if (!reader.IsDBNull(4) || reader.IsDBNull(5)
            || reader.GetInt32(5) != (int)ExecutionOutcome.Completed || reader.GetInt32(6) != 0)
          return false;
      }

      using SqliteCommand save = database.Command(transaction, """
        UPDATE execution_tools SET outcome = $outcome, result_json = $result, error = $error
        WHERE request_id = $request AND call_id = $call;
        """,
          ("$request", requestId), ("$call", result.CallId), ("$outcome", (int)result.Outcome),
          ("$result", json), ("$error", result.Error));
      return save.ExecuteNonQuery() == 1;
    });
  }

  // Capture valid JSON text now rather than retaining a borrowed JsonDocument or serializing exceptions.
  internal static string JsonText(JsonElement value, bool requireObject = false) {
    if (value.ValueKind == JsonValueKind.Undefined || (requireObject && value.ValueKind != JsonValueKind.Object))
      throw new ArgumentException("Tool payloads require valid JSON; arguments must be an object.", nameof(value));

    string json = value.GetRawText();
    if (StorageText.StrictUtf8.GetByteCount(json) > StorageText.MaximumBytes)
      throw new ArgumentException("Tool JSON must not exceed 16 KiB of UTF-8.", nameof(value));

    return json;
  }
}
