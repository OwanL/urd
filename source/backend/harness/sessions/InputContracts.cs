namespace Urd.Core.Sessions;

// Original decoded input. Retries must preserve every member unchanged.
public sealed record InputCommand(Guid InputId, long ExpectedHistoryRevision, string Text);

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

// First saved decision for the exact original command. Acceptance associates input
// with a run; it does not mean a model request has started or an answer has succeeded.
public sealed record InputReceipt(
    Guid SessionId,
    InputCommand Command,
    InputDecision Decision,
    InputRefusalReason? RefusalReason,
    Guid? RunId);
