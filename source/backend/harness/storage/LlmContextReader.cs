using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Providers;

namespace Urd.Core.Storage;

// Execution owns context eligibility: never-attempted history is excluded, while the
// current claim is included to prepare its send. Attempted failures stay eligible.
internal sealed class LlmContextReader(Database database, ToolResultCodec toolResults) {
  internal ReadOnlyCollection<ChatMessage> Read(SqliteTransaction transaction, Guid sessionId,
      Guid executionId, long historyRevision, Guid requestId) =>
      ReadContext(transaction, sessionId, historyRevision, executionId, requestId).ReadMessages().ToList().AsReadOnly();

  internal CompactionContext ReadContext(SqliteTransaction transaction, Guid sessionId,
      long historyRevision, Guid? executionId = null, Guid? requestId = null) {
    CompactionSummary? boundary = CompactionStorage.ReadLatest(database, transaction, sessionId, historyRevision);
    using SqliteCommand read = database.Command(transaction, """
      WITH requests AS (
          SELECT r.ordinal AS run_ordinal, q.request_index, q.request_id, q.usage_json, q.compaction_id FROM execution_runs r
          JOIN execution_requests q ON q.run_id = r.run_id
          WHERE r.session_id = $session AND r.history_revision = $revision
              AND ($run IS NULL OR r.ordinal <= (SELECT ordinal FROM execution_runs WHERE run_id = $run))
              AND (q.attempt = 2 OR q.request_id = $request)
              AND ($run IS NULL OR r.run_id <> $run OR q.request_index <=
                  (SELECT request_index FROM execution_requests WHERE request_id = $request))
              AND (r.ordinal > $cutRun OR (r.ordinal = $cutRun AND q.request_index >= $cutRequest))
      ), contents AS (
          SELECT q.run_ordinal, q.request_index, q.request_id, 0 AS kind, i.ordinal AS content_index,
              d.original_text_utf8 AS text_utf8, NULL AS call_id, NULL AS name, NULL AS json,
              NULL AS background, NULL AS origin_request_id, NULL AS outcome
          FROM requests q JOIN execution_inputs i ON i.request_id = q.request_id
          JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, 1, o.chunk_index, o.text_utf8,
              NULL, NULL, NULL, NULL, NULL, NULL
          FROM requests q JOIN execution_output o ON o.request_id = q.request_id
          WHERE NOT EXISTS (SELECT 1 FROM execution_provider_context c WHERE c.request_id = q.request_id)
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, 2, t.call_index, NULL,
              t.call_id, t.name, t.arguments_json, NULL, NULL, NULL
          FROM requests q JOIN execution_tools t ON t.request_id = q.request_id
          WHERE NOT EXISTS (SELECT 1 FROM execution_provider_context c WHERE c.request_id = q.request_id)
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, 3, t.call_index, NULL,
              t.call_id, t.name, t.result_json, t.background, t.request_id, t.outcome
          FROM requests q JOIN execution_tools t ON t.request_id = q.request_id
          WHERE t.result_json IS NOT NULL OR (t.background = 1 AND t.started = 1)
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, 4, 0, NULL, NULL, NULL,
              c.response_json, NULL, NULL, NULL
          FROM requests q JOIN execution_provider_context c ON c.request_id = q.request_id
          UNION ALL
          SELECT q.run_ordinal, q.request_index, q.request_id, -1, c.ordinal, NULL,
              t.call_id, t.name, t.result_json, NULL, c.request_id, t.outcome
          FROM requests q JOIN execution_tool_completions c ON c.claimed_request_id = q.request_id
          JOIN execution_tools t ON t.request_id = c.request_id AND t.call_id = c.call_id
          WHERE c.session_id = $session AND c.history_revision = $revision
      ) SELECT c.request_id, c.kind, c.text_utf8, c.call_id, c.name, c.json, c.background,
          c.origin_request_id, c.outcome, c.run_ordinal, c.request_index, q.usage_json, q.compaction_id
      FROM contents c JOIN requests q ON q.request_id = c.request_id
      ORDER BY c.run_ordinal, c.request_index, c.kind, c.content_index;
      """,
        ("$session", sessionId), ("$run", executionId), ("$revision", historyRevision), ("$request", requestId),
        ("$cutRun", boundary?.FirstKeptRunOrdinal ?? -1), ("$cutRequest", boundary?.FirstKeptRequestIndex ?? 0));
    using SqliteDataReader reader = read.ExecuteReader();
    var messages = new List<ChatMessage>();
    var entries = new List<ContextEntry>();
    var humanMessages = new HashSet<ChatMessage>();
    string? previousRequest = null;
    long runOrdinal = 0;
    int requestIndex = 0;
    long? observedTokens = null;
    int observedThrough = 0;
    string? usageJson = null;
    Guid? usageBoundary = null;
    StringBuilder? text = null;
    ChatMessage? providerMessage = null;
    var calls = new List<AIContent>();
    var results = new List<AIContent>();
    while (reader.Read()) {
      string currentRequest = reader.GetString(0);
      if (previousRequest != currentRequest) {
        FinishGroup();
        messages.Clear();
        humanMessages.Clear();
        text = null;
        providerMessage = null;
        calls.Clear();
        results.Clear();
        previousRequest = currentRequest;
        runOrdinal = reader.GetInt64(9);
        requestIndex = reader.GetInt32(10);
        usageJson = reader.IsDBNull(11) ? null : reader.GetString(11);
        usageBoundary = reader.IsDBNull(12) ? null : reader.GetGuid(12);
      }

      switch (reader.GetInt32(1)) {
        case -1:
          messages.Add(new(ChatRole.User, toolResults.ReadModelCompletion(
              Guid.Parse(reader.GetString(7)), reader.GetString(3), reader.GetString(4),
              (ExecutionOutcome)reader.GetInt32(8), reader.GetString(5))));
          break;
        case 0:
          var human = new ChatMessage(ChatRole.User, StorageText.StrictUtf8.GetString((byte[])reader.GetValue(2)));
          messages.Add(human);
          humanMessages.Add(human);
          break;
        case 1:
          (text ??= new()).Append(StorageText.StrictUtf8.GetString((byte[])reader.GetValue(2)));
          break;
        case 2:
          var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(5))!;
          calls.Add(new FunctionCallContent(reader.GetString(3), reader.GetString(4), arguments));
          break;
        case 3:
          object result = reader.GetInt32(6) == 1
              ? ToolResultCodec.BackgroundStarted(Guid.Parse(reader.GetString(7)), reader.GetString(3))
              : toolResults.ReadModelResult(reader.GetString(5));
          results.Add(new FunctionResultContent(reader.GetString(3), result));
          break;
        case 4:
          providerMessage = LlmProviderContext.Restore(
              JsonSerializer.Deserialize<LlmProviderResponse>(reader.GetString(5))!);
          break;
      }
    }

