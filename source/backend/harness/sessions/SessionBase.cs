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
public abstract class SessionBase {
  private readonly UrdStore store;
  private readonly PreferenceManager preferences;
  private readonly ModelSelector? modelSelector;
  private readonly QuestionDelivery questions;
  private readonly SessionInputs inputs;
  private readonly ManualCompaction manualCompaction;
  private readonly ChatProcesses? processes;
  private readonly ActiveExecution activeExecution;
  private readonly AsyncLock sessionLock = new();
  private readonly SessionNotifications notifications;
  private readonly SessionStatus status;
  private bool acceptingUserMessages = true;
  private bool needsAttention;
  private bool? hasAcceptedInput;
  private SessionExecutionDriver? execution => activeExecution.Current;
  private Task<MessageEditReceipt>? editTask;
  private MessageEditSubmission? editSubmission;

  private protected SessionBase(SessionRecord record, SessionKind kind, UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      ToolCatalog? tools = null, ChatProcesses? processes = null, PendingToolCalls? pending = null,
      PreferenceManager? preferences = null, ModelSelector? modelSelector = null, ISubagentRunner? subagents = null) {
    Id = record.Id;
    Kind = kind;
    notifications = new SessionNotifications(Id);
    status = new SessionStatus(record.HistoryRevision);
    Transcript = new SessionTranscript(Id, store, sessionLock);
    HistoryRevision = record.HistoryRevision;
    WorkingDirectory = record.WorkingDirectory;
    this.store = store;
    this.preferences = preferences ?? new PreferenceManager(store);
    this.modelSelector = modelSelector;
    inputs = new SessionInputs(this, store, sessionLock);
    manualCompaction = new ManualCompaction(this, store, sessionLock);
    questions = new QuestionDelivery(this, store, sessionLock, (subagents as SessionManager)?.Questions);
    activeExecution = new ActiveExecution(this, store, sessionLock, createChatClient,
        tools ?? new ToolCatalog([]), pending ?? new PendingToolCalls(), subagents);
    this.processes = processes;
  }

  public Guid Id { get; }
  public SessionTranscript Transcript { get; }
  public string? WorkingDirectory { get; private set; }

  internal SessionKind Kind { get; }
  internal SessionExecutionRecorder Recorder => execution?.Recorder
      ?? throw new InvalidOperationException("The session has no active execution recorder.");

  internal long HistoryRevision { get; private set; }

  // Caller holds the session lock.
  internal bool IsAcceptingWork => acceptingUserMessages && editTask is null;
  // Caller holds the session lock.
  internal bool CanStartWork => IsAcceptingWork && !needsAttention;

  // Caller holds the session lock.
  internal SessionExecutionDriver? CurrentExecution => execution;

  // Caller holds the session lock.
  internal void MarkAcceptedInput() => hasAcceptedInput = true;

  // Restores the delivered starting view; opening a session alone never composes it.
  internal StartingContext? ReadStartingContext() => store.StartingContexts.Read(Id);
  internal StartingContext? ReadStartingContext(Guid boundaryId) => store.StartingContexts.Read(Id, boundaryId);
  internal UserPreferences ReadPreferences() => preferences.Read();
  public IReadOnlyList<Guid>? ReadSubagentProviders() => store.Sessions.ReadSubagentProviders(Id);

  internal Task<LlmSelection> ResolveAuxiliaryModelAsync(AuxiliaryModelPurpose purpose,
      UserPreferences captured, CancellationToken cancellation) => modelSelector?.ResolveAsync(purpose, captured,
          Kind == SessionKind.Subagent ? ReadSubagentProviders() : null, cancellation)
      ?? Task.FromException<LlmSelection>(new InvalidOperationException("No auxiliary model is configured."));

  internal Task<bool> HasSubagentProvidersAsync(CancellationToken cancellation) =>
      modelSelector?.HasSubagentProvidersAsync(ReadSubagentProviders(), cancellation)
      ?? Task.FromResult(false);

