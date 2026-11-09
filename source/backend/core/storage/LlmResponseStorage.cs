using System.Text.Json;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Providers;

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
            output_id = CASE WHEN output_id IS NULL AND $hasText = 1 THEN $output ELSE output_id END,
            output_final = $final
        WHERE request_id = $request AND run_id = $run AND outcome IS NULL
            AND next_batch_index = $index AND output_final = 0
            AND NOT EXISTS (SELECT 1 FROM execution_messages m
                WHERE m.request_id = $request AND m.role = 1 AND m.end_batch_index IS NULL
                    AND m.message_id <> $output)
            AND NOT EXISTS (SELECT 1 FROM execution_messages m
                WHERE m.request_id = $request AND m.role = 1 AND m.message_id = $output
                    AND m.end_batch_index IS NOT NULL)
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
          ("$hasText", chunk.Text.Length > 0 ? 1 : 0),
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

      bool createsTextRun = chunk.Text.Length > 0;
      if (createsTextRun) {
        using SqliteCommand index = database.Command(transaction, """
          INSERT INTO execution_messages (
              session_id, history_revision, run_id, role, message_id, request_id,
              first_batch_index, created_at)
          SELECT $session, $revision, $run, 1, $output, $request, $index, $created
          WHERE NOT EXISTS (SELECT 1 FROM execution_messages
              WHERE request_id = $request AND role = 1 AND message_id = $output);
          """,
            ("$session", sessionId), ("$revision", historyRevision), ("$run", executionId),
            ("$output", chunk.ResponseId), ("$request", chunk.RequestId), ("$index", chunk.ChunkIndex),
            ("$created", StorageText.UtcTimestamp()));
        index.ExecuteNonQuery();
      }

      if (chunk.IsFinal) {
        using SqliteCommand close = database.Command(transaction, """
          UPDATE execution_messages SET end_batch_index = $end
          WHERE request_id = $request AND role = 1 AND message_id = $output AND end_batch_index IS NULL;
          """,
            ("$end", chunk.ChunkIndex + 1), ("$request", chunk.RequestId), ("$output", chunk.ResponseId));
        close.ExecuteNonQuery();
      }

      return true;
    });
  }

  // A provider-observed boundary closes the current text run and reserves a display entry,
  // but it does not authorize execution or persist any arguments.
  internal bool ObserveTool(Guid sessionId, Guid executionId, long historyRevision,
      Guid requestId, string callId, string? name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(callId);
    if (name is not null && string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("A tool name must be nonempty when supplied.", nameof(name));

    return database.Write(transaction => {
      using (SqliteCommand existing = database.Command(transaction, """
        SELECT tool_name FROM execution_messages
        WHERE request_id = $request AND role = 2 AND call_id = $call;
        """, ("$request", requestId), ("$call", callId)))
      using (SqliteDataReader reader = existing.ExecuteReader()) {
        if (reader.Read()) {
          string? savedName = reader.IsDBNull(0) ? null : reader.GetString(0);
          if (savedName is not null && name is not null && savedName != name)
            return false;
          if (savedName is null && name is not null) {
            reader.Close();
            using SqliteCommand updateName = database.Command(transaction, """
              UPDATE execution_messages SET tool_name = $name
              WHERE request_id = $request AND role = 2 AND call_id = $call AND tool_name IS NULL;
              """, ("$name", name), ("$request", requestId), ("$call", callId));
            updateName.ExecuteNonQuery();
          }
          return true;
        }
      }

      using SqliteCommand allowed = database.Command(transaction, """
        SELECT EXISTS (SELECT 1 FROM execution_requests q
            JOIN execution_runs r ON r.run_id = q.run_id JOIN sessions s ON s.session_id = r.session_id
            WHERE q.request_id = $request AND q.run_id = $run AND q.outcome IS NULL AND q.output_final = 0
                AND r.session_id = $session AND r.history_revision = $revision AND s.history_revision = $revision
                AND r.stopping = 0 AND r.outcome IS NULL
                AND NOT EXISTS (SELECT 1 FROM execution_requests later
                    WHERE later.run_id = $run AND later.request_index > q.request_index));
        """, ("$session", sessionId), ("$run", executionId), ("$revision", historyRevision),
          ("$request", requestId));
      if ((long)allowed.ExecuteScalar()! == 0)
        return false;

      using SqliteCommand closeRun = database.Command(transaction, """
        UPDATE execution_messages SET end_batch_index = (
            SELECT next_batch_index FROM execution_requests WHERE request_id = $request)
        WHERE request_id = $request AND role = 1 AND end_batch_index IS NULL;
        """, ("$request", requestId));
      closeRun.ExecuteNonQuery();

      using SqliteCommand reserve = database.Command(transaction, """
        INSERT INTO execution_messages (
            session_id, history_revision, run_id, role, message_id, request_id, created_at, call_id, tool_name)
        VALUES ($session, $revision, $run, 2, $entry, $request, $created, $call, $name);
        """,
          ("$session", sessionId), ("$revision", historyRevision), ("$run", executionId),
          ("$entry", Guid.NewGuid()), ("$request", requestId), ("$created", StorageText.UtcTimestamp()),
          ("$call", callId), ("$name", name));
      reserve.ExecuteNonQuery();
      return true;
    });
  }

  internal bool CompleteRequest(Guid sessionId, Guid executionId, long historyRevision, LlmRequestResult completion) {
    if (!Enum.IsDefined(completion.Outcome))
      throw new ArgumentOutOfRangeException(nameof(completion));

    if (completion.ToolCalls is { Count: > 16 })
      throw new ArgumentException("A response may authorize at most 16 tool calls.", nameof(completion));

    var calls = (completion.ToolCalls ?? []).Select(call =>
        (call.CallId, call.Name, Arguments: StorageText.JsonText(call.Arguments, requireObject: true))).ToArray();
    if (calls.Any(call => string.IsNullOrWhiteSpace(call.CallId) || string.IsNullOrWhiteSpace(call.Name))
        || calls.Select(call => call.CallId).Distinct(StringComparer.Ordinal).Count() != calls.Length
        || (calls.Length != 0 && completion.Outcome != ExecutionOutcome.Completed))
      throw new ArgumentException("Only a completed response may authorize up to 16 distinct, named tool calls.", nameof(completion));

    string? providerResponseJson = null;
    if (completion.ProviderResponse is { } response) {
      if (completion.Outcome != ExecutionOutcome.Completed)
        throw new ArgumentException("Only a completed response may retain provider context.", nameof(completion));
      _ = LlmProviderContext.Restore(response);
      providerResponseJson = JsonSerializer.Serialize(response);
    }
    string? usageJson = completion.Usage is null ? null : JsonSerializer.Serialize(completion.Usage);

    return database.Write(transaction => CompleteRequest(transaction, sessionId, executionId,
        historyRevision, completion, calls, providerResponseJson, usageJson));
  }

  private bool CompleteRequest(SqliteTransaction transaction, Guid sessionId, Guid executionId,
      long historyRevision, LlmRequestResult completion, (string CallId, string Name, string Arguments)[] calls,
      string? providerResponseJson, string? usageJson) {
    bool alreadyCompleted;
    using (SqliteCommand read = database.Command(transaction, """
      SELECT q.outcome, q.error, q.output_final, r.stopping, r.outcome,
          EXISTS (SELECT 1 FROM execution_requests later
              WHERE later.run_id = $run AND later.request_index > q.request_index), c.response_json, q.usage_json
      FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
      JOIN sessions s ON s.session_id = r.session_id
      LEFT JOIN execution_provider_context c ON c.request_id = q.request_id
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

      ExecutionOutcome? savedOutcome = reader.IsDBNull(0) ? null : (ExecutionOutcome)reader.GetInt32(0);
      string? savedError = reader.IsDBNull(1) ? null : reader.GetString(1);
      bool hasFinalOutput = reader.GetInt32(2) != 0;
      bool stopRequested = reader.GetInt32(3) != 0;
      bool executionSettled = !reader.IsDBNull(4);
      alreadyCompleted = savedOutcome is not null;
      if (alreadyCompleted) {
        string? savedProviderResponseJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        string? savedUsageJson = reader.IsDBNull(7) ? null : reader.GetString(7);
        if (savedOutcome != completion.Outcome || savedError != completion.Error
            || savedProviderResponseJson != providerResponseJson || savedUsageJson != usageJson)
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

    if (completion.Outcome == ExecutionOutcome.Completed
        && !MatchesObservedCalls(transaction, completion.RequestId, calls))
      return false;

    foreach (var call in calls) {
      using SqliteCommand exists = database.Command(transaction,
          "SELECT EXISTS(SELECT 1 FROM execution_tools WHERE run_id = $run AND call_id = $call);",
          ("$run", executionId), ("$call", call.CallId));
      if ((long)exists.ExecuteScalar()! != 0)
        return false;
    }

    using SqliteCommand update = database.Command(transaction, """
      UPDATE execution_requests SET outcome = $outcome, error = $error, usage_json = $usage
      WHERE request_id = $request;
      UPDATE execution_inputs SET outcome = $outcome WHERE request_id = $request;
      """,
        ("$outcome", (int)completion.Outcome),
        ("$error", completion.Error),
        ("$usage", usageJson),
        ("$request", completion.RequestId));
    update.ExecuteNonQuery();
    using (SqliteCommand closeRun = database.Command(transaction, """
      UPDATE execution_messages SET end_batch_index = (
          SELECT next_batch_index FROM execution_requests WHERE request_id = $request)
      WHERE request_id = $request AND role = 1 AND end_batch_index IS NULL;
      """, ("$request", completion.RequestId)))
      closeRun.ExecuteNonQuery();
    if (providerResponseJson is not null) {
      using SqliteCommand retain = database.Command(transaction, """
        INSERT INTO execution_provider_context (request_id, response_json) VALUES ($request, $response);
        """, ("$request", completion.RequestId), ("$response", providerResponseJson));
      retain.ExecuteNonQuery();
    }

    var observed = ReadObservedCalls(transaction, completion.RequestId);
    for (int index = 0; index < calls.Length; index++) {
      var call = calls[index];
      if (observed.ContainsKey(call.CallId)) {
        using SqliteCommand attach = database.Command(transaction, """
          UPDATE execution_messages SET tool_name = $name
          WHERE request_id = $request AND role = 2 AND call_id = $call;
          """, ("$name", call.Name), ("$request", completion.RequestId), ("$call", call.CallId));
        attach.ExecuteNonQuery();
      } else {
        ReserveCompletedCall(transaction, sessionId, executionId, historyRevision, completion.RequestId,
            call.CallId, call.Name);
      }

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

  private bool MatchesObservedCalls(SqliteTransaction transaction, Guid requestId,
      (string CallId, string Name, string Arguments)[] calls) {
    List<(string CallId, string? Name)> observed = ReadObservedCallsInOrder(transaction, requestId);
    int callIndex = 0;
    foreach (var reservation in observed) {
      while (callIndex < calls.Length && calls[callIndex].CallId != reservation.CallId)
        callIndex++;
      if (callIndex == calls.Length || (reservation.Name is not null && reservation.Name != calls[callIndex].Name))
        return false;
      callIndex++;
    }

    return true;
  }

  private void ReserveCompletedCall(SqliteTransaction transaction, Guid sessionId, Guid executionId,
      long historyRevision, Guid requestId, string callId, string name) {
    using SqliteCommand reserve = database.Command(transaction, """
      INSERT INTO execution_messages (
          session_id, history_revision, run_id, role, message_id, request_id, created_at, call_id, tool_name)
      VALUES ($session, $revision, $run, 2, $entry, $request, $created, $call, $name);
      """,
        ("$session", sessionId), ("$revision", historyRevision), ("$run", executionId),
        ("$entry", Guid.NewGuid()), ("$request", requestId), ("$created", StorageText.UtcTimestamp()),
        ("$call", callId), ("$name", name));
    reserve.ExecuteNonQuery();
  }

  private Dictionary<string, string?> ReadObservedCalls(SqliteTransaction transaction, Guid requestId) =>
      ReadObservedCallsInOrder(transaction, requestId).ToDictionary(call => call.CallId, call => call.Name,
          StringComparer.Ordinal);

  private List<(string CallId, string? Name)> ReadObservedCallsInOrder(SqliteTransaction transaction, Guid requestId) {
    using SqliteCommand read = database.Command(transaction, """
      SELECT call_id, tool_name FROM execution_messages
      WHERE request_id = $request AND role = 2 ORDER BY ordinal;
      """, ("$request", requestId));
    using SqliteDataReader reader = read.ExecuteReader();
    List<(string CallId, string? Name)> observed = [];
    while (reader.Read())
      observed.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
    return observed;
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
