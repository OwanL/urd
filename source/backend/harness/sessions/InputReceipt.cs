namespace Urd.Core.Sessions;

// First saved decision for an exact original command. Acceptance associates input
// with a run; it does not mean a model request has started or an answer has succeeded.
public sealed record InputReceipt(
    Guid SessionId,
    InputCommand Command,
    InputDecision Decision,
    InputRefusalReason? RefusalReason,
    Guid? RunId);
