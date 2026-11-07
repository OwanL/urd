namespace Urd.Core.Sessions;

// Original decoded user message submission. Retries must repeat every member unchanged.
public sealed record UserMessageSubmission(Guid SubmissionId, long ExpectedHistoryRevision, string Text);

// First saved acceptance/refusal for an exact original submission. Acceptance associates
// the submission with an execution; it does not mean an LLM request has started or an answer has succeeded.
public sealed record UserMessageReceipt(
    Guid SessionId,
    UserMessageSubmission Submission,
    MessageAcceptance Acceptance,
    MessageRefusalReason? RefusalReason,
    Guid? ExecutionId);

// Whether a user message submission was accepted into an execution or refused.
public enum MessageAcceptance {
  Accepted = 1,
  Refused = 2
}

// Why a user message submission was refused.
public enum MessageRefusalReason {
  StaleRevision = 1,
  Capacity = 2,
  Stopping = 3
}
