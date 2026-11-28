using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Concurrency;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Storage;
using Urd.Core.Providers;
using Urd.Core.Preferences;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Owns session identity, admission and execution lifetime. Collaborators share its session lock;
// transcript source assembly, LLM work and cleanup joins run outside it.
public abstract partial class SessionBase {
  private readonly UrdStore store;
  private readonly PreferenceManager preferences;
  private readonly ModelSelector? modelSelector;
  private readonly SessionInputs inputs;
  private readonly ManualCompaction manualCompaction;
  private readonly MessageEditing messageEditing;
  private readonly ChatProcesses? processes;
  private readonly ActiveExecution activeExecution;
  private readonly AsyncLock sessionLock = new();
  private readonly SessionNotifications notifications;
  private readonly SessionStatus status;
  private readonly SessionManager? manager;
  private bool completionPending;
  private bool acceptingUserMessages = true;
  private volatile bool closed;
  private TaskCompletionSource? closeCleanup;
  private bool needsAttention;
  private bool? hasAcceptedInput;

  private protected SessionBase(SessionRecord record, SessionKind kind, UrdStore store, Func<ProviderConnection, LlmApi?, IChatClient> createChatClient,
      ToolCatalog? tools = null, ChatProcesses? processes = null, PendingToolCalls? pending = null,
      PreferenceManager? preferences = null, ModelSelector? modelSelector = null, ISubagentRunner? subagents = null,
      int subagentDepth = 0) {
    Id = record.Id;
    Kind = kind;
    SubagentDepth = subagentDepth;
    notifications = new SessionNotifications(Id);
    status = new SessionStatus(record.HistoryRevision);
    Transcript = new SessionTranscript(Id, store, sessionLock);
    HistoryRevision = record.HistoryRevision;
    snoozedUntil = record.SnoozedUntil;
    WorkingDirectory = record.WorkingDirectory;
    this.store = store;
    this.preferences = preferences ?? new PreferenceManager(store);
    this.modelSelector = modelSelector;
    manager = subagents as SessionManager;
    inputs = new SessionInputs(this, store, sessionLock);
    manualCompaction = new ManualCompaction(this, store, sessionLock);
    Questions = new QuestionDelivery(this, store, sessionLock, manager?.Questions);
    activeExecution = new ActiveExecution(this, store, sessionLock, createChatClient,
        tools ?? new ToolCatalog([]), pending ?? new PendingToolCalls(), subagents);
    messageEditing = new MessageEditing(this, store, sessionLock, activeExecution);
    this.processes = processes;
  }

  private protected async Task<UserImage> SaveImageAsync(Guid imageId, byte[] data, CancellationToken cancellation) {
    using var _ = await sessionLock.AcquireAsync(cancellation);
    ThrowIfClosed();
    return await store.UserImages.SaveAsync(Id, imageId, data, cancellation);
  }

  private protected async Task<DataContent?> ReadImageAsync(Guid imageId, CancellationToken cancellation) {
    using var _ = await sessionLock.AcquireAsync(cancellation);
    ThrowIfClosed();
    return store.UserImages.Read(Id, imageId);
  }

  public Guid Id { get; }
  public SessionTranscript Transcript { get; }
  public string? WorkingDirectory { get; private set; }

  internal SessionKind Kind { get; }
  internal int SubagentDepth { get; }
  internal bool IsClosed => closed;
  internal QuestionDelivery Questions { get; }
  internal SessionExecutionRecorder Recorder => CurrentExecution?.Recorder
      ?? throw new InvalidOperationException("The session has no active execution recorder.");

  internal long HistoryRevision { get; private set; }

  // Caller holds the session lock.
  internal bool IsAcceptingWork => !closed && acceptingUserMessages && !messageEditing.IsEditing && !IsSnoozed
      && snoozeCleanup is null;
  // Caller holds the session lock.
  internal bool CanStartWork => IsAcceptingWork && !needsAttention;
  // Caller holds the session lock.
  internal bool CanAcceptEdits => !closed && acceptingUserMessages && !needsAttention && !IsSnoozed
      && snoozeCleanup is null;

