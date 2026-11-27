using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Appends ordered output segments and records explicit request completion.
internal sealed class LlmResponseStorage(Database database) {
  internal bool Append(ExecutionIdentity execution, LlmResponseChunk chunk) {
    ArgumentOutOfRangeException.ThrowIfNegative(chunk.ChunkIndex);
    if (chunk.RequestId == Guid.Empty || chunk.ResponseId == Guid.Empty
        || StorageText.StrictUtf8.GetByteCount(chunk.Text) > StorageText.MaximumBytes) {
      throw new ArgumentException("Output requires identities and at most 16 KiB of valid UTF-8 text.", nameof(chunk));
    }

    return database.Write(transaction => {
      int updatedRequestCount = database.Execute(transaction, $"""
        UPDATE execution_requests SET next_chunk_index = next_chunk_index + 1,
            response_id = CASE WHEN response_id IS NULL AND $hasText = 1 THEN $output ELSE response_id END,
            response_final = $final
        WHERE request_id = $request AND run_id = $run AND outcome IS NULL
            AND next_chunk_index = $index AND response_final = 0
            AND NOT EXISTS (SELECT 1 FROM execution_messages m
                WHERE m.request_id = $request AND m.role = {(int)TranscriptEntryKind.Text} AND m.end_chunk_index IS NULL
                    AND m.message_id <> $output)
            AND NOT EXISTS (SELECT 1 FROM execution_messages m
                WHERE m.request_id = $request AND m.role = {(int)TranscriptEntryKind.Text} AND m.message_id = $output
                    AND m.end_chunk_index IS NOT NULL)
            AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
                WHERE r.run_id = $run AND r.session_id = $session AND {ExecutionSql.CurrentRunCondition});
        """, ExecutionSql.Parameters(execution,
          ("$request", chunk.RequestId), ("$output", chunk.ResponseId), ("$index", chunk.ChunkIndex),
          ("$hasText", chunk.Text.Length > 0 ? 1 : 0), ("$final", chunk.IsFinal ? 1 : 0)));
      if (updatedRequestCount == 0)
        return false;

      database.Execute(transaction, """
        INSERT INTO execution_output (request_id, chunk_index, text_utf8, is_final)
        VALUES ($request, $index, $text, $final);
        """,
          ("$request", chunk.RequestId), ("$index", chunk.ChunkIndex),
          ("$text", StorageText.StrictUtf8.GetBytes(chunk.Text)), ("$final", chunk.IsFinal ? 1 : 0));

      bool hasText = chunk.Text.Length > 0;
      if (hasText) {
        int insertedTextMessageCount = database.Execute(transaction, $"""
          INSERT INTO execution_messages (
              session_id, history_revision, run_id, role, message_id, request_id,
              first_chunk_index, created_at, layout_weight)
          SELECT $session, $revision, $run, {(int)TranscriptEntryKind.Text}, $output, $request, $index, $created, 256 + $bytes
          WHERE NOT EXISTS (SELECT 1 FROM execution_messages
              WHERE request_id = $request AND role = {(int)TranscriptEntryKind.Text} AND message_id = $output);
          """, ExecutionSql.Parameters(execution,
            ("$output", chunk.ResponseId), ("$request", chunk.RequestId), ("$index", chunk.ChunkIndex),
            ("$created", StorageText.UtcTimestamp()), ("$bytes", StorageText.StrictUtf8.GetByteCount(chunk.Text))));
        if (insertedTextMessageCount == 0) {
          database.Execute(transaction, $"""
            UPDATE execution_messages SET layout_weight = layout_weight + $bytes
            WHERE request_id = $request AND role = {(int)TranscriptEntryKind.Text} AND message_id = $output;
            """, ("$request", chunk.RequestId), ("$output", chunk.ResponseId),
              ("$bytes", StorageText.StrictUtf8.GetByteCount(chunk.Text)));
        }
      } else {
        // Empty committed segments still advance source cursors in this history revision.
        database.Execute(transaction,
            "UPDATE sessions SET position_version = position_version + 1 WHERE session_id = $session;",
            ("$session", execution.SessionId));
      }

      if (chunk.IsFinal) {
        database.Execute(transaction, $"""
          UPDATE execution_messages SET end_chunk_index = $end
          WHERE request_id = $request AND role = {(int)TranscriptEntryKind.Text} AND message_id = $output AND end_chunk_index IS NULL;
          """, ("$end", chunk.ChunkIndex + 1), ("$request", chunk.RequestId), ("$output", chunk.ResponseId));
      }

      return true;
    });
  }

  // A provider-observed boundary closes the current text run and reserves a display entry,
  // but it does not authorize execution or persist any arguments.
  internal bool RecordToolCall(ExecutionIdentity execution, Guid requestId, string callId, string? name) =>
      RecordToolCall(execution, requestId, callId, name, entryId: null) is not null;

