using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// Owns one session's state. Every read and write goes through this class's lock;
// model work and stop callbacks always run outside it.
public abstract class SessionBase {
  private readonly SessionStore store;
  private readonly IChatClient chatClient;
  private readonly IReadOnlyList<AIFunction> tools;
  private readonly AsyncLock gate = new();
  private bool acceptingInput = true;
  private SessionExecution? execution;
  private TaskCompletionSource<ExecutionResult>? settlement;
  private Task? executionTask;

  private protected SessionBase(SessionRecord record, SessionStore store, IChatClient chatClient,
      IReadOnlyList<AIFunction>? tools = null) {
    Id = record.Id;
    HistoryRevision = record.HistoryRevision;
    this.store = store;
    this.chatClient = chatClient;
    this.tools = tools ?? [];
  }

  public Guid Id { get; }

  internal long HistoryRevision { get; }

  public async Task<InputReceipt?> GetInputReceiptAsync(Guid inputId, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.Inputs.ReadReceipt(Id, inputId);
  }

  public async Task<SessionSnapshot> ReadSnapshotAsync(CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    StoredSessionSnapshot stored = store.Snapshots.Read(Id);
    bool runOwnedElsewhere = stored.UnsettledRunId is { } runId && !IsCurrentRun(runId);
    bool fenced = !acceptingInput || stored.HistoryRevision != HistoryRevision || runOwnedElsewhere;
    bool stopping = stored.StopRequested
        || (stored.UnsettledRunId is { } activeRun && IsCurrentRun(activeRun) && execution!.IsStopping);
    return new(Id, stored.HistoryRevision, fenced, stored.UnsettledRunId, stopping,
        stored.LastSettlement, stored.Messages, stored.HasOlderMessages);
  }

  public async Task<MessageContentPage> ReadMessageContentAsync(
      MessageContentCursor cursor, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.Content.Read(Id, cursor);
  }

  private protected async Task<InputReceipt> AdmitInputAsync(InputCommand command) {
    // A new run starts only after the lock is released.
    TaskCompletionSource? lockReleased = null;
    InputReceipt receipt;
    using (await gate.AcquireAsync()) {
      (receipt, bool newlyAccepted) = WriteToStore(
          () => store.Inputs.Admit(Id, HistoryRevision, command, acceptingInput, Guid.NewGuid(), execution?.RunId));

      // Steering joins the owned run; neither it nor a retained receipt starts another driver.
      if (newlyAccepted && execution is null) {
        lockReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StartExecution(receipt.RunId!.Value, lockReleased.Task);
      }
    }

    lockReleased?.SetResult();
    return receipt;
  }

  internal async Task FenceInputAsync() {
    using var held = await gate.AcquireAsync();
    acceptingInput = false;
  }

  // Called after all manager-owned sessions have fenced input. A failed Stop write
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
          WriteToStore(() => store.Runs.RequestStop(Id, execution.RunId, ownsRun: true));
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

  public async Task<StopReceipt> RequestStopAsync(Guid runId) {
    if (runId == Guid.Empty)
      throw new ArgumentException("A run ID is required.", nameof(runId));

    using var held = await gate.AcquireAsync();
    bool ownsRun = IsCurrentRun(runId);
    // Signal even if the Stop write fails. The driver owns joining the retained
    // callback task before settlement; this acknowledgement never means cleanup finished.
    if (ownsRun)
      _ = execution!.StopAsync();

    return WriteToStore(() => store.Runs.RequestStop(Id, runId, ownsRun));
  }

  internal async Task<ExecutionResult> WaitForSettlementAsync(Guid runId, CancellationToken cancellation = default) {
    Task pendingExecution;
    Task<ExecutionResult> pendingSettlement;
    using (await gate.AcquireAsync(cancellation)) {
      if (!IsCurrentRun(runId)) {
        return store.Runs.ReadSettlement(Id, runId)
            ?? throw new InvalidOperationException($"Run '{runId}' has no known settlement in this session.");
      }

      pendingExecution = executionTask!;
      pendingSettlement = settlement!.Task;
    }

    await pendingExecution.WaitAsync(cancellation);
    return await pendingSettlement.WaitAsync(cancellation);
  }

  internal async Task<LlmRequest?> ClaimNextRequestAsync(Guid runId) {
    using var held = await gate.AcquireAsync();
    return IsCurrentRun(runId) && !execution!.IsStopping
        ? store.Runs.ClaimNextRequest(Id, runId, HistoryRevision) : null;
  }

