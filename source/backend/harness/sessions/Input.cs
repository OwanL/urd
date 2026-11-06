namespace Urd.Core.Sessions;

// Original decoded user input. Retries must repeat every member unchanged.
public sealed record InputCommand(Guid InputId, long ExpectedHistoryRevision, string Text);

// First saved decision for an exact original command. Acceptance associates input
// with a run; it does not mean a model request has started or an answer has succeeded.
public sealed record InputReceipt(
    Guid SessionId,
    InputCommand Command,
    InputDecision Decision,
    InputRefusalReason? RefusalReason,
    Guid? RunId);

// Whether submitted input was accepted into a run or refused.
public enum InputDecision {
    Accepted = 1,
    Refused = 2
}

// Why submitted input was refused.
public enum InputRefusalReason {
    StaleRevision = 1,
    Capacity = 2,
    Stopping = 3
}
