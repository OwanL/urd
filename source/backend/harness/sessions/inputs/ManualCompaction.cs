using Urd.Core.Compaction;
using Urd.Core.Concurrency;
using Urd.Core.Execution;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// Admits manual compaction and reads its receipts. All queue access holds the session lock.
internal sealed class ManualCompaction(SessionBase session, UrdStore store, AsyncLock sessionLock) {
  private ManualCompactionRequest? queued;
  private (ManualCompactionRequest Request, CompactionReceipt Receipt)? discarded;

  internal Guid? QueuedId => queued?.OperationId;

  internal async Task<CompactionReceipt?> CompactAsync(ManualCompactionRequest submission) {
    ArgumentNullException.ThrowIfNull(submission);
    if (submission.OperationId == Guid.Empty || submission.ExpectedHistoryRevision < 0
        || submission.LlmSelection is null || submission.Focus?.Length > 8192)
      throw new ArgumentException("Compaction requires an operation ID, revision, selection and bounded focus.", nameof(submission));
    submission.LlmSelection.Validate();
    TaskCompletionSource? executionStart = null;
    CompactionReceipt? receipt;
    using (await sessionLock.AcquireAsync()) {
      session.ThrowIfClosed();
      if (queued?.OperationId == submission.OperationId) {
        ValidateRetry(queued, submission);
        return new(submission.OperationId, null, Queued: true);
      }
      if (discarded is { } prior && prior.Request.OperationId == submission.OperationId) {
        ValidateRetry(prior.Request, submission);
        return prior.Receipt;
      }
      // Check durable retries and conflicting operation IDs before considering a new request.
      var retained = session.WriteToStore(() => store.Compactions.Start(session.Id, submission, false));
      if (retained is not null)
        return retained.Value.Receipt;
      if (queued is not null || submission.ExpectedHistoryRevision != session.HistoryRevision
          || session.CompactionUnavailableReason() is not null)
        return null;
      if (session.CurrentExecution is not null) {
        queued = submission;
        session.PublishChange();
        return new(submission.OperationId, null, Queued: true);
      }
      var admission = session.WriteToStore(() => store.Compactions.Start(session.Id, submission, true));
      if (admission is null)
        return null;
      receipt = admission.Value.Receipt;
      if (admission.Value.New) {
        executionStart = session.StartExecutionAfterUnlock(submission.OperationId, submission.LlmSelection, submission);
        session.PublishChange();
      }
    }
    executionStart?.SetResult();
    return receipt;
  }

  internal Task<CompactionReceipt?> ReadReceiptAsync(Guid operationId, CancellationToken cancellation = default) =>
      session.ReadUnderLockAsync(() => {
        if (queued?.OperationId == operationId)
          return new CompactionReceipt(operationId, null, Queued: true);
        if (discarded is { } prior && prior.Receipt.ExecutionId == operationId)
          return prior.Receipt;
        return store.Compactions.ReadReceipt(session.Id, operationId);
      }, cancellation);

  internal void Invalidate(string reason) {
    if (queued is not { } request)
      return;
    // Keep the latest invalidation readable even though it never acquired a durable run.
    discarded = (request, new(request.OperationId, ExecutionOutcome.Cancelled, reason));
    queued = null;
  }

  private static void ValidateRetry(ManualCompactionRequest original, ManualCompactionRequest retry) {
    if (original.ExpectedHistoryRevision != retry.ExpectedHistoryRevision || original.Focus != retry.Focus
        || !LlmSelectionJson.SameOperation(original.LlmSelection, retry.LlmSelection))
      throw new UserMessageConflictException(retry.OperationId);
  }

  // Called after the previous execution and any waking question have settled.
  internal TaskCompletionSource? StartQueued() {
    if (queued is null || !session.CanStartWork || session.CurrentExecution is not null)
      return null;
    ManualCompactionRequest request = queued;
    if (request.ExpectedHistoryRevision != session.HistoryRevision) {
      Invalidate("Queued compaction skipped because history changed.");
      session.PublishChange();
      return null;
    }
    string? skipReason = session.CompactionHistoryReason(session.ReadCurrentSnapshot());
    (CompactionReceipt Receipt, bool New)? admission;
    try {
      admission = session.WriteToStore(() => store.Compactions.Start(session.Id, request, true));
    } catch (UserMessageConflictException) {
      Invalidate("Queued compaction could not start because its operation ID was already used.");
      session.PublishChange();
      return null;
    }
    if (admission is null)
      return null;
    queued = null;
    TaskCompletionSource? start = admission.Value.New
        ? session.StartExecutionAfterUnlock(request.OperationId, request.LlmSelection, request, compactionSkipReason: skipReason) : null;
    session.PublishChange();
    return start;
  }
}
