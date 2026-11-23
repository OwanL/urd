using Urd.Core.Concurrency;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// Owns message edit admission, replacement and the edit shutdown join.
internal sealed class MessageEditing(SessionBase session, UrdStore store, AsyncLock sessionLock,
    ActiveExecution activeExecution) {
  private Task<MessageEditReceipt>? editTask;
  private string? editSubmissionHash;

  internal bool IsEditing => editTask is not null;

  // Caller holds the session lock.
  internal Task? PendingEdit => editTask;

  internal static async Task JoinPendingEditAsync(Task? pendingEdit) {
    if (pendingEdit is null)
      return;
    try {
      await pendingEdit;
    } catch (UserMessageConflictException) {
      // Shutdown fenced an admitted edit before replacement; no history was changed.
    }
  }

  internal Task<MessageEditReceipt?> ReadReceiptAsync(Guid operationId, CancellationToken cancellation = default) =>
      session.ReadUnderLockAsync(() => store.MessageEdits.ReadReceipt(session.Id, operationId), cancellation);

  internal async Task<MessageEditReceipt> ReplaceAsync(MessageEditSubmission submission) {
    MessageEditStorage.Validate(submission);
    if (submission.ImageIds is { } ids)
      submission = submission with { ImageIds = Array.AsReadOnly(ids.ToArray()) };
    string submissionHash = MessageEditStorage.Hash(submission);
    Task<MessageEditReceipt> work;
    TaskCompletionSource? released = null;
    using (await sessionLock.AcquireAsync()) {
      session.ThrowIfClosed();
      if (editTask is not null && editSubmissionHash == submissionHash) {
        work = editTask;
      } else {
        MessageEditReceipt? receipt = store.MessageEdits.ReadReceiptOrValidate(session.Id, submission, submissionHash);
        if (receipt is not null)
          return receipt;
        if (editTask is not null || !session.CanAcceptEdits)
          throw new UserMessageConflictException(submission.OperationId);
        released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        editSubmissionHash = submissionHash;
        work = editTask = ReplaceAfterStopAsync(submission, submissionHash, released.Task);
        session.PublishChange();
      }
    }
    released?.SetResult();
    return await work;
  }

  private async Task<MessageEditReceipt> ReplaceAfterStopAsync(MessageEditSubmission submission, string hash, Task admitted) {
    await admitted;
    TaskCompletionSource? executionStart = null;
    try {
      await activeExecution.StopAndDrainAsync();
      using var _ = await sessionLock.AcquireAsync();
      if (!session.CanAcceptEdits)
        throw new UserMessageConflictException(submission.OperationId);
      MessageEditReceipt receipt = session.WriteToStore(() => store.MessageEdits.Replace(session.Id, submission, hash));
      session.ReplaceHistoryRevision(receipt.HistoryRevision);
      executionStart = session.StartExecutionAfterUnlock(receipt.ExecutionId, submission.LlmSelection);
      return receipt;
    } finally {
      using (await sessionLock.AcquireAsync()) {
        editTask = null;
        editSubmissionHash = null;
        executionStart ??= session.StartQueuedCompaction();
        session.PublishChange();
      }
      executionStart?.SetResult();
    }
  }
}
