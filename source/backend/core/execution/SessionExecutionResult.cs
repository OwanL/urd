namespace Urd.Core.Execution;

// Saved session execution result, published only after owned work and cleanup finish.
public sealed record SessionExecutionResult(
    Guid ExecutionId,
    ExecutionOutcome Outcome);

// Shared outcome of one LLM request attempt, one tool invocation, or a whole session execution.
public enum ExecutionOutcome {
  Completed,
  Cancelled,
  Failed
}
