namespace Urd.Core.Execution;

// Outcome of one LLM request attempt or of a whole run.
public enum ExecutionOutcome
{
    Completed,
    Cancelled,
    Failed
}