  internal void ThrowIfClosed() {
    if (closed)
      throw new KeyNotFoundException($"Session '{Id}' does not exist.");
  }

  private protected async Task<(SessionCloseStatus Status, IReadOnlyList<Guid>? DeletedSessions)> DeleteIfIdleAsync(
      Action ensureManagerOpen, CancellationToken cancellation) {
    using var _ = await sessionLock.AcquireAsync(cancellation);
    ensureManagerOpen();
    cancellation.ThrowIfCancellationRequested();
    if (closed)
      return (SessionCloseStatus.NotFound, null);
    Guid? drainedRun = CurrentExecution is { CanCloseAfterFailure: true } driver ? driver.ExecutionId : null;
    if (messageEditing.IsEditing || (drainedRun is null && (!CanStartWork || CurrentExecution is not null)))
      return (SessionCloseStatus.Busy, null);

    SessionSnapshot snapshot = ReadCurrentSnapshot();
    if (drainedRun is null
        ? snapshot.UnsettledExecutionId is not null || snapshot.AdmissionFenced || snapshot.StopRequested
        : snapshot.UnsettledExecutionId is { } unsettled && unsettled != drainedRun)
      return (SessionCloseStatus.Busy, null);

    // The gate serializes deletion with admission. Only the specific run whose driver verified
    // cleanup can be deleted without a saved outcome. The store checks the entire tree.
    IReadOnlyList<Guid>? deletedSessions = store.DeletePrimarySessionTree(Id, drainedRun, CancellationToken.None);
    if (deletedSessions is null)
      return (SessionCloseStatus.NotFound, null);

    closed = true;
    closeCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Questions.ForgetRecipient();
    notifications.Close();
    return (SessionCloseStatus.Closed, deletedSessions);
  }

  internal void CompleteCloseCleanup(Exception? failure) {
    if (failure is null)
      closeCleanup!.TrySetResult();
    else {
      closeCleanup!.TrySetException(failure);
      _ = closeCleanup.Task.Exception;
    }
  }

  internal async Task JoinCloseCleanupAsync() {
    Task? pending;
    using (await sessionLock.AcquireAsync())
      pending = closeCleanup?.Task;
    if (pending is not null)
      await pending;
  }

  // Caller holds the session lock.
  internal void ReplaceHistoryRevision(long revision) {
    HistoryRevision = revision;
    manualCompaction.Invalidate("Queued compaction skipped because history changed.");
    status.Replaced(revision);
  }

  // Caller holds the session lock.
  private void FenceUserMessages() {
    if (!closed && acceptingUserMessages) {
      acceptingUserMessages = false;
      manualCompaction.Invalidate("Queued compaction cancelled because the session is shutting down.");
      PublishChange();
    }
  }

  // Caller holds the session lock.
  internal SessionExecutionDriver? CurrentExecution => activeExecution.Current;

  // Caller holds the session lock.
  internal void MarkAcceptedInput() => hasAcceptedInput = true;

  // Restores the delivered starting view; opening a session alone never composes it.
  internal StartingContext? ReadStartingContext() => store.StartingContexts.Read(Id);
  internal StartingContext? ReadStartingContext(Guid boundaryId) => store.StartingContexts.Read(Id, boundaryId);
  // The selection a session reuses instead of pruning tools and instructions; primaries prune.
  internal virtual StartingContext? ReadInheritedSelection() => null;
  private protected StartingContext? ReadActiveStartingContext(Guid sessionId) => store.StartingContexts.Read(sessionId);
  internal UserPreferences ReadPreferences() => preferences.Read();
  public IReadOnlyList<Guid>? ReadSubagentProviders() => store.Sessions.ReadSubagentProviders(Id);

  internal Task<LlmSelection> ResolveAuxiliaryModelAsync(AuxiliaryModelPurpose purpose,
      UserPreferences captured, CancellationToken cancellation) => modelSelector?.ResolveAsync(purpose, captured,
          Kind == SessionKind.Subagent ? ReadSubagentProviders() : null, cancellation)
      ?? Task.FromException<LlmSelection>(new InvalidOperationException("No auxiliary model is configured."));

