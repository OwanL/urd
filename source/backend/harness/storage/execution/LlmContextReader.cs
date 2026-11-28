using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Compaction;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Execution owns context eligibility: never-attempted history is excluded, while the
// current claim is included to prepare its send. Only accepted continuations can omit a tail.
internal sealed class LlmContextReader(Database database, ToolResultCodec toolResults) {
  internal ReadOnlyCollection<ChatMessage> Read(SqliteTransaction transaction, Guid sessionId,
      Guid executionId, long historyRevision, Guid requestId) =>
      ReadContext(transaction, sessionId, historyRevision, executionId, requestId).ReadMessages().ToList().AsReadOnly();

  internal ModelHistory ReadContext(SqliteTransaction transaction, Guid sessionId,
      long historyRevision, Guid? executionId = null, Guid? requestId = null) {
    SavedHistoryCompaction? boundary = CompactionStorage.ReadLatest(database, transaction, sessionId, historyRevision);
    return ReadContext(transaction, sessionId, historyRevision, executionId, requestId,
        boundary?.FirstRetainedMessageLocation, boundary);
  }

  private ModelHistory ReadContext(SqliteTransaction transaction, Guid sessionId, long historyRevision,
      Guid? executionId, Guid? requestId, HistoryMessageLocation? firstRetained, SavedHistoryCompaction? boundary) {
    using SqliteCommand read = database.Command(transaction, $"""
      WITH requests AS (
          SELECT r.ordinal AS run_ordinal, q.request_index, q.request_id, q.usage_json, q.compaction_id,
              1 AS include_response,
              CASE WHEN EXISTS (SELECT 1 FROM execution_runs continuation
                  WHERE continuation.session_id = r.session_id
                      AND continuation.history_revision = r.history_revision
                      AND continuation.kind = $continuation
                      AND ($run IS NULL OR continuation.ordinal <=
                          (SELECT ordinal FROM execution_runs WHERE run_id = $run))
                      AND continuation.continuation_omitted_request_id = q.request_id)
                  THEN 1 ELSE 0 END AS omit_assistant
          FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
          WHERE r.session_id = $session AND r.history_revision = $revision
              AND ($run IS NULL OR r.ordinal <= (SELECT ordinal FROM execution_runs WHERE run_id = $run))
              AND (q.attempt = 2 OR q.request_id = $request)
              AND ($run IS NULL OR r.run_id <> $run OR q.request_index <=
                  (SELECT request_index FROM execution_requests WHERE request_id = $request))
              AND (r.ordinal > $cutRun OR (r.ordinal = $cutRun AND q.request_index >= $cutRequest))
      ), contents AS (
          SELECT q.run_ordinal, q.request_index, q.request_id, {(int)ContextRowKind.User} AS kind, i.ordinal AS content_index,
              d.original_text_utf8 AS text_utf8, NULL AS call_id,
              CASE WHEN i.provenance = {(int)InputProvenance.AgentQuestion} THEN (SELECT t.title
                  FROM session_prepass_title t WHERE t.session_id = i.sender_session_id) END AS name, NULL AS json,
              NULL AS background, i.sender_session_id AS origin_request_id, NULL AS outcome, i.provenance AS input_provenance,
              CASE WHEN i.provenance = {(int)InputProvenance.AgentQuestion} THEN i.input_id END AS question_id,
              d.image_ids_json AS image_ids_json
          FROM requests q JOIN execution_inputs i ON i.request_id = q.request_id
          JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, {(int)ContextRowKind.Text}, o.chunk_index, o.text_utf8,
              NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
          FROM requests q JOIN execution_output o ON o.request_id = q.request_id
          WHERE q.include_response = 1 AND q.omit_assistant = 0
              AND NOT EXISTS (SELECT 1 FROM execution_provider_context c WHERE c.request_id = q.request_id)
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, {(int)ContextRowKind.ToolCall}, t.call_index, NULL,
              t.call_id, t.name, t.arguments_json, NULL, NULL, NULL, NULL, NULL, NULL
          FROM requests q JOIN execution_tools t ON t.request_id = q.request_id
          WHERE q.include_response = 1 AND q.omit_assistant = 0
              AND NOT EXISTS (SELECT 1 FROM execution_provider_context c WHERE c.request_id = q.request_id)
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, {(int)ContextRowKind.ToolResult}, t.call_index, NULL,
              t.call_id, t.name, t.result_json, t.background, t.request_id, t.outcome, NULL, NULL, NULL
          FROM requests q JOIN execution_tools t ON t.request_id = q.request_id
          WHERE q.include_response = 1 AND (t.result_json IS NOT NULL OR (t.background = 1 AND t.started = 1))
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, {(int)ContextRowKind.ProviderResponse}, 0, NULL, NULL, NULL,
              c.response_json, NULL, NULL, NULL, NULL, NULL, NULL
          FROM requests q JOIN execution_provider_context c ON c.request_id = q.request_id
          WHERE q.include_response = 1
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, {(int)ContextRowKind.ToolCompletion}, c.ordinal, NULL,
              t.call_id, t.name, t.result_json, NULL, c.request_id, t.outcome, NULL, NULL, NULL
          FROM requests q JOIN execution_tool_completions c ON c.claimed_request_id = q.request_id
          JOIN execution_tools t ON t.request_id = c.request_id AND t.call_id = c.call_id
          WHERE c.session_id = $session AND c.history_revision = $revision
      ) SELECT c.request_id, c.kind, c.text_utf8, c.call_id, c.name, c.json, c.background,
          c.origin_request_id, c.outcome, c.input_provenance, c.question_id, c.run_ordinal, c.request_index,
          q.usage_json, q.compaction_id, q.omit_assistant, c.image_ids_json
      FROM contents c JOIN requests q ON q.request_id = c.request_id
      ORDER BY c.run_ordinal, c.request_index, c.kind, c.content_index;
      """,
        ("$session", sessionId), ("$run", executionId), ("$revision", historyRevision), ("$request", requestId),
        ("$continuation", (int)ExecutionRunKind.Continuation),
        ("$cutRun", firstRetained?.ExecutionOrdinal ?? -1),
        ("$cutRequest", firstRetained?.RequestIndexInExecution ?? 0));
    using SqliteDataReader reader = read.ExecuteReader();
    var messages = new List<ChatMessage>();
    var entries = new List<HistoryMessage>();
    var inputMessages = new Dictionary<ChatMessage, Guid?>();
    var imageInputs = new Dictionary<ChatMessage, string>();
    ContextRow? previous = null;
    long? observedTokens = null;
    int observedThrough = 0;
    StringBuilder? text = null;
    ChatMessage? providerMessage = null;
    var calls = new List<AIContent>();
    var results = new List<AIContent>();
    while (reader.Read()) {
      var row = new ContextRow(reader.Guid("request_id"), (ContextRowKind)reader.Int("kind"),
          reader.IsDBNull(reader.GetOrdinal("text_utf8")) ? null
              : StorageText.StrictUtf8.GetString((byte[])reader.GetValue(reader.GetOrdinal("text_utf8"))),
          reader.NullableString("call_id"), reader.NullableString("name"), reader.NullableString("json"),
          reader.NullableInt("background") == 1, reader.NullableGuid("origin_request_id"),
          (ExecutionOutcome?)reader.NullableInt("outcome"), (InputProvenance?)reader.NullableInt("input_provenance"),
          reader.NullableGuid("question_id"), reader.Long("run_ordinal"), reader.Int("request_index"),
          reader.NullableString("usage_json"), reader.NullableGuid("compaction_id"), reader.Bool("omit_assistant"));
      if (previous?.RequestId != row.RequestId) {
        FinishGroup();
        messages.Clear();
        inputMessages.Clear();
        text = null;
        providerMessage = null;
        calls.Clear();
        results.Clear();
        previous = row;
      }

      switch (row.Kind) {
        case ContextRowKind.ToolCompletion:
          messages.Add(ReadToolCompletion(row));
          break;
        case ContextRowKind.User:
          // A presented question is agent-authored data labelled with its sender, never human input.
          // User rows carry the asking session's final title in the name column.
          string? imageIds = reader.NullableString("image_ids_json");
          string? inputText = row.InputProvenance == InputProvenance.AgentQuestion
              ? AgentQuestionContext.Describe(row.Name, row.Text!) : row.Text;
          var input = new ChatMessage(ChatRole.User,
              imageIds is not null && string.IsNullOrWhiteSpace(inputText) ? null : inputText);
          messages.Add(input);
          if (imageIds is not null)
            imageInputs.Add(input, imageIds);
          inputMessages.Add(input, row.QuestionId);
          break;
        case ContextRowKind.Text:
          (text ??= new()).Append(row.Text);
          break;
        case ContextRowKind.ToolCall:
          var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(row.Json!)!;
          calls.Add(new FunctionCallContent(row.CallId!, row.Name!, arguments));
          break;
        case ContextRowKind.ToolResult:
          object result = ReadToolResult(row);
          results.Add(new FunctionResultContent(row.CallId!, result));
          break;
        case ContextRowKind.ProviderResponse:
          providerMessage = LlmProviderContext.Restore(JsonSerializer.Deserialize<LlmProviderResponse>(row.Json!)!);
          break;
      }
    }

    FinishGroup();
    reader.Close();
    if (imageInputs.Count > 0) {
      var images = new UserImageStorage(database, transaction.Connection!.DataSource + ".user-images");
      var loaded = new Dictionary<Guid, DataContent>();
      foreach (ChatMessage message in entries.Select(entry => entry.Message)) {
        if (!imageInputs.TryGetValue(message, out string? json))
          continue;
        foreach (Guid id in UserImageIds.Read(json)!) {
          if (!loaded.TryGetValue(id, out DataContent? image)) {
            image = images.Read(transaction, sessionId, id)
                ?? throw new InvalidDataException("A stored user image is missing.");
            loaded.Add(id, image);
          }
          message.Contents.Add(new DataContent(image.Data, image.MediaType));
        }
      }
    }
    if (observedTokens is { } known)
      observedTokens = ContextTokenBudget.AddSaturating(known,
          ContextTokenBudget.EstimateMessages(entries.Skip(observedThrough).Select(entry => entry.Message)));
    return new(entries.AsReadOnly(), boundary, observedTokens) {
      UsageCheckpoint = requestId is Guid current
          ? ReadUsageCheckpoint(transaction, sessionId, historyRevision, current, boundary?.Id, entries) : null
    };

    void FinishGroup() {
      if (previous is null)
        return;
      AddResponse(messages, text, calls, results, providerMessage, previous.OmitAssistant);
      Guid id = previous.RequestId;
      int first = id == firstRetained?.RequestId ? firstRetained.MessageIndexInRequest : 0;
      for (int index = first; index < messages.Count; index++) {
        ChatMessage message = messages[index];
        bool startsUserTurn = inputMessages.TryGetValue(message, out Guid? questionId);
        entries.Add(new(new(id, previous.RunOrdinal, previous.RequestIndex, index), startsUserTurn, message) {
          AgentQuestionId = questionId
        });
      }
      if (previous.UsageJson is null || previous.CompactionId != boundary?.Id)
        return;
      UsageDetails? usage = JsonSerializer.Deserialize<UsageDetails>(previous.UsageJson);
      if (usage?.InputTokenCount is not >= 0 || (!previous.OmitAssistant && usage.OutputTokenCount is not >= 0))
        return;
      observedTokens = ContextTokenBudget.AddSaturating(usage.InputTokenCount.Value,
          previous.OmitAssistant ? 0 : usage.OutputTokenCount!.Value);
      observedTokens = ContextTokenBudget.AddSaturating(observedTokens.Value,
          ContextTokenBudget.EstimateMessages(messages.Where(message => message.Role == ChatRole.Tool)));
      observedThrough = entries.Count;
    }
  }

