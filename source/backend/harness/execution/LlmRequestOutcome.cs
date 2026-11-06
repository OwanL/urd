namespace Urd.Core.Execution;

// Local outcome of one LLM request attempt, not live provider status.
// SessionExecution reports it to SessionBase, which still has to save it.
internal sealed record LlmRequestOutcome(
    Guid RequestId,
    ExecutionOutcome Outcome,
    string? Error = null);