    FinishGroup();
    if (observedTokens is { } known)
      observedTokens = ContextBudget.Add(known, ContextBudget.Estimate(entries.Skip(observedThrough).Select(entry => entry.Message)));
    return new(entries.AsReadOnly(), boundary, observedTokens);

    void FinishGroup() {
      if (previousRequest is null)
        return;
      AddResponse(messages, text, calls, results, providerMessage);
      Guid id = Guid.Parse(previousRequest);
      int first = id == boundary?.FirstKeptRequestId ? boundary.FirstKeptMessageIndex : 0;
      for (int index = first; index < messages.Count; index++)
        entries.Add(new(id, runOrdinal, requestIndex, index, humanMessages.Contains(messages[index]), messages[index]));
      if (usageJson is null || usageBoundary != boundary?.Id)
        return;
      UsageDetails? usage = JsonSerializer.Deserialize<UsageDetails>(usageJson);
      if (usage?.InputTokenCount is not >= 0 || usage.OutputTokenCount is not >= 0)
        return;
      observedTokens = ContextBudget.Add(usage.InputTokenCount.Value, usage.OutputTokenCount.Value);
      observedTokens = ContextBudget.Add(observedTokens.Value,
          ContextBudget.Estimate(messages.Where(message => message.Role == ChatRole.Tool)));
      observedThrough = entries.Count;
    }
  }

  private static void AddResponse(List<ChatMessage> messages, StringBuilder? text,
      List<AIContent> calls, List<AIContent> results, ChatMessage? providerMessage) {
    if (providerMessage is not null) {
      messages.Add(providerMessage);
    } else if (text is not null || calls.Count > 0) {
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