  private ContextUsageCheckpoint? ReadUsageCheckpoint(SqliteTransaction transaction, Guid sessionId, long revision,
      Guid requestId, Guid? boundary, List<HistoryMessage> entries) {
    var checkpoint = database.QuerySingleOrDefault(transaction, $"""
      SELECT r.ordinal, q.request_index, q.usage_json, COALESCE(r.llm_selection_json, d.llm_selection_json) AS selection
      FROM execution_requests current JOIN execution_runs active ON active.run_id = current.run_id
      JOIN execution_runs r ON r.session_id = $session AND r.history_revision = $revision
      JOIN execution_requests q ON q.run_id = r.run_id
      LEFT JOIN input_decisions d ON d.session_id = r.session_id AND d.input_id = r.input_id
      WHERE current.request_id = $request AND r.ordinal >= ({StoredContextUsage.RevisionFloorSql})
          AND (r.ordinal < active.ordinal OR (r.ordinal = active.ordinal AND q.request_index < current.request_index))
          AND r.ordinal >= COALESCE((SELECT MAX(omission.ordinal) FROM execution_runs omission
              WHERE omission.session_id = $session AND omission.history_revision = $revision
                  AND omission.ordinal <= active.ordinal AND omission.continuation_omitted_request_id IS NOT NULL), 0)
          AND q.attempt = 2 AND q.outcome = {(int)ExecutionOutcome.Completed}
          AND q.compaction_id IS $boundary AND q.usage_json IS NOT NULL
      ORDER BY r.ordinal DESC, q.request_index DESC LIMIT 1;
      """, row => (Ordinal: row.Long("ordinal"), RequestIndex: row.Int("request_index"),
          Tokens: StoredContextUsage.ReadTokens(row.String("usage_json")),
          Selection: LlmSelectionJson.Read(row.NullableString("selection"))),
      ("$session", sessionId), ("$revision", revision), ("$request", requestId), ("$boundary", boundary));
    if (checkpoint.Tokens is not long tokens || checkpoint.Selection is not { } selection)
      return null;

    // The reported output includes the assistant's calls, not their results. Background
    // completions claimed as input by that request are already covered by its input count.
    int covered = entries.Count;
    while (covered > 0 && IsAfterCheckpoint(entries[covered - 1]))
      covered--;
    return new(selection, tokens, entries.Count - covered);

    bool IsAfterCheckpoint(HistoryMessage entry) => entry.Location.ExecutionOrdinal > checkpoint.Ordinal
        || (entry.Location.ExecutionOrdinal == checkpoint.Ordinal
            && (entry.Location.RequestIndexInExecution > checkpoint.RequestIndex
                || (entry.Location.RequestIndexInExecution == checkpoint.RequestIndex && entry.Message.Role == ChatRole.Tool)));
  }

