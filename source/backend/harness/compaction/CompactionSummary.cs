using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Providers;

namespace Urd.Core.Compaction;

public enum CompactionReason { Manual, Threshold, Overflow }

// Only successful summaries become model-context boundaries. Original history remains intact.
public sealed record CompactionSummary(
    Guid Id, Guid SessionId, long HistoryRevision, Guid ExecutionId,
    Guid FirstKeptRequestId, int FirstKeptMessageIndex, long FirstKeptRunOrdinal, int FirstKeptRequestIndex,
    string Summary, CompactionReason Reason, long TokensBefore, long TokensAfter,
    DateTimeOffset CreatedAt, UsageDetails? Usage = null);

public sealed record CompactionSubmission(
    Guid OperationId, long ExpectedHistoryRevision, LlmSelection LlmSelection, string? Focus = null);

public sealed record CompactionReceipt(Guid ExecutionId, ExecutionOutcome? Outcome, string? Error = null);
