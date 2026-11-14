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
      Guid requestId, string callId, bool returned, int? characterLimit = null) => database.Read(transaction => {
    using (SqliteCommand check = database.Command(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId))) {
      if (check.ExecuteScalar() is not long revision)
        throw new KeyNotFoundException("The session does not exist.");
      if (revision != expectedHistoryRevision)
        throw new HistoryRevisionConflictException(expectedHistoryRevision, revision);
    }

    using SqliteCommand read = database.Command(transaction, """
      SELECT t.rowid, t.background, t.started, t.result_json IS NOT NULL, t.outcome, t.name,
          EXISTS (SELECT 1 FROM execution_tool_completions c
              WHERE c.session_id = r.session_id AND c.history_revision = r.history_revision
                  AND c.request_id = t.request_id AND c.call_id = t.call_id AND c.published = 1),
          (SELECT COALESCE(receiving_run.llm_selection_json, d.llm_selection_json)
              FROM execution_runs receiving_run
              JOIN execution_requests receiving ON receiving.run_id = receiving_run.run_id
              LEFT JOIN session_compactions receiving_compaction
                  ON receiving_compaction.compaction_id = receiving.compaction_id
              LEFT JOIN input_decisions d ON d.session_id = receiving_run.session_id AND d.input_id = receiving_run.input_id
              WHERE receiving_run.session_id = r.session_id AND receiving_run.history_revision = r.history_revision
                  AND receiving.attempt = 2
                  AND (receiving_run.ordinal > r.ordinal OR
                      (receiving_run.ordinal = r.ordinal AND receiving.request_index > q.request_index))
                  AND (receiving_compaction.compaction_id IS NULL
                      OR r.ordinal > receiving_compaction.first_kept_run_ordinal
                      OR (r.ordinal = receiving_compaction.first_kept_run_ordinal
                          AND q.request_index >= receiving_compaction.first_kept_request_index))
              ORDER BY receiving_run.ordinal, receiving.request_index LIMIT 1),
          (SELECT COALESCE(receiving_run.llm_selection_json, d.llm_selection_json)
              FROM execution_tool_completions c
              JOIN execution_requests receiving ON receiving.request_id = c.claimed_request_id
              JOIN execution_runs receiving_run ON receiving_run.run_id = receiving.run_id
              LEFT JOIN session_compactions receiving_compaction
                  ON receiving_compaction.compaction_id = receiving.compaction_id
              LEFT JOIN input_decisions d ON d.session_id = receiving_run.session_id AND d.input_id = receiving_run.input_id
              WHERE c.session_id = r.session_id AND c.history_revision = r.history_revision
                  AND c.request_id = t.request_id AND c.call_id = t.call_id AND c.published = 1
                  AND receiving.attempt = 2
                  AND (receiving_compaction.compaction_id IS NULL
                      OR receiving_run.ordinal > receiving_compaction.first_kept_run_ordinal
                      OR (receiving_run.ordinal = receiving_compaction.first_kept_run_ordinal
                          AND (receiving.request_index > receiving_compaction.first_kept_request_index
                              OR (receiving.request_index = receiving_compaction.first_kept_request_index
                                  AND receiving_compaction.first_kept_message_index = 0)))))
      FROM execution_tools t JOIN execution_runs r ON r.run_id = t.run_id
      JOIN execution_requests q ON q.request_id = t.request_id AND q.run_id = t.run_id
      WHERE r.session_id = $session AND r.history_revision = $revision
          AND t.request_id = $request AND t.call_id = $call;
      """, ("$session", sessionId), ("$revision", expectedHistoryRevision),
        ("$request", requestId), ("$call", callId));
    using SqliteDataReader reader = read.ExecuteReader();
    if (!reader.Read())
      return null;

    long rowId = reader.GetInt64(0);
    // Do not SELECT the payloads: even a bounded managed read would materialize SQLite's full TEXT values.
    using var inputBlob = new SqliteBlob(transaction.Connection!, "execution_tools", "arguments_json", rowId, readOnly: true);
    using var inputReader = new StreamReader(inputBlob);
    string input = ToolTranscriptPreview.ReadText(inputReader, characterLimit, out bool inputTruncated);
    bool background = reader.GetInt32(1) != 0;
    bool started = reader.GetInt32(2) != 0;
    bool hasResult = reader.GetBoolean(3);
    ExecutionOutcome? outcome = reader.IsDBNull(4) ? null : (ExecutionOutcome)reader.GetInt32(4);
    string name = reader.GetString(5);
    bool returnedPublished = reader.GetBoolean(6);
    string? output = null;
    bool outputTruncated = false;
    bool projectionFinal = false;
    if (!returned && background && started) {
      projectionFinal = true;
      using var startedReader = new StringReader(ToolResultCodec.BackgroundStarted(requestId, callId));
      output = ToolTranscriptPreview.ReadText(startedReader, characterLimit, out outputTruncated);
    } else if (hasResult && (!returned || (background && returnedPublished && outcome is not null))) {
      using var resultBlob = new SqliteBlob(transaction.Connection!, "execution_tools", "result_json", rowId, readOnly: true);
      using var resultReader = new StreamReader(resultBlob);
      // Until a receiver is saved, image placeholders can still become the model's omission notice.
      projectionFinal = !reader.IsDBNull(returned ? 8 : 7);
      output = returned
          ? results.ReadModelCompletionText(requestId, callId, name, outcome!.Value, resultReader, characterLimit,
              ReadReceivingModelSupportsImages(reader, 8), out outputTruncated)
          : results.ReadModelResultText(resultReader, characterLimit,
              ReadReceivingModelSupportsImages(reader, 7), out outputTruncated);
    }

    return new ToolCallTranscript(input, output, inputTruncated || outputTruncated, projectionFinal);
  });

  private static bool ReadReceivingModelSupportsImages(SqliteDataReader reader, int selectionColumn) {
    // If no sent receiving request exists yet, preserve placeholders rather than infer future model support.
    string? selectionJson = reader.IsDBNull(selectionColumn) ? null : reader.GetString(selectionColumn);
    return LlmSelectionJson.Read(selectionJson)?.SupportsImages ?? true;
  }

  internal bool TryStart(ExecutionIdentity execution, Guid requestId, string callId, bool background = false) {
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
          ("$session", execution.SessionId), ("$run", execution.ExecutionId),
          ("$revision", execution.HistoryRevision), ("$request", requestId),
          ("$call", callId), ("$started", StorageText.UtcTimestamp()),
          ("$background", background ? 1 : 0),
          ("$completed", (int)ExecutionOutcome.Completed));
      return start.ExecuteNonQuery() == 1;
    });
  }

  internal bool Complete(ExecutionIdentity execution, Guid requestId, LlmToolResult result) {
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
          ("$session", execution.SessionId), ("$run", execution.ExecutionId),
          ("$revision", execution.HistoryRevision), ("$request", requestId), ("$call", result.CallId)))
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
            ("$completion", Guid.NewGuid()), ("$session", execution.SessionId),
            ("$revision", execution.HistoryRevision),
            ("$request", requestId), ("$call", result.CallId), ("$created", ended));
        completion.ExecuteNonQuery();
      }
      return true;
    });
  }
}