  // Orders the unioned context shapes within each request.
  private enum ContextRowKind { ToolCompletion = -1, User = 0, Text = 1, ToolCall = 2, ToolResult = 3, ProviderResponse = 4 }

  // One streamed context row, including the request metadata needed when its group ends.
  private sealed record ContextRow(Guid RequestId, ContextRowKind Kind, string? Text, string? CallId,
      string? Name, string? Json, bool Background, Guid? OriginRequestId, ExecutionOutcome? Outcome,
      InputProvenance? InputProvenance, Guid? QuestionId, long RunOrdinal, int RequestIndex, string? UsageJson,
      Guid? CompactionId, bool OmitAssistant);

  private ChatMessage ReadToolCompletion(ContextRow row) =>
      new(ChatRole.User, toolResults.ReadModelCompletion(row.CallId!, row.Name!, row.Outcome!.Value, row.Json!));

  private object ReadToolResult(ContextRow row) => row.Background
      ? ToolResultCodec.BackgroundStarted(row.OriginRequestId!.Value, row.CallId!)
      : toolResults.ReadModelResult(row.Json!);

  private static void AddResponse(List<ChatMessage> messages, StringBuilder? text,
      List<AIContent> calls, List<AIContent> results, ChatMessage? providerMessage, bool omitAssistant) {
    if (!omitAssistant) {
      if (providerMessage is not null) {
        messages.Add(providerMessage);
      } else if (text is not null || calls.Count > 0) {
        var contents = new List<AIContent>();
        if (text is not null)
          contents.Add(new TextContent(text.ToString()));
        contents.AddRange(calls);
        messages.Add(new(ChatRole.Assistant, contents));
      }
    }

    if (results.Count > 0)
      messages.Add(new(ChatRole.Tool, results.ToList()));
  }
}
