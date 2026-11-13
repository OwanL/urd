using Urd.Core.Execution;
using Urd.Core.Providers;

namespace Urd.Core.Compaction;

public enum CompactionReason { Manual, Threshold, Overflow }

// Only successful summaries are saved. Original history remains intact.
public sealed record CompactionSummary(
    Guid Id, string Summary, CompactionReason Reason, long TokensBefore, long TokensAfter, DateTimeOffset CreatedAt);

public sealed record CompactionSubmission(
    Guid OperationId, long ExpectedHistoryRevision, LlmSelection LlmSelection, string? Focus = null);

public sealed record CompactionReceipt(Guid ExecutionId, ExecutionOutcome? Outcome, string? Error = null);
