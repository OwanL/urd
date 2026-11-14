using Urd.Core.Providers;

namespace Urd.Core.Sessions;

// Immutable command to continue the current model context without adding user text.
public sealed record ContinuationSubmission(
    Guid OperationId, long ExpectedHistoryRevision, Guid ExpectedLastExecutionId, LlmSelection? LlmSelection);

// Durable acknowledgement for one input-less continuation execution.
public sealed record ContinuationReceipt(Guid SessionId, Guid OperationId, Guid ExecutionId);

internal enum ExecutionRunKind {
  UserInput,
  Compaction,
  Continuation
}
