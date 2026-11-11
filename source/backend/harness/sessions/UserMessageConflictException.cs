namespace Urd.Core.Sessions;

// Thrown when a submission identity is reused with a different original submission.
public sealed class UserMessageConflictException(Guid submissionId)
    : InvalidOperationException($"Submission '{submissionId}' already belongs to a different original submission.") {
  public Guid SubmissionId { get; } = submissionId;
}
