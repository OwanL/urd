using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Storage;
using Urd.Core.Providers;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Owns session identity, admission and execution lifetime. Collaborators share its gate;
// transcript source assembly, LLM work and cleanup joins run outside it.
public abstract class SessionBase {
  private readonly UrdStore store;
  private readonly Func<ProviderConnection, IChatClient> createChatClient;
  private readonly ToolCatalog tools;
  private readonly StartingContextManager startingContextManager;
  private readonly ChatProcesses? processes;
  private readonly PendingToolCalls pending;
  private readonly AsyncLock gate = new();
  private readonly SessionNotifications notifications;
  private readonly SessionStatus status;
  private bool acceptingUserMessages = true;
  private bool needsAttention;
  private SessionExecutionDriver? execution;
  // Retain the driver's task after settlement clears the active execution, so shutdown joins its tail.
  private Task? executionTask;

  private protected SessionBase(SessionRecord record, SessionKind kind, UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      ToolCatalog? tools = null, ChatProcesses? processes = null, PendingToolCalls? pending = null) {
    Id = record.Id;
    Kind = kind;
    notifications = new SessionNotifications(Id);
    status = new SessionStatus(record.HistoryRevision);
    Transcript = new SessionTranscript(Id, store, gate);
    Recorder = new SessionExecutionRecorder(this, store, gate);
    HistoryRevision = record.HistoryRevision;
    WorkingDirectory = record.WorkingDirectory;
    this.store = store;
    this.createChatClient = createChatClient;
    this.tools = tools ?? new ToolCatalog([]);
    startingContextManager = new(this.tools);
    this.processes = processes;
    this.pending = pending ?? new PendingToolCalls();
  }

  public Guid Id { get; }
  public SessionTranscript Transcript { get; }
  public string? WorkingDirectory { get; }

  internal SessionKind Kind { get; }
  internal SessionExecutionRecorder Recorder { get; }
  internal long HistoryRevision { get; }

  // Restores the delivered starting view; opening a session alone never composes it.
  internal StartingContext? ReadStartingContext() => store.StartingContexts.Read(Id);

  public async Task<CompactionReceipt?> CompactAsync(CompactionSubmission submission) {
    ArgumentNullException.ThrowIfNull(submission);
    if (submission.OperationId == Guid.Empty || submission.ExpectedHistoryRevision < 0
        || submission.LlmSelection is null || submission.Focus?.Length > 8192)
      throw new ArgumentException("Compaction requires an operation ID, revision, selection and bounded focus.", nameof(submission));
    submission.LlmSelection.Validate();
    TaskCompletionSource? released = null;
    CompactionReceipt? receipt;
    using (await gate.AcquireAsync()) {
      var admission = WriteToStore(() => store.Compactions.Admit(Id, submission,
          acceptingUserMessages && !needsAttention && submission.ExpectedHistoryRevision == HistoryRevision));
      if (admission is null)
        return null;
      receipt = admission.Value.Receipt;
      if (admission.Value.New) {
        released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StartExecution(submission.OperationId, submission.LlmSelection, released.Task, submission);
        PublishChange();
      }
    }
    released?.SetResult();
    return receipt;
  }

  public async Task<CompactionReceipt?> ReadCompactionReceiptAsync(Guid operationId, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.Compactions.ReadReceipt(Id, operationId);
  }

  public async Task<CompactionSummary?> ReadCompactionAsync(Guid id, long revision, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.Compactions.Read(Id, revision, id);
  }

  internal async Task<CompactionContext> ReadCompactionContextAsync(Guid executionId, Guid? requestId) {
    using var held = await gate.AcquireAsync();
    if (!CanRecordExecution(executionId))
      throw new OperationCanceledException("The execution no longer accepts compaction.");
    return store.Compactions.ReadContext(Id, HistoryRevision, requestId is null ? null : executionId, requestId);
  }

  internal async Task<bool> SaveCompactionAsync(Guid executionId, CompactionPlan plan, CompactionResult result) {
    using var held = await gate.AcquireAsync();
    if (!CanRecordExecution(executionId))
      return false;
    bool saved = WriteToStore(() => store.Compactions.Save(Id, executionId, HistoryRevision, plan, result)) is not null;
    if (saved)
      PublishChange();
    return saved;
  }

  internal async Task SetCompactingAsync(Guid executionId, bool compacting) {
    using var held = await gate.AcquireAsync();
    if (IsCurrentExecution(executionId)) {
      execution!.SetCompacting(compacting);
      PublishChange();
    }
  }

  internal async Task RecordCompactionErrorAsync(Guid executionId, string? error) {
    using var held = await gate.AcquireAsync();
    if (CanRecordExecution(executionId, allowStopping: true))
      WriteToStore(() => { store.Compactions.RecordError(Id, executionId, error); return true; });
  }

  internal async Task<bool> CanRetryOverflowAsync(Guid executionId, Guid requestId) {
    using var held = await gate.AcquireAsync();
    return CanRecordExecution(executionId) && store.Executions.CanRetryOverflow(Id, executionId, requestId);
  }

  // Manager shutdown joins executions before terminating deliberately retained processes.
  internal Task KillProcessesAsync() => OperatingSystem.IsWindows() && processes is not null
      ? processes.KillAllAsync() : Task.CompletedTask;

  public async Task<UserMessageReceipt?> GetUserMessageReceiptAsync(Guid submissionId, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.UserMessages.ReadReceipt(Id, submissionId);
  }

