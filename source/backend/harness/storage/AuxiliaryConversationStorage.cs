using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Retains isolated prepass requests and tool facts without placing them in model history.
internal sealed class AuxiliaryConversationStorage(Database database, ToolResultCodec toolResults) {
  internal bool Create(ExecutionIdentity parent, Guid conversationId, string purpose, LlmSelection selection,
      IReadOnlyList<ChatMessage> input, IReadOnlyList<AIFunctionDeclaration> tools) {
    if (conversationId == Guid.Empty)
      throw new ArgumentException("Conversation ID cannot be empty.", nameof(conversationId));
    ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
    ArgumentNullException.ThrowIfNull(selection);
    _ = StorageText.StrictUtf8.GetByteCount(purpose);
    selection.Validate();
    string selectionJson = LlmSelectionJson.Write(selection)!;
    string inputJson = WriteInputSnapshot(input, tools);

    return database.Write(transaction => database.Execute(transaction, """
      INSERT INTO auxiliary_conversations (
          conversation_id, session_id, run_id, history_revision, purpose, llm_selection_json, input_json, created_at)
      SELECT $conversation, r.session_id, r.run_id, r.history_revision, $purpose, $selection, $input, $created
      FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
      WHERE r.session_id = $session AND r.run_id = $run AND r.history_revision = $revision
          AND s.history_revision = $revision AND r.stopping = 0 AND r.outcome IS NULL
          AND NOT EXISTS (SELECT 1 FROM auxiliary_conversations WHERE conversation_id = $conversation);
      """, ExecutionSql.Parameters(parent, ("$conversation", conversationId), ("$purpose", purpose),
        ("$selection", selectionJson), ("$input", inputJson), ("$created", StorageText.UtcTimestamp()))) == 1);
  }

  // A committed request is an attempt even though auxiliary prompts never enter main input placement.
  internal bool StartRequest(ExecutionIdentity parent, Guid conversationId, Guid requestId) {
    if (conversationId == Guid.Empty || requestId == Guid.Empty)
      throw new ArgumentException("Auxiliary conversation and request identities are required.");

    return database.Write(transaction => database.Execute(transaction, $"""
      INSERT INTO auxiliary_requests (request_id, conversation_id, request_index, created_at)
      SELECT $request, c.conversation_id,
          COALESCE((SELECT MAX(previous.request_index) + 1 FROM auxiliary_requests previous
              WHERE previous.conversation_id = c.conversation_id), 0), $created
      FROM auxiliary_conversations c
      JOIN execution_runs r ON r.run_id = c.run_id
      JOIN sessions s ON s.session_id = c.session_id
      WHERE c.conversation_id = $conversation AND c.session_id = $session AND c.run_id = $run
          AND c.history_revision = $revision AND c.retired_at IS NULL AND {ExecutionSql.CurrentRunCondition}
          AND NOT EXISTS (SELECT 1 FROM auxiliary_requests previous
              WHERE previous.conversation_id = c.conversation_id
                  AND (previous.outcome IS NULL OR EXISTS (SELECT 1 FROM auxiliary_tools t
                      WHERE t.request_id = previous.request_id AND t.result_json IS NULL)))
          AND NOT EXISTS (SELECT 1 FROM auxiliary_requests WHERE request_id = $request);
      """, ExecutionSql.Parameters(parent, ("$conversation", conversationId), ("$request", requestId),
        ("$created", StorageText.UtcTimestamp()))) == 1);
  }

  internal bool Append(ExecutionIdentity parent, Guid conversationId, LlmResponseChunk chunk) {
    ArgumentNullException.ThrowIfNull(chunk);
    ArgumentOutOfRangeException.ThrowIfNegative(chunk.ChunkIndex);
    if (chunk.RequestId == Guid.Empty || chunk.ResponseId == Guid.Empty || chunk.Text is null
        || StorageText.StrictUtf8.GetByteCount(chunk.Text) > StorageText.MaximumBytes)
      throw new ArgumentException("Output requires identities and at most 16 KiB of valid UTF-8 text.", nameof(chunk));

    return database.Write(transaction => {
      int updated = database.Execute(transaction, $"""
        UPDATE auxiliary_requests SET next_chunk_index = next_chunk_index + 1,
            response_final = $final
        WHERE request_id = $request AND conversation_id = $conversation AND outcome IS NULL
            AND next_chunk_index = $index AND response_final = 0
            AND NOT EXISTS (SELECT 1 FROM auxiliary_requests later
                WHERE later.conversation_id = $conversation AND later.request_index > auxiliary_requests.request_index)
            AND EXISTS (SELECT 1 FROM auxiliary_conversations c
                JOIN execution_runs r ON r.run_id = c.run_id JOIN sessions s ON s.session_id = c.session_id
                WHERE c.conversation_id = $conversation AND c.session_id = $session AND c.run_id = $run
                    AND c.history_revision = $revision AND c.retired_at IS NULL AND {ExecutionSql.CurrentRunCondition});
        """, ExecutionSql.Parameters(parent, ("$conversation", conversationId), ("$request", chunk.RequestId),
          ("$index", chunk.ChunkIndex), ("$final", chunk.IsFinal ? 1 : 0)));
      if (updated == 0)
        return false;

      database.Execute(transaction, """
        INSERT INTO auxiliary_output (conversation_id, request_id, chunk_index, response_id, text_utf8, is_final)
        VALUES ($conversation, $request, $index, $response, $text, $final);
        """, ("$conversation", conversationId), ("$request", chunk.RequestId), ("$index", chunk.ChunkIndex),
        ("$response", chunk.ResponseId), ("$text", StorageText.StrictUtf8.GetBytes(chunk.Text)),
        ("$final", chunk.IsFinal ? 1 : 0));
      return true;
    });
  }