  internal async Task<LlmSelection?> ResolveCompactionModelAsync(CancellationToken cancellation) {
    if (modelSelector is null)
      return null;
    try {
      return await modelSelector.ResolveAsync(ModelBucket.Small, ReadPreferences(),
          Kind == SessionKind.Subagent ? ReadSubagentProviders() : null, cancellation);
    } catch (AuxiliaryModelUnavailableException) {
      return null;
    }
  }

  internal Task<bool> CanDelegateAsync(CancellationToken cancellation) =>
      modelSelector is not null && store.Preferences.WithCurrent(p => SubagentDepth < p.MaxSubagentDepth)
          ? modelSelector.HasSubagentProvidersAsync(ReadSubagentProviders(), cancellation)
          : Task.FromResult(false);

  public Task<CompactionReceipt?> CompactAsync(ManualCompactionRequest submission) => manualCompaction.CompactAsync(submission);
  public Task<CompactionReceipt?> CancelQueuedCompactionAsync(Guid operationId) => manualCompaction.CancelQueuedAsync(operationId);
  public Task<CompactionReceipt?> ReadCompactionReceiptAsync(Guid operationId, CancellationToken cancellation = default) =>
      manualCompaction.ReadReceiptAsync(operationId, cancellation);
  public Task<CompactionSummary?> ReadCompactionAsync(Guid id, long revision, CancellationToken cancellation = default) =>
      Transcript.ReadCompactionAsync(id, revision, cancellation);

  // Manager shutdown joins executions before terminating deliberately retained processes.
  internal Task KillProcessesAsync() => OperatingSystem.IsWindows() && processes is not null
      ? processes.KillAllAsync() : Task.CompletedTask;

  public Task<UserMessageReceipt?> ReadUserMessageReceiptAsync(Guid submissionId, CancellationToken cancellation = default) =>
      inputs.ReadUserMessageReceiptAsync(submissionId, cancellation);
  public Task<ContinuationReceipt?> ReadContinuationReceiptAsync(Guid operationId, CancellationToken cancellation = default) =>
      inputs.ReadContinuationReceiptAsync(operationId, cancellation);
  public Task<NotSentPage> ReadNotSentInputsAsync(string? cursor = null, CancellationToken cancellation = default) =>
      inputs.ReadNotSentInputsAsync(cursor, cancellation);
  public Task<bool> RemovePendingInputAsync(Guid executionId, Guid submissionId) =>
      inputs.RemovePendingInputAsync(executionId, submissionId);

  public async Task<SessionSnapshot> ReadSnapshotAsync(CancellationToken cancellation = default) {
    using var _ = await sessionLock.AcquireAsync(cancellation);
    SessionSnapshot snapshot = ReadCurrentSnapshot();
    bool locallyIdle = CanStartWork && CurrentExecution is null
        && snapshot.UnsettledExecutionId is null && !snapshot.AdmissionFenced && !snapshot.StopRequested;
    bool canRemovePending = CanStartWork && !snapshot.AdmissionFenced && !snapshot.StopRequested
        && CurrentExecution is { IsStopping: false } && snapshot.UnsettledExecutionId == CurrentExecution.ExecutionId;
    string? compactionReason = CompactionUnavailableReason(snapshot);
    return snapshot with {
      RequiresRecovery = needsAttention,
      CanCloseAfterFailure = CurrentExecution?.CanCloseAfterFailure == true,
      IsCompacting = CurrentExecution?.IsCompacting == true,
      QueuedCompactionId = manualCompaction.QueuedId,
      ManualCompactionId = CurrentExecution is { IsManualCompaction: true } manual ? manual.ExecutionId : null,
      CompactionUnavailableReason = compactionReason,
      ContextUsage = !snapshot.AdmissionFenced && !snapshot.StopRequested
          && CurrentExecution is { IsCompacting: false, ContextEstimate.UsedTokens: long liveTokens }
          ? new(liveTokens, SessionContextUsageSource.Estimated) : snapshot.ContextUsage,
      WorkingDirectory = WorkingDirectory,
      CanChangeWorkingDirectory = CanChangeWorkingDirectory(snapshot),
      CanContinue = snapshot.CanContinue && locallyIdle,
      CanCompact = compactionReason is null,
      PendingInputs = snapshot.PendingInputs.Select(input => input with {
        CanRemove = input.CanRemove && canRemovePending
      }).ToArray()
    };
  }

