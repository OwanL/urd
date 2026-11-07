using Microsoft.Data.Sqlite;
using Urd.Core.Execution;

namespace Urd.Core.Storage;

// Appends ordered output segments and records explicit request completion.
internal sealed class LlmResponseStorage(Database database) {
  internal bool Append(Guid sessionId, Guid executionId, long historyRevision, LlmResponseChunk chunk) {
    ArgumentOutOfRangeException.ThrowIfNegative(chunk.ChunkIndex);
    if (chunk.RequestId == Guid.Empty || chunk.ResponseId == Guid.Empty
        || StorageText.StrictUtf8.GetByteCount(chunk.Text) > StorageText.MaximumBytes) {
      throw new ArgumentException("Output requires identities and at most 16 KiB of valid UTF-8 text.", nameof(chunk));
    }

    return database.Write(transaction => {
      using SqliteCommand advance = database.Command(transaction, """
        UPDATE execution_requests SET next_batch_index = next_batch_index + 1,
            output_id = $output, output_final = $final
        WHERE request_id = $request AND run_id = $run AND outcome IS NULL
            AND next_batch_index = $index AND output_final = 0
            AND (output_id IS NULL OR output_id = $output)
            AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
                WHERE r.run_id = $run AND r.session_id = $session AND r.history_revision = $revision
                    AND s.history_revision = $revision AND r.stopping = 0 AND r.outcome IS NULL);
        """,
          ("$session", sessionId),
          ("$run", executionId),
          ("$revision", historyRevision),
          ("$request", chunk.RequestId),
          ("$output", chunk.ResponseId),
          ("$index", chunk.ChunkIndex),
          ("$final", chunk.IsFinal ? 1 : 0));
      if (advance.ExecuteNonQuery() == 0)
        return false;

      using SqliteCommand append = database.Command(transaction, """
        INSERT INTO execution_output (request_id, batch_index, text_utf8, is_final)
        VALUES ($request, $index, $text, $final);
        """,
          ("$request", chunk.RequestId),
          ("$index", chunk.ChunkIndex),
          ("$text", StorageText.StrictUtf8.GetBytes(chunk.Text)),
          ("$final", chunk.IsFinal ? 1 : 0));
      append.ExecuteNonQuery();
      if (chunk.ChunkIndex == 0) {
        using SqliteCommand index = database.Command(transaction, """
          INSERT INTO execution_messages
              (session_id, history_revision, run_id, role, message_id, request_id)
          VALUES ($session, $revision, $run, 1, $output, $request);
          """,
            ("$session", sessionId), ("$revision", historyRevision), ("$run", executionId),
            ("$output", chunk.ResponseId), ("$request", chunk.RequestId));
        index.ExecuteNonQuery();
      }
      return true;
    });
  }

  internal bool CompleteRequest(Guid sessionId, Guid executionId, long historyRevision, LlmRequestResult completion) {
    if (!Enum.IsDefined(completion.Outcome))
      throw new ArgumentOutOfRangeException(nameof(completion));

    if (completion.ToolCalls is { Count: > 16 })
      throw new ArgumentException("A response may authorize at most 16 tool calls.", nameof(completion));

    var calls = (completion.ToolCalls ?? []).Select(call =>
        (call.CallId, call.Name, Arguments: ToolStorage.JsonText(call.Arguments, requireObject: true))).ToArray();
    if (calls.Any(call => string.IsNullOrWhiteSpace(call.CallId) || string.IsNullOrWhiteSpace(call.Name))
        || calls.Select(call => call.CallId).Distinct(StringComparer.Ordinal).Count() != calls.Length
        || (calls.Length != 0 && completion.Outcome != ExecutionOutcome.Completed))
      throw new ArgumentException("Only a completed response may authorize up to 16 distinct, named tool calls.", nameof(completion));

    return database.Write(transaction => CompleteRequest(transaction, sessionId, executionId, historyRevision, completion, calls));
  }

