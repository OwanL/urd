using Urd.Core.Concurrency;
using Urd.Core.Execution;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// Admits session inputs and reads their delivery receipts.
internal sealed class SessionInputs(SessionBase session, UrdStore store, AsyncLock sessionLock) {
  internal Task<UserMessageReceipt?> ReadUserMessageReceiptAsync(Guid submissionId, CancellationToken cancellation = default) =>
      session.ReadUnderLockAsync(() => store.UserMessages.ReadReceipt(session.Id, submissionId), cancellation);

  internal async Task<ContinuationReceipt?> ReadContinuationReceiptAsync(Guid operationId,
      CancellationToken cancellation = default) {
    if (operationId == Guid.Empty)
      throw new ArgumentException("An operation ID is required.", nameof(operationId));
    return await session.ReadUnderLockAsync(() => store.Continuations.ReadReceipt(session.Id, operationId), cancellation);
  }

  internal Task<NotSentPage> ReadNotSentInputsAsync(string? cursor = null, CancellationToken cancellation = default) =>
      session.ReadUnderLockAsync(() => store.NotSentInputs.Read(session.Id, cursor), cancellation);

  internal async Task<bool> RemovePendingInputAsync(Guid executionId, Guid submissionId) {
    if (executionId == Guid.Empty || submissionId == Guid.Empty)
      throw new ArgumentException("Execution and submission identities are required.");

    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanStartWork || session.CurrentExecution is not { } current
        || current.Identity.HistoryRevision != session.HistoryRevision
        || current.IsStopping)
      return false;

    SessionSnapshot snapshot = session.ReadCurrentSnapshot();
    if (snapshot.AdmissionFenced || snapshot.StopRequested || snapshot.UnsettledExecutionId != current.ExecutionId
        || executionId != (snapshot.QueuedInputExecutionId ?? current.ExecutionId))
      return false;

    bool removed = session.WriteToStore(() => store.Executions.RemovePendingInput(current.Identity, executionId, submissionId));
    if (removed) {
      current.Wake();
      session.PublishChange();
    }
    return removed;
  }

  internal async Task<ContinuationReceipt?> AdmitContinuationAsync(ContinuationSubmission submission) {
    ArgumentNullException.ThrowIfNull(submission);
    TaskCompletionSource? executionStart = null;
    ContinuationReceipt? receipt;
    using (await sessionLock.AcquireAsync()) {
      session.ThrowIfClosed();
      var admission = session.WriteToStore(() => store.Continuations.Start(session.Id, session.HistoryRevision, submission,
          session.CanStartWork && session.CurrentExecution is null));
      if (admission is null)
        return null;

      receipt = admission.Value.Receipt;
      if (admission.Value.New) {
        executionStart = session.StartExecutionAfterUnlock(receipt.ExecutionId, submission.LlmSelection!);
        session.PublishChange();
      }
    }

    executionStart?.SetResult();
    return receipt;
  }

  internal async Task<UserMessageReceipt> AdmitUserMessageAsync(UserMessageSubmission submission) {
    ArgumentNullException.ThrowIfNull(submission);
    submission = submission with { ImageIds = submission.ImageIds is { Count: > 0 } ids
        ? Array.AsReadOnly(ids.ToArray()) : null };
    // A new execution starts only after the lock is released.
    TaskCompletionSource? executionStart = null;
    UserMessageReceipt receipt;
    using (await sessionLock.AcquireAsync()) {
      session.ThrowIfClosed();
      (receipt, bool newlyAccepted) = session.WriteToStore(
          () => store.UserMessages.Submit(session.Id, session.HistoryRevision, submission, session.IsAcceptingWork, Guid.NewGuid(), session.CurrentExecution?.ExecutionId));
      if (receipt.Acceptance == MessageAcceptance.Accepted)
        session.MarkAcceptedInput();

      // Steering wakes the owned execution; a prompt queued behind compaction waits for settlement.
      if (newlyAccepted) {
        if (session.CurrentExecution is null)
          executionStart = session.StartExecutionAfterUnlock(receipt.ExecutionId!.Value, receipt.Submission.LlmSelection!);
        else if (session.CurrentExecution.ExecutionId == receipt.ExecutionId)
          session.CurrentExecution.Wake();
        session.PublishChange();
      }
    }

    executionStart?.SetResult();
    return receipt;
  }

  // Admits a bounded assignment into its first execution in one lock hold, so every input shares
  // the first request. Refusals throw: a bounded child owns no retries and no resumptions.
  internal async Task<Guid> AdmitAssignmentAsync(IReadOnlyList<UserMessageSubmission> submissions, Guid parentSessionId) {
    if (submissions.Count == 0)
      throw new ArgumentException("An assignment requires at least one input.", nameof(submissions));

    TaskCompletionSource? executionStart = null;
    Guid executionId;
    using (await sessionLock.AcquireAsync()) {
      UserMessageReceipt lead = session.WriteToStore(() => store.UserMessages.Submit(
          session.Id, session.HistoryRevision, submissions[0], session.IsAcceptingWork, Guid.NewGuid(),
          provenance: submissions.Count == 1 ? InputProvenance.AgentAssignment : InputProvenance.InheritedContext,
          senderSessionId: parentSessionId)).Receipt;
      if (lead.Acceptance != MessageAcceptance.Accepted)
        throw new InvalidOperationException($"The assignment was refused ({lead.RefusalReason}).");

      executionId = lead.ExecutionId!.Value;
      foreach (UserMessageSubmission steered in submissions.Skip(1)) {
        UserMessageReceipt receipt = session.WriteToStore(() => store.UserMessages.Submit(
            session.Id, session.HistoryRevision, steered, session.IsAcceptingWork, Guid.NewGuid(), executionId,
            provenance: InputProvenance.AgentAssignment, senderSessionId: parentSessionId)).Receipt;
        if (receipt.Acceptance != MessageAcceptance.Accepted)
          throw new InvalidOperationException($"The assignment was refused ({receipt.RefusalReason}).");
      }

      session.MarkAcceptedInput();
      executionStart = session.StartExecutionAfterUnlock(executionId, submissions[0].LlmSelection!);
      session.PublishChange();
    }

    executionStart?.SetResult();
    return executionId;
  }
}
