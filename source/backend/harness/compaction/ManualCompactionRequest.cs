using Urd.Core.Execution;
using Urd.Core.Providers;

namespace Urd.Core.Compaction;

// Starts an idle session's compaction execution; OperationId makes exact retries idempotent.
public sealed record ManualCompactionRequest(
    Guid OperationId, long ExpectedHistoryRevision, LlmSelection LlmSelection, string? Focus = null);

// A null Outcome means the accepted execution has not settled, not that a summary was saved.
public sealed record CompactionReceipt(Guid ExecutionId, ExecutionOutcome? Outcome, string? Error = null, bool Queued = false);