  public Task<PrepassDetails?> ReadPrepassDetailsAsync(Guid boundaryId, string category,
      long historyRevision, int candidateOffset = 0, int resultOffset = 0, CancellationToken cancellation = default) =>
      Transcript.ReadPrepassDetailsAsync(boundaryId, category, historyRevision, candidateOffset, resultOffset, cancellation);

  public async Task<string?> TryChangeWorkingDirectoryAsync(string path) {
    string normalized = PreferenceManager.NormalizeWorkingDirectory(path);
    using (await sessionLock.AcquireAsync()) {
      ThrowIfClosed();
      SessionSnapshot snapshot = ReadCurrentSnapshot();
      if (!CanChangeWorkingDirectory(snapshot))
        return null;

      WriteToStore(() => store.Sessions.SetWorkingDirectory(Id, normalized));
      WorkingDirectory = normalized;
      PublishChange();
      return normalized;
    }
  }

  public async Task<IDisposable> SubscribeAsync(Action<SessionChange> observer, bool watched,
      CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(observer);
    using var _ = await sessionLock.AcquireAsync(cancellation);
    if (closed)
      return notifications.SubscribeClosed(observer);
    SessionSnapshot stored = store.Snapshots.ReadPersisted(Id);
    status.Update(stored, CurrentExecution);
    return notifications.Subscribe(observer, watched,
        status.CurrentStatus(stored, IsAcceptingWork, needsAttention, CurrentExecution), stored.Title,
        HasPendingUserQuestion);
  }

  private protected Task<ContinuationReceipt?> AdmitContinuationAsync(ContinuationSubmission submission) =>
      inputs.AdmitContinuationAsync(submission);
  private protected Task<UserMessageReceipt> AdmitUserMessageAsync(UserMessageSubmission submission) =>
      inputs.AdmitUserMessageAsync(submission);
  private protected Task<Guid> AdmitAssignmentAsync(IReadOnlyList<UserMessageSubmission> submissions, Guid parentSessionId) =>
      inputs.AdmitAssignmentAsync(submissions, parentSessionId);

  public Task<MessageEditReceipt?> ReadMessageEditReceiptAsync(Guid operationId, CancellationToken cancellation = default) =>
      messageEditing.ReadReceiptAsync(operationId, cancellation);
  private protected Task<MessageEditReceipt> ReplaceUserMessageAsync(MessageEditSubmission submission) =>
      messageEditing.ReplaceAsync(submission);

  internal async Task StopAcceptingUserMessagesAsync() {
    using var _ = await sessionLock.AcquireAsync();
    FenceUserMessages();
  }

  // A failed Stop write must not prevent cancellation or skip joining owned work.
  internal async Task StopExecutionAsync() {
    Task? pendingEdit;
    using (await sessionLock.AcquireAsync())
      pendingEdit = messageEditing.PendingEdit;
    await Task.WhenAll(activeExecution.StopAndDrainAsync(), MessageEditing.JoinPendingEditAsync(pendingEdit));
  }

  public Task<StopReceipt> RequestStopAsync(Guid executionId) => activeExecution.RequestStopAsync(executionId);
  internal Task<SessionExecutionResult> WaitForExecutionFinishAsync(Guid executionId, CancellationToken cancellation = default) =>
      activeExecution.WaitForFinishAsync(executionId, cancellation);