  public Task<CompactionReceipt?> CompactAsync(ManualCompactionRequest submission) => manualCompaction.CompactAsync(submission);
  public Task<CompactionReceipt?> ReadCompactionReceiptAsync(Guid operationId, CancellationToken cancellation = default) => manualCompaction.ReadCompactionReceiptAsync(operationId, cancellation);
  public Task<CompactionSummary?> ReadCompactionAsync(Guid id, long revision, CancellationToken cancellation = default) => manualCompaction.ReadCompactionAsync(id, revision, cancellation);

  internal async Task SetCompactingAsync(Guid executionId, bool compacting) {
    using var _ = await sessionLock.AcquireAsync();
    if (IsCurrentExecution(executionId)) {
      execution!.SetCompacting(compacting);
      PublishChange();
    }
  }

  // Manager shutdown joins executions before terminating deliberately retained processes.
  internal Task KillProcessesAsync() => OperatingSystem.IsWindows() && processes is not null
      ? processes.KillAllAsync() : Task.CompletedTask;

  public Task<UserMessageReceipt?> ReadUserMessageReceiptAsync(Guid submissionId, CancellationToken cancellation = default) => inputs.ReadUserMessageReceiptAsync(submissionId, cancellation);
  public Task<ContinuationReceipt?> ReadContinuationReceiptAsync(Guid operationId, CancellationToken cancellation = default) => inputs.ReadContinuationReceiptAsync(operationId, cancellation);
  public Task<NotSentPage> ReadNotSentInputsAsync(string? cursor = null, CancellationToken cancellation = default) => inputs.ReadNotSentInputsAsync(cursor, cancellation);
  public Task<bool> RemovePendingInputAsync(Guid executionId, Guid submissionId) => inputs.RemovePendingInputAsync(executionId, submissionId);

  public async Task<SessionSnapshot> ReadSnapshotAsync(CancellationToken cancellation = default) {
    using var _ = await sessionLock.AcquireAsync(cancellation);
    SessionSnapshot snapshot = ReadCurrentSnapshot();
    bool locallyIdle = CanStartWork && execution is null
        && snapshot.UnsettledExecutionId is null && !snapshot.AdmissionFenced && !snapshot.StopRequested;
    bool canRemovePending = CanStartWork && !snapshot.AdmissionFenced && !snapshot.StopRequested
        && execution is { IsStopping: false } && snapshot.UnsettledExecutionId == execution.ExecutionId;
    return snapshot with {
      IsCompacting = execution?.IsCompacting == true,
      WorkingDirectory = WorkingDirectory,
      CanChangeWorkingDirectory = CanChangeWorkingDirectory(snapshot),
      CanContinue = snapshot.CanContinue && locallyIdle,
      CanCompact = locallyIdle,
      PendingInputs = snapshot.PendingInputs.Select(input => input with {
        CanRemove = input.CanRemove && canRemovePending
      }).ToArray()
    };
  }

  public async Task<PrepassDetails?> ReadPrepassDetailsAsync(Guid boundaryId, string category,
      long historyRevision, int candidateOffset = 0, int resultOffset = 0,
      CancellationToken cancellation = default) {
    using var _ = await sessionLock.AcquireAsync(cancellation);
    return store.PrepassDetails.Read(Id, historyRevision, boundaryId, category, candidateOffset, resultOffset);
  }