  private bool CompleteRequest(SqliteTransaction transaction, Guid sessionId, Guid executionId,
      long historyRevision, LlmRequestResult completion, (string CallId, string Name, string Arguments)[] calls) {
    bool alreadyCompleted;
    using (SqliteCommand read = database.Command(transaction, """
      SELECT q.outcome, q.error, q.output_final, r.stopping, r.outcome,
          EXISTS (SELECT 1 FROM execution_requests later
              WHERE later.run_id = $run AND later.request_index > q.request_index)
      FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
      JOIN sessions s ON s.session_id = r.session_id
      WHERE r.session_id = $session AND r.run_id = $run AND q.request_id = $request
          AND r.history_revision = $revision AND s.history_revision = $revision;
      """,
        ("$session", sessionId),
        ("$run", executionId),
        ("$revision", historyRevision),
        ("$request", completion.RequestId)))
    using (SqliteDataReader reader = read.ExecuteReader()) {
      if (!reader.Read())
        return false;

      // Locals name the SELECT columns in order.
      ExecutionOutcome? savedOutcome = reader.IsDBNull(0) ? null : (ExecutionOutcome)reader.GetInt32(0);
      string? savedError = reader.IsDBNull(1) ? null : reader.GetString(1);
      bool hasFinalOutput = reader.GetInt32(2) != 0;
      bool stopRequested = reader.GetInt32(3) != 0;
      bool executionSettled = !reader.IsDBNull(4);

      // An exact retry succeeds without rewriting the original evidence, even after settlement.
      alreadyCompleted = savedOutcome is not null;
      if (alreadyCompleted) {
        if (savedOutcome != completion.Outcome || savedError != completion.Error)
          return false;
      } else {
        if (executionSettled || reader.GetInt32(5) != 0)
          return false;
        if (completion.Outcome == ExecutionOutcome.Completed && (stopRequested || !hasFinalOutput))
          return false;
      }
    }

    if (alreadyCompleted)
      return MatchesCalls(transaction, completion.RequestId, calls);

    // A call identity cannot be reused by a later response in the same execution.
    foreach (var call in calls) {
      using SqliteCommand exists = database.Command(transaction,
          "SELECT EXISTS(SELECT 1 FROM execution_tools WHERE run_id = $run AND call_id = $call);",
          ("$run", executionId), ("$call", call.CallId));
      if ((long)exists.ExecuteScalar()! != 0)
        return false;
    }

    using SqliteCommand update = database.Command(transaction,
        "UPDATE execution_requests SET outcome = $outcome, error = $error WHERE request_id = $request;",
        ("$outcome", (int)completion.Outcome),
        ("$error", completion.Error),
        ("$request", completion.RequestId));
    update.ExecuteNonQuery();
    using SqliteCommand delivered = database.Command(transaction,
        "UPDATE execution_inputs SET outcome = $outcome WHERE request_id = $request;",
        ("$outcome", (int)completion.Outcome), ("$request", completion.RequestId));
    delivered.ExecuteNonQuery();
    for (int index = 0; index < calls.Length; index++) {
      var call = calls[index];
      using SqliteCommand authorize = database.Command(transaction, """
        INSERT INTO execution_tools (run_id, request_id, call_index, call_id, name, arguments_json)
        VALUES ($run, $request, $index, $call, $name, $arguments);
        """,
          ("$run", executionId), ("$request", completion.RequestId), ("$index", index),
          ("$call", call.CallId), ("$name", call.Name), ("$arguments", call.Arguments));
      authorize.ExecuteNonQuery();
    }
    return true;
  }

  private bool MatchesCalls(SqliteTransaction transaction, Guid requestId,
      (string CallId, string Name, string Arguments)[] calls) {
    using SqliteCommand read = database.Command(transaction, """
      SELECT call_id, name, arguments_json FROM execution_tools WHERE request_id = $request ORDER BY call_index;
      """, ("$request", requestId));
    using SqliteDataReader reader = read.ExecuteReader();
    int index = 0;
    while (reader.Read()) {
      if (index == calls.Length || reader.GetString(0) != calls[index].CallId
          || reader.GetString(1) != calls[index].Name || reader.GetString(2) != calls[index].Arguments)
        return false;
      index++;
    }
    return index == calls.Length;
  }
}
