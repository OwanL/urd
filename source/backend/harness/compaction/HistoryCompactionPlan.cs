using System.Text;
using System.Text.Json;

namespace Urd.Core.Compaction;

// Summarize the prefix before FirstRetainedMessageIndex; keep the remaining messages verbatim.
internal sealed record HistoryCompactionPlan(
    ModelHistory History, int FirstRetainedMessageIndex, long EstimatedContextTokensBefore,
    CompactionReason Reason, string? Focus = null) {
  internal HistoryMessage FirstRetainedMessage => History.Messages[FirstRetainedMessageIndex];
  internal IReadOnlyList<BackgroundToolCallInput> BackgroundToolCalls { get; init; } = [];

  // Exact stored inputs bypass summarization and its output limit, but still count toward context size.
  internal string AppendBackgroundToolCalls(string summary) {
    if (BackgroundToolCalls.Count == 0)
      return summary;
    var text = new StringBuilder(summary);
    text.Append("\n\n[BACKGROUND TOOL CALLS: inputs retained for result association; not new calls.]\n");
    foreach (BackgroundToolCallInput call in BackgroundToolCalls) {
      text.AppendLine(JsonSerializer.Serialize(new { request_id = call.RequestId, call_id = call.CallId, name = call.Name }));
      text.AppendLine(call.ArgumentsJson);
    }
    return text.ToString();
  }
}

internal sealed record BackgroundToolCallInput(Guid RequestId, string CallId, string Name, string ArgumentsJson);

// A generated summary is not durable until storage accepts the plan's history boundary.
internal sealed record GeneratedCompactionSummary(string Summary, long EstimatedContextTokensAfter);