  // Caller holds the session lock. Results may finish during Stop, but no new work may start.
  internal bool CanRecordExecution(Guid executionId, bool allowStopping = false) =>
      IsCurrentExecution(executionId) && (allowStopping || !CurrentExecution!.IsStopping);

  // Captured identities can outlive a history edit; never authorize their writes in a new revision.
  internal bool CanRecordExecution(ExecutionIdentity identity, bool allowStopping = false) =>
      identity.SessionId == Id && identity.HistoryRevision == HistoryRevision
      && CanRecordExecution(identity.ExecutionId, allowStopping);

  // Live tool output is transient and only useful to currently watched sessions.
  internal void PublishToolProgress(ToolProgressUpdate progress) => notifications.PublishToolProgress(progress);

  internal Lock ToolDraftGate => notifications.ToolDraftGate;
  internal void PublishToolDraft(ToolDraftUpdate draft, Func<ToolDraftUpdate>? readLatest) =>
      notifications.PublishToolDraft(draft, readLatest);

  // Completion and cancellation-failure notifications never start another driver or join themselves.
  internal async Task ReportToolOutcomeAsync(Guid executionId, Exception? failure = null) {
    using var _ = await sessionLock.AcquireAsync();
    if (IsCurrentExecution(executionId))
      CurrentExecution!.ReportToolOutcome(failure);
  }

  // Caller holds the session lock. The driver starts only after admission releases it.
  internal TaskCompletionSource StartExecutionAfterUnlock(Guid executionId, LlmSelection selection,
      ManualCompactionRequest? compaction = null, LlmRequest? firstRequest = null, string? compactionSkipReason = null) =>
      activeExecution.Register(executionId, selection, compaction, firstRequest, compactionSkipReason);

  // Caller holds the session lock. Save before releasing resources or publishing settlement.
  internal (SessionExecutionResult Saved, QueuedInputStart? Next) SaveSettlement(Guid executionId, ExecutionOutcome outcome) {
    var (saved, next) = store.Executions.TryFinish(Id, executionId, outcome, canStartQueuedInput: CanStartWork);
    return (saved ?? throw new InvalidOperationException("The current execution could not be settled."), next);
  }

  // Caller holds the session lock. A completed run may admit a queued waking ask.
  internal TaskCompletionSource? FinishExecution(SessionExecutionDriver execution, SessionExecutionResult saved,
      QueuedInputStart? next, out bool notifyCompletion) {
    Questions.FinishExecution(failedConversation: saved.Outcome == ExecutionOutcome.Failed && !execution.IsManualCompaction);
    execution.Release();
    activeExecution.Clear();
    notifications.ClearToolDrafts();
    status.Settled(saved);
    TaskCompletionSource? inputStart = next is null ? null : StartExecutionAfterUnlock(next.ExecutionId, next.Selection);
    PublishChange();
    TaskCompletionSource? wakeStart = inputStart is null && saved.Outcome == ExecutionOutcome.Completed
        ? Questions.StartPendingQuestionRun() : null;
    // Stop cancels the current execution, not an independently accepted queued compaction.
    TaskCompletionSource? nextStart = inputStart ?? wakeStart ?? manualCompaction.StartQueued();
    if (saved.Outcome != ExecutionOutcome.Completed)
      completionPending = false;
    else if (!execution.IsManualCompaction)
      completionPending = true;
    notifyCompletion = completionPending && Kind == SessionKind.Primary && nextStart is null
        && !HasPendingUserQuestion && CanStartWork;
    if (notifyCompletion)
      completionPending = false;
    return nextStart;
  }

  internal void NotifyCompletion() => manager?.NotifyAttention(AttentionKind.Completion);

  private bool IsCurrentExecution(Guid executionId) => activeExecution.IsCurrent(executionId);

  internal async Task<T> ReadUnderLockAsync<T>(Func<T> read, CancellationToken cancellation = default) {
    using var _ = await sessionLock.AcquireAsync(cancellation);
    return read();
  }

  // Caller holds the session lock.
  internal SessionSnapshot ReadCurrentSnapshot() =>
      status.WithLiveState(store.Snapshots.ReadPersisted(Id), IsAcceptingWork, CurrentExecution);