  // Observations are scoped to a conversation and never authorize tool execution by themselves.
  internal bool RecordToolCall(ExecutionIdentity parent, Guid conversationId, Guid requestId, string callId, string? name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(callId);
    if (name is not null)
      ArgumentException.ThrowIfNullOrWhiteSpace(name);

    return database.Write(transaction => database.Execute(transaction, $"""
      INSERT INTO auxiliary_observed_calls (
          conversation_id, request_id, observed_index, next_chunk_index, call_id, name)
      SELECT c.conversation_id, q.request_id,
          (SELECT count(*) FROM auxiliary_observed_calls observed WHERE observed.request_id = q.request_id),
          q.next_chunk_index, $call, $name
      FROM auxiliary_requests q JOIN auxiliary_conversations c ON c.conversation_id = q.conversation_id
      JOIN execution_runs r ON r.run_id = c.run_id JOIN sessions s ON s.session_id = c.session_id
      WHERE q.request_id = $request AND q.conversation_id = $conversation AND q.outcome IS NULL
          AND q.response_final = 0 AND c.session_id = $session AND c.run_id = $run
          AND c.history_revision = $revision AND c.retired_at IS NULL AND {ExecutionSql.CurrentRunCondition}
          AND NOT EXISTS (SELECT 1 FROM auxiliary_requests later
              WHERE later.conversation_id = $conversation AND later.request_index > q.request_index)
          AND NOT EXISTS (SELECT 1 FROM auxiliary_observed_calls
              WHERE conversation_id = $conversation AND call_id = $call)
          AND NOT EXISTS (SELECT 1 FROM auxiliary_tools WHERE conversation_id = $conversation AND call_id = $call)
          AND (SELECT count(*) FROM auxiliary_observed_calls observed WHERE observed.request_id = q.request_id) < 16;
      """, ExecutionSql.Parameters(parent, ("$conversation", conversationId), ("$request", requestId),
        ("$call", callId), ("$name", name))) == 1);
  }

  internal bool CompleteRequest(ExecutionIdentity parent, Guid conversationId, LlmRequestResult completion) {
    ValidatedLlmRequestResult validated = LlmRequestResultValidator.Validate(completion);
    return database.Write(transaction => CompleteRequest(transaction, parent, conversationId, completion, validated));
  }

