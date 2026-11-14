namespace Urd.Core.Compaction;

// Summarize the prefix before FirstRetainedMessageIndex; keep the remaining messages verbatim.
internal sealed record HistoryCompactionPlan(
    ModelHistory History, int FirstRetainedMessageIndex, long EstimatedContextTokensBefore,
    CompactionReason Reason, string? Focus = null) {
  internal HistoryMessage FirstRetainedMessage => History.Messages[FirstRetainedMessageIndex];
}

// A generated summary is not durable until storage accepts the plan's history boundary.
internal sealed record GeneratedCompactionSummary(string Summary, long EstimatedContextTokensAfter);