  // Caller holds the session lock. Reuse the normal live fence and estimate without reading a full snapshot.
  internal SubagentView? ReadSubagentView() {
    var (view, unsettled, stopping) = store.Snapshots.ReadSubagentView(Id, live: true);
    if (view is null)
      return null;
    var stored = new SessionSnapshot(Id, view.HistoryRevision, true, false, unsettled, stopping,
        null, null, [], ContextUsage: view.ContextUsage);
    SessionSnapshot current = status.WithLiveState(stored, IsAcceptingWork, CurrentExecution);
    return view with {
      AdmissionFenced = current.AdmissionFenced,
      ContextUsage = !current.AdmissionFenced && !current.StopRequested
          && CurrentExecution is { IsCompacting: false, ContextEstimate.UsedTokens: long tokens }
          ? new(tokens, SessionContextUsageSource.Estimated) : view.ContextUsage
    };
  }

  // Caller holds the session lock. LastMeasured describes an older context and cannot gate work.
  internal string? CompactionHistoryReason(SessionSnapshot snapshot) {
    if (snapshot.FirstUserPreview is null && !HasAcceptedInput())
      return "There is no history to compact.";
    SessionContextUsage? usage = CurrentExecution is { IsCompacting: false, ContextEstimate.UsedTokens: long tokens }
        ? new(tokens, SessionContextUsageSource.Estimated) : snapshot.ContextUsage;
    return usage is { Source: not SessionContextUsageSource.LastMeasured, UsedTokens: < 256 }
        ? "Context usage is too low to compact." : null;
  }

  internal string? CompactionUnavailableReason(SessionSnapshot? snapshot = null) {
    snapshot ??= ReadCurrentSnapshot();
    if (!CanStartWork || snapshot.AdmissionFenced || snapshot.StopRequested || CurrentExecution?.IsStopping == true)
      return "The session cannot accept compaction now.";
    if (manualCompaction.QueuedId is not null || CurrentExecution?.IsManualCompaction == true)
      return "Manual compaction is already pending or running.";
    if (CurrentExecution is null && snapshot.UnsettledExecutionId is not null)
      return "The session has unsettled work.";
    return CompactionHistoryReason(snapshot);
  }

  // Caller holds the session lock.
  internal TaskCompletionSource? StartQueuedCompaction() => manualCompaction.StartQueued();

  // Caller holds the session lock.
  private bool CanChangeWorkingDirectory(SessionSnapshot snapshot) =>
      WorkingDirectory is not null && CanStartWork && CurrentExecution is null
      && snapshot.UnsettledExecutionId is null && !snapshot.AdmissionFenced && !HasAcceptedInput();

  // Caller holds the session lock. Accepted decisions never become eligible again.
  private bool HasAcceptedInput() => hasAcceptedInput ??= store.Sessions.HasAcceptedInput(Id);

  // Read under the session lock alongside the rest of the tab metadata.
  protected virtual bool HasPendingUserQuestion => false;

  // Caller holds the session lock.
  internal void PublishChange(string? publishedTitle = null) =>
      notifications.Publish(status.ReadCurrent(IsAcceptingWork, needsAttention, CurrentExecution), publishedTitle,
          HasPendingUserQuestion);

  // Caller holds the session lock.
  internal void MarkNeedsAttention() {
    if (needsAttention && !acceptingUserMessages)
      return;

    needsAttention = true;
    acceptingUserMessages = false;
    manualCompaction.Invalidate("Queued compaction cancelled because the session needs attention.");
    PublishChange();
  }

  // A failed write may still have been committed, so refuse new user messages rather than guess.
  internal void WriteToStore(Action write) {
    try {
      write();
    } catch (SqliteException) {
      MarkNeedsAttention();
      throw;
    }
  }

  internal T WriteToStore<T>(Func<T> write) {
    try {
      return write();
    } catch (SqliteException) {
      MarkNeedsAttention();
      throw;
    }
  }
}
