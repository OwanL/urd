using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Storage;
using Urd.Core.Providers;

namespace Urd.Core.Sessions;

// Owns one session's state. Every read and write goes through this class's lock;
// LLM work and stop callbacks always run outside it.
public abstract class SessionBase {
  private readonly UrdStore store;
  private readonly Func<ProviderConnection, IChatClient> createChatClient;
  private readonly IReadOnlyList<AIFunction> tools;
  private readonly AsyncLock gate = new();
  private bool acceptingUserMessages = true;
  private SessionExecution? execution;
  private TaskCompletionSource<SessionExecutionResult>? settlement;
  private Task? executionTask;

  private protected SessionBase(SessionRecord record, UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      IReadOnlyList<AIFunction>? tools = null) {
    Id = record.Id;
    HistoryRevision = record.HistoryRevision;
    this.store = store;
    this.createChatClient = createChatClient;
    this.tools = tools ?? [];
  }

  public Guid Id { get; }

  internal long HistoryRevision { get; }

  public async Task<UserMessageReceipt?> GetUserMessageReceiptAsync(Guid submissionId, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.UserMessages.ReadReceipt(Id, submissionId);
  }

  public async Task<SessionSnapshot> ReadSnapshotAsync(CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    SessionSnapshot stored = store.Snapshots.ReadStored(Id);
    bool executionOwnedElsewhere = stored.UnsettledExecutionId is { } executionId && !IsCurrentExecution(executionId);
    bool fenced = !acceptingUserMessages || stored.HistoryRevision != HistoryRevision || executionOwnedElsewhere;
    bool stopping = stored.StopRequested
        || (stored.UnsettledExecutionId is { } activeExecution && IsCurrentExecution(activeExecution) && execution!.IsStopping);
    return stored with { AdmissionFenced = fenced, StopRequested = stopping };
  }

  public async Task<MessageContentPage> ReadMessageContentAsync(
      MessageContentCursor cursor, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.Content.Read(Id, cursor);
  }

  private protected async Task<UserMessageReceipt> AdmitUserMessageAsync(UserMessageSubmission submission) {
    // A new execution starts only after the lock is released.
    TaskCompletionSource? lockReleased = null;
    UserMessageReceipt receipt;
    using (await gate.AcquireAsync()) {
      (receipt, bool newlyAccepted) = WriteToStore(
          () => store.UserMessages.Admit(Id, HistoryRevision, submission, acceptingUserMessages, Guid.NewGuid(), execution?.ExecutionId));

      // Steering joins the owned execution; neither it nor a retained receipt starts another driver.
      if (newlyAccepted && execution is null) {
        lockReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StartExecution(receipt.ExecutionId!.Value, receipt.Submission.LlmSelection!, lockReleased.Task);
      }
    }

    lockReleased?.SetResult();
    return receipt;
  }

  internal async Task FenceUserMessagesAsync() {
    using var held = await gate.AcquireAsync();
    acceptingUserMessages = false;
  }

  // Called after all manager-owned sessions have fenced user messages. A failed Stop write
  // must not prevent local cancellation or let shutdown skip waiting for owned work.
  internal async Task StopExecutionAsync() {
    Task pendingExecution;
    Task pendingSettlement;
    Task stopCallbacks = Task.CompletedTask;
    Exception? stopFailure = null;
    using (await gate.AcquireAsync()) {
      pendingExecution = executionTask ?? Task.CompletedTask;
      pendingSettlement = settlement?.Task ?? Task.CompletedTask;
      if (execution is not null) {
        try {
          WriteToStore(() => store.Executions.RequestStop(Id, execution.ExecutionId, ownsExecution: true));
        } catch (Exception failure) {
          stopFailure = failure;
        }

        stopCallbacks = execution.StopAsync();
      }
    }

    Task stopSaved = stopFailure is null ? Task.CompletedTask : Task.FromException(stopFailure);
    Task drained = Task.WhenAll(stopSaved, stopCallbacks, pendingExecution, pendingSettlement);
    try {
      await drained;
    } catch (Exception failure) {
      throw new AggregateException($"Session '{Id}' could not shut down.", drained.Exception ?? failure);
    }
  }

  public async Task<StopReceipt> RequestStopAsync(Guid executionId) {
    if (executionId == Guid.Empty)
      throw new ArgumentException("An execution ID is required.", nameof(executionId));

    using var held = await gate.AcquireAsync();
    bool ownsExecution = IsCurrentExecution(executionId);
    // Signal even if the Stop write fails. The driver owns joining the retained
    // callback task before settlement; this acknowledgement never means cleanup finished.
    if (ownsExecution)
      _ = execution!.StopAsync();

    return WriteToStore(() => store.Executions.RequestStop(Id, executionId, ownsExecution));
  }

  internal async Task<SessionExecutionResult> WaitForSettlementAsync(Guid executionId, CancellationToken cancellation = default) {
    Task pendingExecution;
    Task<SessionExecutionResult> pendingSettlement;
    using (await gate.AcquireAsync(cancellation)) {
      if (!IsCurrentExecution(executionId)) {
        return store.Executions.ReadSettlement(Id, executionId)
            ?? throw new InvalidOperationException($"Execution '{executionId}' has no known settlement in this session.");
      }

      pendingExecution = executionTask!;
      pendingSettlement = settlement!.Task;
    }

    await pendingExecution.WaitAsync(cancellation);
    return await pendingSettlement.WaitAsync(cancellation);
  }

