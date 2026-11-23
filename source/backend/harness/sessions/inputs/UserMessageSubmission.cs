using Urd.Core.Providers;

namespace Urd.Core.Sessions;

// Retries must repeat the text, selection and ordered attachments. Omitted attachments mean none.
public sealed record UserMessageSubmission(Guid SubmissionId, long ExpectedHistoryRevision, string Text,
    LlmSelection? LlmSelection = null, IReadOnlyList<Guid>? ImageIds = null);

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
  Stopping = 3,
  SelectionLocked = 4
}
