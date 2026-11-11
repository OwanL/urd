using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Tools;

namespace Urd.Core.Storage;

// Claims authorized calls before invocation and retains actual results without replay.
internal sealed class ToolStorage(Database database, ToolResultCodec results) {
  // Original output is available for inspection regardless of the next model's capabilities.
  internal ToolOutput? ReadOutput(Guid sessionId, Guid requestId, string callId) => database.Guarded(() => {
    using SqliteCommand read = database.Command(null, """
      SELECT t.result_json FROM execution_tools t JOIN execution_runs r ON r.run_id = t.run_id
      WHERE r.session_id = $session AND t.request_id = $request AND t.call_id = $call;
      """, ("$session", sessionId), ("$request", requestId), ("$call", callId));
    return read.ExecuteScalar() is string value ? results.Read(value) : null;
  });

  internal ToolCallTranscript? ReadTranscript(Guid sessionId, long expectedHistoryRevision,
      Guid requestId, string callId, bool returned) => database.Read(transaction => {
    using (SqliteCommand check = database.Command(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId))) {
      if (check.ExecuteScalar() is not long revision)
        throw new KeyNotFoundException("The session does not exist.");
      if (revision != expectedHistoryRevision)
        throw new HistoryRevisionConflictException(expectedHistoryRevision, revision);
    }

    using SqliteCommand read = database.Command(transaction, """
      SELECT t.arguments_json, t.background, t.started, t.result_json, t.outcome, t.name,
          EXISTS (SELECT 1 FROM execution_tool_completions c
              WHERE c.session_id = r.session_id AND c.history_revision = r.history_revision
                  AND c.request_id = t.request_id AND c.call_id = t.call_id AND c.published = 1)
      FROM execution_tools t JOIN execution_runs r ON r.run_id = t.run_id
      JOIN execution_requests q ON q.request_id = t.request_id AND q.run_id = t.run_id
      WHERE r.session_id = $session AND r.history_revision = $revision
          AND t.request_id = $request AND t.call_id = $call;
      """, ("$session", sessionId), ("$revision", expectedHistoryRevision),
        ("$request", requestId), ("$call", callId));
    using SqliteDataReader reader = read.ExecuteReader();
    if (!reader.Read())
      return null;

    string input = reader.GetString(0);
    bool background = reader.GetInt32(1) != 0;
    bool started = reader.GetInt32(2) != 0;
    string? resultJson = reader.IsDBNull(3) ? null : reader.GetString(3);
    ExecutionOutcome? outcome = reader.IsDBNull(4) ? null : (ExecutionOutcome)reader.GetInt32(4);
    string name = reader.GetString(5);
    bool returnedPublished = reader.GetBoolean(6);
    string? output = null;
    if (returned && background && returnedPublished && resultJson is not null && outcome is not null)
      output = results.ReadModelCompletionText(requestId, callId, name, outcome.Value, resultJson);
    else if (!returned && background && started)
      output = ToolResultCodec.BackgroundStarted(requestId, callId);
    else if (!returned && resultJson is not null)
      output = results.ReadModelResultText(resultJson);

    return new ToolCallTranscript(input, output, Truncated: false);
  });

  internal bool TryStart(Guid sessionId, Guid executionId, long revision, Guid requestId, string callId,
      bool background = false) {
    return database.Write(transaction => {
      using SqliteCommand start = database.Command(transaction, """
        UPDATE execution_tools SET started = 1, started_at = $started, background = $background
        WHERE request_id = $request AND run_id = $run AND call_id = $call
            AND started = 0 AND outcome IS NULL AND result_json IS NULL
            AND EXISTS (SELECT 1 FROM execution_requests q
                JOIN execution_runs r ON r.run_id = q.run_id
                JOIN sessions s ON s.session_id = r.session_id
                WHERE q.request_id = $request AND q.run_id = $run AND q.outcome = $completed
                    AND r.session_id = $session AND r.history_revision = $revision
                    AND s.history_revision = $revision AND r.stopping = 0 AND r.outcome IS NULL
                    AND NOT EXISTS (SELECT 1 FROM execution_requests later
                        WHERE later.run_id = $run AND later.request_index > q.request_index));
        """,
          ("$session", sessionId), ("$run", executionId), ("$revision", revision),
          ("$request", requestId), ("$call", callId), ("$started", StorageText.UtcTimestamp()),
          ("$background", background ? 1 : 0),
          ("$completed", (int)ExecutionOutcome.Completed));
      return start.ExecuteNonQuery() == 1;
    });
  }

  internal bool Complete(Guid sessionId, Guid executionId, long revision, Guid requestId, LlmToolResult result) {
    if (!Enum.IsDefined(result.Outcome))
      throw new ArgumentOutOfRangeException(nameof(result));
    // Publish immutable image bytes before committing their references. Failed or uncertain
    // commits retain those assets; deleting them could break another call's saved result.
    string json = results.Write(result.Output);
    string? error = result.Output.IsError ? result.Output.Text[..Math.Min(result.Output.Text.Length, 1024)] : null;

    return database.Write(transaction => {
      bool background;
      using (SqliteCommand read = database.Command(transaction, """
        SELECT t.started, t.outcome, t.result_json, t.error, r.outcome, q.outcome,
            EXISTS (SELECT 1 FROM execution_requests later
                WHERE later.run_id = $run AND later.request_index > q.request_index), t.background
        FROM execution_tools t JOIN execution_requests q ON q.request_id = t.request_id
        JOIN execution_runs r ON r.run_id = q.run_id JOIN sessions s ON s.session_id = r.session_id
        WHERE t.run_id = $run AND t.request_id = $request AND t.call_id = $call
            AND r.session_id = $session AND r.history_revision = $revision AND s.history_revision = $revision;
        """,
          ("$session", sessionId), ("$run", executionId), ("$revision", revision),
          ("$request", requestId), ("$call", result.CallId)))
      using (SqliteDataReader reader = read.ExecuteReader()) {
        if (!reader.Read() || reader.GetInt32(0) == 0)
          return false;

        if (!reader.IsDBNull(1))
          return reader.GetInt32(1) == (int)result.Outcome && !reader.IsDBNull(2)
              && reader.GetString(2) == json && (reader.IsDBNull(3) ? null : reader.GetString(3)) == error;

        background = reader.GetInt32(7) != 0;
        // Stop permits result saves until settlement. Only background calls may outlive a request.
        if (!reader.IsDBNull(4) || reader.IsDBNull(5)
            || reader.GetInt32(5) != (int)ExecutionOutcome.Completed || (!background && reader.GetInt32(6) != 0))
          return false;
      }

      string ended = StorageText.UtcTimestamp();
      using SqliteCommand save = database.Command(transaction, """
        UPDATE execution_tools SET outcome = $outcome, result_json = $result, error = $error, ended_at = $ended
        WHERE request_id = $request AND call_id = $call;
        """,
          ("$request", requestId), ("$call", result.CallId), ("$outcome", (int)result.Outcome),
          ("$result", json), ("$error", error), ("$ended", ended));
      if (save.ExecuteNonQuery() != 1)
        return false;

      if (background) {
        using SqliteCommand completion = database.Command(transaction, """
          INSERT INTO execution_tool_completions (
              completion_id, session_id, history_revision, request_id, call_id, created_at)
          VALUES ($completion, $session, $revision, $request, $call, $created);
          """,
            ("$completion", Guid.NewGuid()), ("$session", sessionId), ("$revision", revision),
            ("$request", requestId), ("$call", result.CallId), ("$created", ended));
        completion.ExecuteNonQuery();
      }
      return true;
    });
  }
}