  public async Task<string?> TryChangeWorkingDirectoryAsync(string path) {
    string normalized = PreferenceManager.NormalizeWorkingDirectory(path);
    using (await sessionLock.AcquireAsync()) {
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
    SessionSnapshot stored = store.Snapshots.ReadPersisted(Id);
    status.Update(stored, execution);
    return notifications.Subscribe(observer, watched,
        status.CurrentStatus(stored, IsAcceptingWork, needsAttention, execution), stored.Title);
  }

  private protected Task<ContinuationReceipt?> AdmitContinuationAsync(ContinuationSubmission submission) => inputs.AdmitContinuationAsync(submission);
  private protected Task<UserMessageReceipt> AdmitUserMessageAsync(UserMessageSubmission submission) => inputs.AdmitUserMessageAsync(submission);
  private protected Task<Guid> AdmitAssignmentAsync(IReadOnlyList<UserMessageSubmission> submissions, Guid parentSessionId) => inputs.AdmitAssignmentAsync(submissions, parentSessionId);

  public Task<MessageEditReceipt?> ReadMessageEditReceiptAsync(Guid operationId, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.MessageEdits.ReadReceipt(Id, operationId), cancellation);

  private protected async Task<MessageEditReceipt> ReplaceUserMessageAsync(MessageEditSubmission submission) {
    MessageEditStorage.Validate(submission);
    Task<MessageEditReceipt> work;
    TaskCompletionSource? released = null;
    using (await sessionLock.AcquireAsync()) {
      MessageEditReceipt? receipt = store.MessageEdits.ReadReceiptOrValidate(Id, submission);
      if (receipt is not null)
        return receipt;
      if (editTask is not null) {
        if (editSubmission != submission)
          throw new UserMessageConflictException(submission.OperationId);
        work = editTask;
      } else {
        if (!acceptingUserMessages || needsAttention)
          throw new UserMessageConflictException(submission.OperationId);
        released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        editSubmission = submission;
        work = editTask = ReplaceAfterStopAsync(submission, released.Task);
        PublishChange();
      }
    }
    released?.SetResult();
    return await work;
  }

  private async Task<MessageEditReceipt> ReplaceAfterStopAsync(MessageEditSubmission submission, Task admitted) {
    await admitted;
    TaskCompletionSource? executionStart = null;
    try {
      await StopActiveExecutionAsync();
      using var _ = await sessionLock.AcquireAsync();
      if (!acceptingUserMessages || needsAttention)
        throw new UserMessageConflictException(submission.OperationId);
      MessageEditReceipt receipt = WriteToStore(() => store.MessageEdits.Replace(Id, submission));
      HistoryRevision = receipt.HistoryRevision;
      status.Replaced(HistoryRevision);
      executionStart = StartExecutionAfterUnlock(receipt.ExecutionId, submission.LlmSelection);
      return receipt;
    } finally {
      using (await sessionLock.AcquireAsync()) {
        editTask = null;
        editSubmission = null;
        PublishChange();
      }
      executionStart?.SetResult();
    }
  }

  internal async Task StopAcceptingUserMessagesAsync() {
    using var _ = await sessionLock.AcquireAsync();
    if (acceptingUserMessages) {
      acceptingUserMessages = false;
      PublishChange();
    }
  }

  // Called after all manager-owned sessions have fenced user messages. A failed Stop write
  // must not prevent local cancellation or let shutdown skip waiting for owned work.
  internal async Task StopExecutionAsync() {
    Task? pendingEdit;
    using (await sessionLock.AcquireAsync())
      pendingEdit = editTask;
    await Task.WhenAll(StopActiveExecutionAsync(), JoinEditAsync(pendingEdit));
  }

  private static async Task JoinEditAsync(Task? pendingEdit) {
    if (pendingEdit is null)
      return;
    try {
      await pendingEdit;
    } catch (UserMessageConflictException) {
      // Shutdown fenced an admitted edit before replacement; no history was changed.
    }
  }

  private async Task StopActiveExecutionAsync() {
    Task pendingExecution;
    Task pendingSettlement;
    Task stopCallbacks = Task.CompletedTask;
    Exception? stopFailure = null;
    using (await sessionLock.AcquireAsync()) {
      pendingExecution = activeExecution.RetainedTask ?? Task.CompletedTask;
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

    using (await sessionLock.AcquireAsync()) {
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
  }

  internal async Task<SessionExecutionResult> WaitForExecutionFinishAsync(Guid executionId, CancellationToken cancellation = default) {
    Task pendingExecution;
    Task<SessionExecutionResult> pendingSettlement;
    using (await sessionLock.AcquireAsync(cancellation)) {
      if (!IsCurrentExecution(executionId)) {
        return store.Executions.ReadSettlement(Id, executionId)
            ?? throw new InvalidOperationException($"Execution '{executionId}' has no known settlement in this session.");
      }

      pendingExecution = activeExecution.RetainedTask!;
      pendingSettlement = execution!.Settlement;
    }

    await pendingExecution.WaitAsync(cancellation);
    return await pendingSettlement.WaitAsync(cancellation);
  }

  internal Task<AgentQuestionResult> QueueQuestionAsync(HarnessToolContext caller, string prompt,
      bool wake, CancellationToken cancellation) => questions.QueueQuestionAsync(caller, prompt, wake, cancellation);

  // Caller holds the session lock.
  internal LlmRequest? CreateNextRequest(ExecutionIdentity identity) => questions.CreateNextRequest(identity);

  internal (bool Presented, ChatMessage? Reminder) ReadQuestionForRequest(LlmRequest request) =>
      questions.ReadQuestionForRequest(request);

  // Caller holds the session lock.
  internal void ParkBackgroundWait(Guid executionId) => questions.ParkBackgroundWait(executionId);
  internal void UnparkBackgroundWait(Guid executionId) => questions.UnparkBackgroundWait(executionId);
  internal void FailPresentedQuestion() => questions.FailPresentedQuestion();

  // Caller holds the session lock. Results may finish during Stop, but no new work may start.
  internal bool CanRecordExecution(Guid executionId, bool allowStopping = false) =>
      IsCurrentExecution(executionId) && (allowStopping || !execution!.IsStopping);

  // Captured identities can outlive a history edit; never authorize their writes in a new revision.
  internal bool CanRecordExecution(ExecutionIdentity identity, bool allowStopping = false) =>
      identity.SessionId == Id && identity.HistoryRevision == HistoryRevision
      && CanRecordExecution(identity.ExecutionId, allowStopping);

  // Completion and cancellation-failure notifications never start another driver or join themselves.
  internal async Task ReportToolOutcomeAsync(Guid executionId, Exception? failure = null) {
    using var _ = await sessionLock.AcquireAsync();
    if (IsCurrentExecution(executionId))
      execution!.ReportToolOutcome(failure);
  }

  // Caller holds the session lock. The driver starts only after admission releases it.
  internal TaskCompletionSource StartExecutionAfterUnlock(Guid executionId, LlmSelection selection,
      ManualCompactionRequest? compaction = null, LlmRequest? firstRequest = null) =>
      activeExecution.Register(executionId, selection, compaction, firstRequest);

  // Caller holds the session lock. Save before releasing resources and publishing; only then may
  // the driver complete its settlement task. An ordinary settlement also admits a queued waking
  // ask, so authorization cannot be stranded by a settlement that never presented it.
  internal SessionExecutionResult FinishExecution(SessionExecutionDriver execution, ExecutionOutcome outcome,
      out TaskCompletionSource? wakeStart) {
    SessionExecutionResult saved = store.Executions.TryFinish(Id, execution.ExecutionId, outcome)
        ?? throw new InvalidOperationException("The current execution could not be settled.");

    questions.FinishExecution();
    execution.Release();
    activeExecution.Clear();
    status.Settled(saved);
    PublishChange();
    wakeStart = saved.Outcome == ExecutionOutcome.Completed ? questions.StartPendingQuestionRun() : null;
    return saved;
  }

  private bool IsCurrentExecution(Guid executionId) => activeExecution.IsCurrent(executionId);

  internal async Task<T> ReadUnderLockAsync<T>(Func<T> read, CancellationToken cancellation = default) {
    using var _ = await sessionLock.AcquireAsync(cancellation);
    return read();
  }

  // Caller holds the session lock.
  internal SessionSnapshot ReadCurrentSnapshot() =>
      status.WithLiveState(store.Snapshots.ReadPersisted(Id), IsAcceptingWork, execution);

  // Caller holds the session lock.
  private bool CanChangeWorkingDirectory(SessionSnapshot snapshot) =>
      WorkingDirectory is not null && CanStartWork && execution is null
      && snapshot.UnsettledExecutionId is null && !snapshot.AdmissionFenced && !HasAcceptedInput();

  // Caller holds the session lock. Accepted decisions never become eligible again.
  private bool HasAcceptedInput() => hasAcceptedInput ??= store.Sessions.HasAcceptedInput(Id);

  // Caller holds the session lock.
  internal void PublishChange(string? publishedTitle = null) =>
      notifications.Publish(status.ReadCurrent(IsAcceptingWork, needsAttention, execution), publishedTitle);

  // Caller holds the session lock.
  internal void MarkNeedsAttention() {
    if (needsAttention && !acceptingUserMessages)
      return;

    needsAttention = true;
    acceptingUserMessages = false;
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
