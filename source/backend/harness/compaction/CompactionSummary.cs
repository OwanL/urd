namespace Urd.Core.Compaction;

public enum CompactionReason { Manual, Threshold, Overflow }

// Only successful summaries are saved. Original history remains intact.
public sealed record CompactionSummary(
    Guid Id, string Summary, CompactionReason Reason,
    long EstimatedContextTokensBefore, long EstimatedContextTokensAfter, DateTimeOffset CreatedAt);