  public async Task<NotSentPage> ReadNotSentInputsAsync(string? cursor = null, CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return store.NotSentInputs.Read(Id, cursor);
  }

  public async Task<SessionSnapshot> ReadSnapshotAsync(CancellationToken cancellation = default) {
    using var held = await gate.AcquireAsync(cancellation);
    return status.ApplyLiveState(store.Snapshots.ReadStored(Id), acceptingUserMessages, execution)
        with { IsCompacting = execution?.IsCompacting == true };
  }

  public async Task<IDisposable> SubscribeAsync(Action<SessionChange> observer, bool watched,
      CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(observer);
    using var held = await gate.AcquireAsync(cancellation);
    SessionSnapshot stored = store.Snapshots.ReadStored(Id);
    status.Observe(stored, execution);
    return notifications.Subscribe(observer, watched, status.ReadStored(stored, acceptingUserMessages, needsAttention, execution));
  }

  private protected async Task<UserMessageReceipt> AdmitUserMessageAsync(UserMessageSubmission submission) {
    // A new execution starts only after the lock is released.
    TaskCompletionSource? lockReleased = null;
    UserMessageReceipt receipt;
    using (await gate.AcquireAsync()) {
      (receipt, bool newlyAccepted) = WriteToStore(
          () => store.UserMessages.Admit(Id, HistoryRevision, submission, acceptingUserMessages, Guid.NewGuid(), execution?.ExecutionId));

      // Steering joins the owned execution; neither it nor a retained receipt starts another driver.
      if (newlyAccepted) {
        if (execution is null) {
          lockReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
          StartExecution(receipt.ExecutionId!.Value, receipt.Submission.LlmSelection!, lockReleased.Task);
        }
        execution!.Wake();
        PublishChange();
      }
    }

    lockReleased?.SetResult();
    return receipt;
  }

  internal async Task FenceUserMessagesAsync() {
    using var held = await gate.AcquireAsync();
    if (acceptingUserMessages) {
      acceptingUserMessages = false;
      PublishChange();
    }
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
      pendingSettlement = execution?.Settlement ?? Task.CompletedTask;
      if (execution is not null) {
        try {
          StopReceipt stop = WriteToStore(
              () => store.Executions.RequestStop(Id, execution.ExecutionId, ownsExecution: true));
          if (stop.Status == StopStatus.Requested)
            PublishChange();
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

    StopReceipt receipt = WriteToStore(() => store.Executions.RequestStop(Id, executionId, ownsExecution));
    if (receipt.Status == StopStatus.Requested)
      PublishChange();
    return receipt;
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
      pendingSettlement = execution!.Settlement;
    }

    await pendingExecution.WaitAsync(cancellation);
    return await pendingSettlement.WaitAsync(cancellation);
  }

  // Caller holds the session gate; the driver publishes after updating its waiting state.
  internal LlmRequest? ClaimNextRequest(Guid executionId) => IsCurrentExecution(executionId) && !execution!.IsStopping
      ? store.Executions.ClaimNextRequest(Id, executionId, HistoryRevision) : null;

  // Caller holds the session gate. Results may finish during Stop, but no new work may start.
  internal bool CanRecordExecution(Guid executionId, bool allowStopping = false) =>
      IsCurrentExecution(executionId) && (allowStopping || !execution!.IsStopping);

  // Completion and cancellation-failure notifications never start another driver or join themselves.
  internal async Task BackgroundToolCompletedAsync(Guid executionId, Exception? failure = null) {
    using var held = await gate.AcquireAsync();
    if (IsCurrentExecution(executionId))
      execution!.BackgroundToolCompleted(failure);
  }

  // Caller holds the lock. The driver starts only after admission releases it.
  private void StartExecution(Guid executionId, LlmSelection selection, Task lockReleased, CompactionSubmission? compaction = null) {
    var work = new SessionExecution(executionId, this, createChatClient, selection,
        startingContextManager, tools.For(selection.Family), pending, compaction);
    execution = new SessionExecutionDriver(this, gate, work);
    executionTask = execution.RunAsync(lockReleased);
  }

  // Caller holds the session gate. Save before releasing resources and publishing;
  // only then may the driver complete its settlement task.
  internal SessionExecutionResult SettleExecution(SessionExecutionDriver execution, ExecutionOutcome outcome) {
    SessionExecutionResult saved = store.Executions.TrySettle(Id, execution.ExecutionId, outcome)
        ?? throw new InvalidOperationException("The current execution could not be settled.");

    execution.Release();
    this.execution = null;
    status.Settled(saved);
    PublishChange();
    return saved;
  }

  private bool IsCurrentExecution(Guid executionId) => execution?.ExecutionId == executionId;

  // Caller holds the session gate.
  internal void PublishChange() => notifications.Publish(status.ReadCurrent(acceptingUserMessages, needsAttention, execution));

  // Caller holds the session gate.
  internal void MarkNeedsAttention() {
    if (needsAttention && !acceptingUserMessages)
      return;

    needsAttention = true;
    acceptingUserMessages = false;
    PublishChange();
  }

  // A failed write may still have been committed, so refuse new user messages rather than guess.
  internal T WriteToStore<T>(Func<T> write) {
    try {
      return write();
    } catch (SqliteException) {
      MarkNeedsAttention();
      throw;
    }
  }
}