  private bool CompleteRequest(SqliteTransaction transaction, ExecutionIdentity parent, Guid conversationId,
      LlmRequestResult completion, ValidatedLlmRequestResult validated) {
    RequestState? state = database.QuerySingleOrDefault(transaction, """
      SELECT q.outcome, q.response_final, r.stopping, r.outcome AS parent_outcome,
          c.retired_at IS NOT NULL AS retired,
          EXISTS (SELECT 1 FROM auxiliary_requests later
              WHERE later.conversation_id = c.conversation_id AND later.request_index > q.request_index) AS has_later
      FROM auxiliary_requests q JOIN auxiliary_conversations c ON c.conversation_id = q.conversation_id
      JOIN execution_runs r ON r.run_id = c.run_id JOIN sessions s ON s.session_id = c.session_id
      WHERE q.request_id = $request AND q.conversation_id = $conversation AND c.session_id = $session
          AND c.run_id = $run AND c.history_revision = $revision AND s.history_revision = $revision;
      """, row => new RequestState((ExecutionOutcome?)row.NullableInt("outcome"), row.Bool("response_final"),
        row.Bool("stopping"), row.NullableInt("parent_outcome") is not null,
        row.Bool("retired"), row.Bool("has_later")),
      ExecutionSql.Parameters(parent, ("$conversation", conversationId), ("$request", completion.RequestId)));
    if (state is null || state.Outcome is not null || state.ParentSettled || state.Retired || state.HasLater)
      return false;
    if (completion.Outcome == ExecutionOutcome.Completed
        && (state.Stopping || !state.HasFinalOutput || !MatchesObservedCalls(transaction, conversationId, completion.RequestId, validated.Calls)))
      return false;

    if (validated.Calls.Any(call => database.Scalar<bool>(transaction, """
        SELECT EXISTS (SELECT 1 FROM auxiliary_observed_calls
            WHERE conversation_id = $conversation AND call_id = $call AND request_id <> $request)
            OR EXISTS (SELECT 1 FROM auxiliary_tools WHERE conversation_id = $conversation AND call_id = $call);
        """, ("$conversation", conversationId), ("$call", call.CallId), ("$request", completion.RequestId))))
      return false;

    database.Execute(transaction, """
      UPDATE auxiliary_requests SET outcome = $outcome, error = $error,
          usage_json = $usage, provider_response_json = $provider, completed_at = $completed
      WHERE request_id = $request AND conversation_id = $conversation AND outcome IS NULL;
      """, ("$outcome", (int)completion.Outcome), ("$error", completion.Error),
      ("$usage", validated.UsageJson), ("$provider", validated.ProviderResponseJson),
      ("$completed", StorageText.UtcTimestamp()), ("$request", completion.RequestId), ("$conversation", conversationId));

    for (int index = 0; index < validated.Calls.Length; index++) {
      var call = validated.Calls[index];
      database.Execute(transaction, """
        INSERT INTO auxiliary_tools (
            conversation_id, request_id, call_index, call_id, name, arguments_json, arguments_excerpt)
        VALUES ($conversation, $request, $index, $call, $name, $arguments, $excerpt);
        """, ("$conversation", conversationId), ("$request", completion.RequestId), ("$index", index),
        ("$call", call.CallId), ("$name", call.Name), ("$arguments", call.Arguments), ("$excerpt", call.Excerpt));
    }
    return true;
  }