  internal async Task<bool> RecordOutputAsync(Guid runId, LlmResponseChunk chunk) {
    using var held = await gate.AcquireAsync();
    return IsCurrentRun(runId) && !execution!.IsStopping
        && store.Output.Append(Id, runId, HistoryRevision, chunk);
  }

  internal async Task<bool> CompleteRequestAsync(Guid runId, LlmRequestOutcome outcome) {
    using var held = await gate.AcquireAsync();
    return IsCurrentRun(runId)
        && (outcome.Outcome != ExecutionOutcome.Completed || !execution!.IsStopping)
        && store.Output.CompleteRequest(Id, runId, HistoryRevision, outcome);
  }

  internal async Task<bool> TryStartToolAsync(Guid runId, Guid requestId, string callId) {
    using var held = await gate.AcquireAsync();
    return IsCurrentRun(runId) && !execution!.IsStopping
        && store.Tools.TryStart(Id, runId, HistoryRevision, requestId, callId);
  }

  internal async Task<bool> CompleteToolAsync(Guid runId, Guid requestId, LlmToolResult result) {
    using var held = await gate.AcquireAsync();
    return IsCurrentRun(runId) && store.Tools.Complete(Id, runId, HistoryRevision, requestId, result);
  }

  // Caller holds the lock.
  private void StartExecution(Guid runId, Task lockReleased) {
    var newExecution = new SessionExecution(runId, this, chatClient, tools);
    var newSettlement = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    execution = newExecution;
    settlement = newSettlement;
    executionTask = RunExecutionAsync(newExecution, newSettlement, lockReleased);
  }

  private async Task RunExecutionAsync(SessionExecution execution,
      TaskCompletionSource<ExecutionResult> settlement, Task lockReleased) {
    try {
      await lockReleased;
      LlmRequest? request = await ClaimNextRequestAsync(execution.RunId);
      if (request is null) {
        if (!execution.IsStopping)
          throw new InvalidOperationException("The run has no initial request and was not stopped.");

        await AdvanceExecutionAsync(execution, settlement, ExecutionOutcome.Cancelled);
        return;
      }

      while (request is not null) {
        ExecutionOutcome outcome = await execution.RunRequestAsync(request);
        request = await AdvanceExecutionAsync(execution, settlement, outcome);
      }
    } catch (Exception failure) {
      // The run's saved state is uncertain, so keep it unsettled and refuse new input.
      // The execution and its resources stay in place for later recovery.
      using var held = await gate.AcquireAsync();
      acceptingInput = false;
      settlement.TrySetException(failure);
      _ = settlement.Task.Exception; // Observed even if nobody is waiting yet.
    }
  }

  private async Task<LlmRequest?> AdvanceExecutionAsync(SessionExecution execution,
      TaskCompletionSource<ExecutionResult> settlement, ExecutionOutcome outcome) {
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

  // Caller holds the lock. Admission cannot slip between the pending-input check and settlement.
  private LlmRequest? ClaimOrSettle(SessionExecution execution,
      TaskCompletionSource<ExecutionResult> settlement, ExecutionOutcome outcome) {
    if (outcome == ExecutionOutcome.Completed && !execution.IsStopping) {
      LlmRequest? next = store.Runs.ClaimNextRequest(Id, execution.RunId, HistoryRevision);
      if (next is not null)
        return next;
    }

    Settle(execution, settlement, outcome);
    return null;
  }

  // Caller holds the lock. Saves the result before releasing resources and
  // publishing it, so waiters only ever see a saved settlement.
  private void Settle(SessionExecution execution,
      TaskCompletionSource<ExecutionResult> settlement, ExecutionOutcome outcome) {
    ExecutionResult saved = store.Runs.TrySettle(Id, execution.RunId, outcome)
        ?? throw new InvalidOperationException("The current run could not be settled.");

    execution.Dispose();
    this.execution = null;
    this.settlement = null;
    settlement.TrySetResult(saved);
  }

  private bool IsCurrentRun(Guid runId) => execution?.RunId == runId;

  // A failed write may still have been committed, so refuse new input rather than guess.
  private T WriteToStore<T>(Func<T> write) {
    try {
      return write();
    } catch (SqliteException) {
      acceptingInput = false;
      throw;
    }
  }
}