  internal async Task<LlmRequest?> ClaimNextRequestAsync(Guid executionId) {
    using var held = await gate.AcquireAsync();
    return IsCurrentExecution(executionId) && !execution!.IsStopping
        ? store.Executions.ClaimNextRequest(Id, executionId, HistoryRevision) : null;
  }

  internal async Task<bool> RecordOutputAsync(Guid executionId, LlmResponseChunk chunk) {
    using var held = await gate.AcquireAsync();
    return IsCurrentExecution(executionId) && !execution!.IsStopping
        && store.LlmResponses.Append(Id, executionId, HistoryRevision, chunk);
  }

  internal async Task<bool> CompleteRequestAsync(Guid executionId, LlmRequestResult outcome) {
    using var held = await gate.AcquireAsync();
    return IsCurrentExecution(executionId)
        && (outcome.Outcome != ExecutionOutcome.Completed || !execution!.IsStopping)
        && store.LlmResponses.CompleteRequest(Id, executionId, HistoryRevision, outcome);
  }

  internal async Task<bool> TryStartToolAsync(Guid executionId, Guid requestId, string callId) {
    using var held = await gate.AcquireAsync();
    return IsCurrentExecution(executionId) && !execution!.IsStopping
        && store.Tools.TryStart(Id, executionId, HistoryRevision, requestId, callId);
  }

  internal async Task<bool> CompleteToolAsync(Guid executionId, Guid requestId, LlmToolResult result) {
    using var held = await gate.AcquireAsync();
    return IsCurrentExecution(executionId) && store.Tools.Complete(Id, executionId, HistoryRevision, requestId, result);
  }

  // Caller holds the lock.
  private void StartExecution(Guid executionId, LlmSelection selection, Task lockReleased) {
    var newExecution = new SessionExecution(executionId, this, createChatClient, selection, tools);
    var newSettlement = new TaskCompletionSource<SessionExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    execution = newExecution;
    settlement = newSettlement;
    executionTask = RunExecutionAsync(newExecution, newSettlement, lockReleased);
  }

  private async Task RunExecutionAsync(SessionExecution execution,
      TaskCompletionSource<SessionExecutionResult> settlement, Task lockReleased) {
    try {
      await lockReleased;
      LlmRequest? request = await ClaimNextRequestAsync(execution.ExecutionId);
      if (request is null) {
        if (!execution.IsStopping)
          throw new InvalidOperationException("The execution has no initial request and was not stopped.");

        await AdvanceExecutionAsync(execution, settlement, ExecutionOutcome.Cancelled);
        return;
      }

      while (request is not null) {
        ExecutionOutcome outcome = await execution.RunRequestAsync(request);
        request = await AdvanceExecutionAsync(execution, settlement, outcome);
      }
    } catch (Exception failure) {
      // The execution's saved state is uncertain, so keep it unsettled and refuse new user messages.
      // The execution and its resources stay in place for later recovery.
      using var held = await gate.AcquireAsync();
      acceptingUserMessages = false;
      settlement.TrySetException(failure);
      _ = settlement.Task.Exception; // Observed even if nobody is waiting yet.
    }
  }

  private async Task<LlmRequest?> AdvanceExecutionAsync(SessionExecution execution,
      TaskCompletionSource<SessionExecutionResult> settlement, ExecutionOutcome outcome) {
    Task stopCallbacks;
    using (await gate.AcquireAsync()) {
      // A late Stop cannot escape its join or race the next request's claim.
      stopCallbacks = execution.StopCallbacks;
      if (stopCallbacks.IsCompletedSuccessfully)
        return ClaimOrSettle(execution, settlement, outcome);
    }

    // Stop callbacks may call back into this session, so never await them under the lock.
    await stopCallbacks;
    using (await gate.AcquireAsync()) {
      return ClaimOrSettle(execution, settlement, outcome);
    }
  }

  // Caller holds the lock. Admission cannot slip between the pending-submission check and settlement.
  private LlmRequest? ClaimOrSettle(SessionExecution execution,
      TaskCompletionSource<SessionExecutionResult> settlement, ExecutionOutcome outcome) {
    if (outcome == ExecutionOutcome.Completed && !execution.IsStopping) {
      LlmRequest? next = store.Executions.ClaimNextRequest(Id, execution.ExecutionId, HistoryRevision);
      if (next is not null)
        return next;
    }

    Settle(execution, settlement, outcome);
    return null;
  }

  // Caller holds the lock. Saves the result before releasing resources and
  // publishing it, so waiters only ever see a saved settlement.
  private void Settle(SessionExecution execution,
      TaskCompletionSource<SessionExecutionResult> settlement, ExecutionOutcome outcome) {
    SessionExecutionResult saved = store.Executions.TrySettle(Id, execution.ExecutionId, outcome)
        ?? throw new InvalidOperationException("The current execution could not be settled.");

    execution.Dispose();
    this.execution = null;
    this.settlement = null;
    settlement.TrySetResult(saved);
  }

  private bool IsCurrentExecution(Guid executionId) => execution?.ExecutionId == executionId;

  // A failed write may still have been committed, so refuse new user messages rather than guess.
  private T WriteToStore<T>(Func<T> write) {
    try {
      return write();
    } catch (SqliteException) {
      acceptingUserMessages = false;
      throw;
    }
  }
}