  private bool MatchesObservedCalls(SqliteTransaction transaction, Guid conversationId, Guid requestId,
      (string CallId, string Name, string Arguments, string Excerpt)[] calls) {
    List<(string CallId, string? Name)> observed = database.Query(transaction, """
      SELECT call_id, name FROM auxiliary_observed_calls WHERE conversation_id = $conversation
          AND request_id = $request ORDER BY observed_index;
      """, row => (row.String("call_id"), row.NullableString("name")),
      ("$conversation", conversationId), ("$request", requestId));
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

  // Claims cannot repeat; foreground calls may run concurrently.
  internal bool TryStartTool(ExecutionIdentity parent, Guid conversationId, Guid requestId, string callId) {
    ArgumentException.ThrowIfNullOrWhiteSpace(callId);
    return database.Write(transaction => database.Execute(transaction, $"""
      UPDATE auxiliary_tools SET started = 1, started_at = $started
      WHERE conversation_id = $conversation AND request_id = $request AND call_id = $call
          AND started = 0 AND outcome IS NULL AND result_json IS NULL
          AND EXISTS (SELECT 1 FROM auxiliary_requests q
              JOIN auxiliary_conversations c ON c.conversation_id = q.conversation_id
              JOIN execution_runs r ON r.run_id = c.run_id JOIN sessions s ON s.session_id = c.session_id
              WHERE q.request_id = $request AND q.conversation_id = $conversation
                  AND q.outcome = $completed AND c.session_id = $session AND c.run_id = $run
                  AND c.history_revision = $revision AND c.retired_at IS NULL
                  AND {ExecutionSql.CurrentRunCondition}
                  AND NOT EXISTS (SELECT 1 FROM auxiliary_requests later
                      WHERE later.conversation_id = $conversation AND later.request_index > q.request_index));
      """, ExecutionSql.Parameters(parent, ("$conversation", conversationId), ("$request", requestId),
        ("$call", callId), ("$started", StorageText.UtcTimestamp()),
        ("$completed", (int)ExecutionOutcome.Completed))) == 1);
  }

  // The original result is retained; stop permits a running foreground call to save its actual outcome.
  internal bool CompleteTool(ExecutionIdentity parent, Guid conversationId, Guid requestId, LlmToolResult result) {
    ArgumentNullException.ThrowIfNull(result);
    if (!Enum.IsDefined(result.Outcome))
      throw new ArgumentOutOfRangeException(nameof(result));
    string json = toolResults.Write(result.Output);
    string? error = result.Output.IsError ? result.Output.Text[..Math.Min(result.Output.Text.Length, 1024)] : null;

    return database.Write(transaction => {
      bool canSave = database.Scalar<bool>(transaction, """
        SELECT EXISTS (SELECT 1 FROM auxiliary_tools t
            JOIN auxiliary_requests q ON q.request_id = t.request_id
            JOIN auxiliary_conversations c ON c.conversation_id = t.conversation_id
            JOIN execution_runs r ON r.run_id = c.run_id JOIN sessions s ON s.session_id = c.session_id
            WHERE t.conversation_id = $conversation AND t.request_id = $request AND t.call_id = $call
                AND t.started = 1 AND t.result_json IS NULL AND q.outcome = $completed
                AND r.outcome IS NULL AND r.session_id = $session AND c.run_id = $run
                AND c.history_revision = $revision AND s.history_revision = $revision AND c.retired_at IS NULL);
        """, ExecutionSql.Parameters(parent, ("$conversation", conversationId), ("$request", requestId),
          ("$call", result.CallId), ("$completed", (int)ExecutionOutcome.Completed)));
      if (!canSave)
        return false;

      return database.Execute(transaction, """
        UPDATE auxiliary_tools SET outcome = $outcome, result_json = $result, error = $error, ended_at = $ended
        WHERE conversation_id = $conversation AND request_id = $request AND call_id = $call
            AND started = 1 AND result_json IS NULL;
        """, ("$outcome", (int)result.Outcome), ("$result", json), ("$error", error),
        ("$ended", StorageText.UtcTimestamp()), ("$conversation", conversationId),
        ("$request", requestId), ("$call", result.CallId)) == 1;
    });
  }

  // Retirement preserves saved responses and actual results, replacing only unresolved work with uncertainty.
  internal void Retire(SqliteTransaction transaction, Guid executionId, ExecutionOutcome parentOutcome) {
    string ended = StorageText.UtcTimestamp();
    string notExecuted = "Tool was not executed.";
    string unknown = "Tool result unavailable; effects may have occurred. Do not automatically retry.";
    database.Execute(transaction, """
      UPDATE auxiliary_tools SET outcome = $tool_outcome, interrupted = 1,
          error = CASE WHEN started = 0 THEN $not_executed ELSE $unknown END,
          result_json = CASE WHEN started = 0 THEN $not_executed_result ELSE $unknown_result END,
          ended_at = CASE WHEN started = 1 THEN $ended ELSE NULL END
      WHERE conversation_id IN (SELECT conversation_id FROM auxiliary_conversations WHERE run_id = $run)
          AND result_json IS NULL;
      UPDATE auxiliary_requests SET outcome = $request_outcome,
          error = COALESCE(error, $request_error), completed_at = $ended
      WHERE conversation_id IN (SELECT conversation_id FROM auxiliary_conversations WHERE run_id = $run)
          AND outcome IS NULL;
      UPDATE auxiliary_conversations SET retired_at = $ended, parent_outcome = $parent_outcome
      WHERE run_id = $run AND retired_at IS NULL;
      """, ("$tool_outcome", (int)(parentOutcome == ExecutionOutcome.Cancelled
          ? ExecutionOutcome.Cancelled : ExecutionOutcome.Failed)),
      ("$not_executed", notExecuted), ("$unknown", unknown),
      ("$not_executed_result", toolResults.Error(notExecuted)), ("$unknown_result", toolResults.Error(unknown)),
      ("$ended", ended), ("$request_outcome", (int)(parentOutcome == ExecutionOutcome.Cancelled
          ? ExecutionOutcome.Cancelled : ExecutionOutcome.Failed)),
      ("$request_error", parentOutcome == ExecutionOutcome.Cancelled
          ? "Interrupted by parent cancellation." : "Interrupted by parent failure."),
      ("$parent_outcome", (int)parentOutcome), ("$run", executionId));
  }

  private static string WriteInputSnapshot(IReadOnlyList<ChatMessage> input,
      IReadOnlyList<AIFunctionDeclaration> tools) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(tools);

    var messages = input.Select(message => {
      ArgumentNullException.ThrowIfNull(message);
      string role = message.Role.ToString();
      if (role is not ("system" or "developer" or "user"))
        throw new ArgumentException("Initial auxiliary input supports only system, developer and user messages.", nameof(message));
      string[] texts = message.Contents.Select(content => content is TextContent text
          ? text.Text
          : throw new ArgumentException("Initial auxiliary input supports only text content.", nameof(content))).ToArray();
      return new { role, texts };
    }).ToArray();
    var declarations = tools.Select(tool => {
      ArgumentNullException.ThrowIfNull(tool);
      if (string.IsNullOrWhiteSpace(tool.Name) || tool.JsonSchema.ValueKind == JsonValueKind.Undefined)
        throw new ArgumentException("Initial auxiliary tools require a name and JSON schema.", nameof(tool));
      return new { name = tool.Name, description = tool.Description, schema = tool.JsonSchema };
    }).ToArray();

    return JsonSerializer.Serialize(new { messages, tools = declarations });
  }

  private sealed record RequestState(ExecutionOutcome? Outcome, bool HasFinalOutput,
      bool Stopping, bool ParentSettled, bool Retired, bool HasLater);
}
