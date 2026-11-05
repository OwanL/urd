using Microsoft.Extensions.AI;

namespace Urd.Core.Execution;

internal enum ExecutionOutcome
{
    Completed,
    Cancelled,
    Failed
}

internal sealed record RunSettlement(Guid RunId, ExecutionOutcome Outcome);
internal sealed record RequestPlan(Guid RequestId, IReadOnlyList<ChatMessage> Messages);
internal sealed record OutputBatch(Guid RequestId, Guid OutputId, long BatchIndex, string Text, bool IsFinal);
internal sealed record RequestCompletion(Guid RequestId, ExecutionOutcome Outcome, string? Error = null);