  internal Guid? RecordToolCall(ExecutionIdentity execution, Guid requestId, string callId, string? name, Guid? entryId) {
    ArgumentException.ThrowIfNullOrWhiteSpace(callId);
    if (name is not null && string.IsNullOrWhiteSpace(name))
      throw new ArgumentException("A tool name must be nonempty when supplied.", nameof(name));

    return database.Write<Guid?>(transaction => {
      bool allowed = database.Scalar<bool>(transaction, $"""
        SELECT EXISTS (SELECT 1 FROM execution_requests q
            JOIN execution_runs r ON r.run_id = q.run_id JOIN sessions s ON s.session_id = r.session_id
            WHERE q.request_id = $request AND q.run_id = $run AND q.outcome IS NULL AND q.response_final = 0
                AND r.session_id = $session AND {ExecutionSql.CurrentRunCondition}
                AND NOT EXISTS (SELECT 1 FROM execution_requests later
                    WHERE later.run_id = $run AND later.request_index > q.request_index));
        """, ExecutionSql.Parameters(execution, ("$request", requestId)));
      if (!allowed)
        return null;

      var existing = database.Query(transaction, $"""
        SELECT message_id, tool_name FROM execution_messages
        WHERE request_id = $request AND role = {(int)TranscriptEntryKind.Tool} AND call_id = $call;
        """, row => (row.Guid("message_id"), row.NullableString("tool_name")), ("$request", requestId), ("$call", callId));
      if (existing.Count != 0) {
        (Guid savedId, string? savedName) = existing[0];
        if (savedName is not null && name is not null && savedName != name)
          return null;
        if (savedName is null && name is not null) {
          database.Execute(transaction, $"""
            UPDATE execution_messages SET tool_name = $name
            WHERE request_id = $request AND role = {(int)TranscriptEntryKind.Tool} AND call_id = $call AND tool_name IS NULL;
            """, ("$name", name), ("$request", requestId), ("$call", callId));
        }
        return savedId;
      }

      database.Execute(transaction, $"""
        UPDATE execution_messages SET end_chunk_index = (
            SELECT next_chunk_index FROM execution_requests WHERE request_id = $request)
        WHERE request_id = $request AND role = {(int)TranscriptEntryKind.Text} AND end_chunk_index IS NULL;
        """, ("$request", requestId));

      Guid reservedId = entryId ?? Guid.NewGuid();
      ReserveCall(transaction, execution, requestId, callId, name, reservedId);
      return reservedId;
    });
  }

  internal bool CompleteRequest(ExecutionIdentity execution, LlmRequestResult completion) {
    ValidatedLlmRequestResult validated = LlmRequestResultValidator.Validate(completion);
    return database.Write(transaction => CompleteRequest(transaction, execution,
        completion, validated.Calls, validated.ProviderResponseJson, validated.UsageJson));
  }

