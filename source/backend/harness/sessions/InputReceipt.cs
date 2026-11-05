namespace Urd.Core.Sessions;

public enum InputRefusalReason
{
    StaleRevision = 1,
    Capacity = 2,
    Stopping = 3
}

public enum InputDecision
{
    Accepted = 1,
    Refused = 2
}

/// <summary>
/// The first durable decision for the exact original command. Acceptance associates
/// input with a run; it is not evidence of a model request or successful answer.
/// </summary>
public sealed record InputReceipt(
    Guid SessionId,
    InputCommand Command,
    InputDecision Decision,
    InputRefusalReason? RefusalReason,
    Guid? RunId);
