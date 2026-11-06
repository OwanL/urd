namespace Urd.Core.Execution;

// Saved run result, published only after owned work and cleanup finish.
public sealed record ExecutionResult(
    Guid RunId,
    ExecutionOutcome Outcome);