  private bool CompleteRequest(SqliteTransaction transaction, ExecutionIdentity execution,
      LlmRequestResult completion,
      (string CallId, string Name, string Arguments, string Excerpt)[] calls,
      string? providerResponseJson, string? usageJson) {
    RequestCompletionState? state = database.QuerySingleOrDefault(transaction, """
      SELECT q.outcome AS request_outcome, q.error, q.response_final, r.stopping,
          r.outcome IS NOT NULL AS execution_settled,
          EXISTS (SELECT 1 FROM execution_requests later
              WHERE later.run_id = $run AND later.request_index > q.request_index) AS has_later_request,
          c.response_json, q.usage_json
      FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
      JOIN sessions s ON s.session_id = r.session_id
      LEFT JOIN execution_provider_context c ON c.request_id = q.request_id
      WHERE r.session_id = $session AND r.run_id = $run AND q.request_id = $request
          AND r.history_revision = $revision AND s.history_revision = $revision;
      """, row => new RequestCompletionState((ExecutionOutcome?)row.NullableInt("request_outcome"),
          row.NullableString("error"), row.Bool("response_final"), row.Bool("stopping"),
          row.Bool("execution_settled"), row.Bool("has_later_request"),
          row.NullableString("response_json"), row.NullableString("usage_json")),
        ExecutionSql.Parameters(execution, ("$request", completion.RequestId)));
    if (state is null)
      return false;
    if (state.Outcome is not null) {
      return state.Outcome == completion.Outcome && state.Error == completion.Error
          && state.ProviderResponseJson == providerResponseJson && state.UsageJson == usageJson
          && MatchesCalls(transaction, completion.RequestId, calls);
    }
    if (state.ExecutionSettled || state.HasLaterRequest)
      return false;
    if (completion.Outcome == ExecutionOutcome.Completed && (state.StopRequested || !state.HasFinalOutput))
      return false;
    if (completion.Outcome == ExecutionOutcome.Completed
        && !MatchesObservedCalls(transaction, completion.RequestId, calls))
      return false;

    if (calls.Any(call => database.Scalar<bool>(transaction,
        "SELECT EXISTS(SELECT 1 FROM execution_tools WHERE run_id = $run AND call_id = $call);",
        ("$run", execution.ExecutionId), ("$call", call.CallId))))
      return false;

    database.Execute(transaction, """
      UPDATE execution_requests SET outcome = $outcome, error = $error, usage_json = $usage
      WHERE request_id = $request;
      UPDATE execution_inputs SET outcome = $outcome,
          not_sent = CASE WHEN EXISTS (SELECT 1 FROM execution_requests q
              WHERE q.request_id = $request AND q.attempt = 1) THEN 1 ELSE 0 END
      WHERE request_id = $request;
      """,
        ("$outcome", (int)completion.Outcome), ("$error", completion.Error),
        ("$usage", usageJson), ("$request", completion.RequestId));
    database.Execute(transaction, $"""
      UPDATE execution_messages SET end_chunk_index = (
          SELECT next_chunk_index FROM execution_requests WHERE request_id = $request)
      WHERE request_id = $request AND role = {(int)TranscriptEntryKind.Text} AND end_chunk_index IS NULL;
      """, ("$request", completion.RequestId));
    if (providerResponseJson is not null) {
      database.Execute(transaction, """
        INSERT INTO execution_provider_context (request_id, response_json) VALUES ($request, $response);
        """, ("$request", completion.RequestId), ("$response", providerResponseJson));
    }

    var observed = ReadObservedCallsInOrder(transaction, completion.RequestId)
        .ToDictionary(call => call.CallId, call => call.Name, StringComparer.Ordinal);
    for (int index = 0; index < calls.Length; index++) {
      var call = calls[index];
      if (observed.ContainsKey(call.CallId)) {
        database.Execute(transaction, $"""
          UPDATE execution_messages SET tool_name = $name
          WHERE request_id = $request AND role = {(int)TranscriptEntryKind.Tool} AND call_id = $call;
          """, ("$name", call.Name), ("$request", completion.RequestId), ("$call", call.CallId));
      } else {
        ReserveCall(transaction, execution, completion.RequestId, call.CallId, call.Name);
      }

      database.Execute(transaction, """
        INSERT INTO execution_tools (
            run_id, request_id, call_index, call_id, name, arguments_json, arguments_excerpt)
        VALUES ($run, $request, $index, $call, $name, $arguments, $excerpt);
        """,
          ("$run", execution.ExecutionId), ("$request", completion.RequestId), ("$index", index),
          ("$call", call.CallId), ("$name", call.Name), ("$arguments", call.Arguments),
          ("$excerpt", call.Excerpt));
    }
    return true;
  }

  private bool MatchesObservedCalls(SqliteTransaction transaction, Guid requestId,
      (string CallId, string Name, string Arguments, string Excerpt)[] calls) {
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

  private void ReserveCall(SqliteTransaction transaction, ExecutionIdentity execution,
      Guid requestId, string callId, string? name, Guid? entryId = null) {
    database.Execute(transaction, $"""
      INSERT INTO execution_messages (
          session_id, history_revision, run_id, role, message_id, request_id, created_at, call_id, tool_name)
      VALUES ($session, $revision, $run, {(int)TranscriptEntryKind.Tool}, $entry, $request, $created, $call, $name);
      """, ExecutionSql.Parameters(execution,
        ("$entry", entryId ?? Guid.NewGuid()), ("$request", requestId), ("$created", StorageText.UtcTimestamp()),
        ("$call", callId), ("$name", name)));
  }

  private List<(string CallId, string? Name)> ReadObservedCallsInOrder(SqliteTransaction transaction, Guid requestId) =>
      database.Query(transaction, $"""
        SELECT call_id, tool_name FROM execution_messages
        WHERE request_id = $request AND role = {(int)TranscriptEntryKind.Tool} ORDER BY ordinal;
        """, row => (row.String("call_id"), row.NullableString("tool_name")), ("$request", requestId));

  private bool MatchesCalls(SqliteTransaction transaction, Guid requestId,
      (string CallId, string Name, string Arguments, string Excerpt)[] calls) {
    using SqliteCommand read = database.Command(transaction, """
      SELECT call_id, name, arguments_json, arguments_excerpt
      FROM execution_tools WHERE request_id = $request ORDER BY call_index;
      """, ("$request", requestId));
    using SqliteDataReader reader = read.ExecuteReader();
    int index = 0;
    while (reader.Read()) {
      if (index == calls.Length || reader.String("call_id") != calls[index].CallId
          || reader.String("name") != calls[index].Name || reader.String("arguments_json") != calls[index].Arguments
          || reader.String("arguments_excerpt") != calls[index].Excerpt)
        return false;
      index++;
    }
    return index == calls.Length;
  }

  // Captures request and run state before completion checks or writes in the same transaction.
  private sealed record RequestCompletionState(ExecutionOutcome? Outcome, string? Error, bool HasFinalOutput,
      bool StopRequested, bool ExecutionSettled, bool HasLaterRequest, string? ProviderResponseJson, string? UsageJson);
}
