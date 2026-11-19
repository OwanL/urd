using Urd.Core.Compaction;
using Urd.Core.Concurrency;
using Urd.Core.Execution;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// Admits manual compaction and reads its receipts and summaries.
internal sealed class ManualCompaction(SessionBase session, UrdStore store, AsyncLock sessionLock) {
  internal async Task<CompactionReceipt?> CompactAsync(ManualCompactionRequest submission) {
    ArgumentNullException.ThrowIfNull(submission);
    if (submission.OperationId == Guid.Empty || submission.ExpectedHistoryRevision < 0
        || submission.LlmSelection is null || submission.Focus?.Length > 8192)
      throw new ArgumentException("Compaction requires an operation ID, revision, selection and bounded focus.", nameof(submission));
    submission.LlmSelection.Validate();
    TaskCompletionSource? executionStart = null;
    CompactionReceipt? receipt;
    using (await sessionLock.AcquireAsync()) {
      var admission = session.WriteToStore(() => store.Compactions.Start(session.Id, submission,
          session.CanStartWork && session.CurrentExecution is null && submission.ExpectedHistoryRevision == session.HistoryRevision));
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
      session.ReadUnderLockAsync(() => store.Compactions.ReadReceipt(session.Id, operationId), cancellation);

  internal Task<CompactionSummary?> ReadSummaryAsync(Guid id, long revision, CancellationToken cancellation = default) =>
      session.ReadUnderLockAsync(() => store.Compactions.Read(session.Id, revision, id), cancellation);
}
