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
  private readonly ISubagentRunner? subagents;
  private readonly AgentQuestions? agentQuestions;
  private readonly Func<ProviderConnection, IChatClient> createChatClient;
  private readonly ToolCatalog tools;
  private readonly StartingContextBuilder startingContextBuilder;
  private readonly ChatProcesses? processes;
  private readonly PendingToolCalls pending;
  private readonly AsyncLock sessionLock = new();
  private readonly SessionNotifications notifications;
  private readonly SessionStatus status;
  private bool acceptingUserMessages = true;
  private bool needsAttention;
  private bool? hasAcceptedInput;
  private SessionExecutionDriver? execution;
  // Retain the driver's task after settlement clears the active execution, so shutdown joins its tail.
  private Task? executionTask;
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
    this.subagents = subagents;
    agentQuestions = (subagents as SessionManager)?.Questions;
    this.createChatClient = createChatClient;
    this.tools = tools ?? new ToolCatalog([]);
    startingContextBuilder = new(this.tools, instructionDiscovery: store.InstructionDiscovery);
    this.processes = processes;
    this.pending = pending ?? new PendingToolCalls();
  }

  public Guid Id { get; }
  public SessionTranscript Transcript { get; }
  public string? WorkingDirectory { get; private set; }

  internal SessionKind Kind { get; }
  internal SessionExecutionRecorder Recorder => execution?.Recorder
      ?? throw new InvalidOperationException("The session has no active execution recorder.");

  internal long HistoryRevision { get; private set; }

  private bool IsAcceptingWork => acceptingUserMessages && editTask is null;
  private bool CanStartWork => IsAcceptingWork && !needsAttention;

  // Restores the delivered starting view; opening a session alone never composes it.
  internal StartingContext? ReadStartingContext() => store.StartingContexts.Read(Id);
  internal StartingContext? ReadStartingContext(Guid boundaryId) => store.StartingContexts.Read(Id, boundaryId);
  internal UserPreferences ReadPreferences() => preferences.Read();
  internal Task<LlmSelection> ResolveAuxiliaryModelAsync(AuxiliaryModelPurpose purpose,
      UserPreferences captured, CancellationToken cancellation) => modelSelector?.ResolveAsync(purpose, captured, cancellation)
      ?? Task.FromException<LlmSelection>(new InvalidOperationException("No auxiliary model is configured."));

  public async Task<CompactionReceipt?> CompactAsync(ManualCompactionRequest submission) {
    ArgumentNullException.ThrowIfNull(submission);
    if (submission.OperationId == Guid.Empty || submission.ExpectedHistoryRevision < 0
        || submission.LlmSelection is null || submission.Focus?.Length > 8192)
      throw new ArgumentException("Compaction requires an operation ID, revision, selection and bounded focus.", nameof(submission));
    submission.LlmSelection.Validate();
    TaskCompletionSource? executionStart = null;
    CompactionReceipt? receipt;
    using (await sessionLock.AcquireAsync()) {
      var admission = WriteToStore(() => store.Compactions.Start(Id, submission,
          CanStartWork && submission.ExpectedHistoryRevision == HistoryRevision));
      if (admission is null)
        return null;
      receipt = admission.Value.Receipt;
      if (admission.Value.New) {
        executionStart = StartExecutionAfterUnlock(submission.OperationId, submission.LlmSelection, submission);
        PublishChange();
      }
    }
    executionStart?.SetResult();
    return receipt;
  }

  public Task<CompactionReceipt?> ReadCompactionReceiptAsync(Guid operationId, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.Compactions.ReadReceipt(Id, operationId), cancellation);

  public Task<CompactionSummary?> ReadCompactionAsync(Guid id, long revision, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.Compactions.Read(Id, revision, id), cancellation);

  internal async Task<ModelHistory> ReadModelHistoryAsync(Guid executionId, Guid? requestId) {
    using var _ = await sessionLock.AcquireAsync();
    if (!CanRecordExecution(executionId))
      throw new OperationCanceledException("The execution no longer accepts compaction.");
    return store.Compactions.ReadContext(Id, HistoryRevision, requestId is null ? null : executionId, requestId);
  }

  internal async Task<IReadOnlyList<BackgroundToolCallInput>> ReadCompactionBackgroundCallsAsync(
      Guid executionId, HistoryMessageLocation firstRetained) {
    using var _ = await sessionLock.AcquireAsync();
    if (!CanRecordExecution(executionId))
      throw new OperationCanceledException("The execution no longer accepts compaction.");
    return store.Compactions.ReadBackgroundToolCalls(Id, HistoryRevision, firstRetained);
  }

  internal async Task<bool> SaveCompactionAsync(Guid executionId, HistoryCompactionPlan plan, GeneratedCompactionSummary result) {
    using var _ = await sessionLock.AcquireAsync();
    if (!CanRecordExecution(executionId))
      return false;
    bool saved = WriteToStore(() => store.Compactions.Save(Id, executionId, HistoryRevision, plan, result));
    if (saved)
      PublishChange();
    return saved;
  }

  internal async Task SetCompactingAsync(Guid executionId, bool compacting) {
    using var _ = await sessionLock.AcquireAsync();
    if (IsCurrentExecution(executionId)) {
      execution!.SetCompacting(compacting);
      PublishChange();
    }
  }

  internal async Task RecordCompactionErrorAsync(Guid executionId, string? error) {
    using var _ = await sessionLock.AcquireAsync();
    if (CanRecordExecution(executionId, allowStopping: true))
      WriteToStore(() => store.Compactions.RecordError(Id, executionId, error));
  }

  internal Task<bool> CanRetryOverflowAsync(Guid executionId, Guid requestId) =>
      ReadUnderLockAsync(() => CanRecordExecution(executionId) && store.Executions.CanRetryOverflow(Id, executionId, requestId));

  // Manager shutdown joins executions before terminating deliberately retained processes.
  internal Task KillProcessesAsync() => OperatingSystem.IsWindows() && processes is not null
      ? processes.KillAllAsync() : Task.CompletedTask;

  public Task<UserMessageReceipt?> ReadUserMessageReceiptAsync(Guid submissionId, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.UserMessages.ReadReceipt(Id, submissionId), cancellation);

  public async Task<ContinuationReceipt?> ReadContinuationReceiptAsync(Guid operationId,
      CancellationToken cancellation = default) {
    if (operationId == Guid.Empty)
      throw new ArgumentException("An operation ID is required.", nameof(operationId));
    return await ReadUnderLockAsync(() => store.Continuations.ReadReceipt(Id, operationId), cancellation);
  }

  public Task<NotSentPage> ReadNotSentInputsAsync(string? cursor = null, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => store.NotSentInputs.Read(Id, cursor), cancellation);

  public async Task<bool> RemovePendingInputAsync(Guid executionId, Guid submissionId) {
    if (executionId == Guid.Empty || submissionId == Guid.Empty)
      throw new ArgumentException("Execution and submission identities are required.");

    using var _ = await sessionLock.AcquireAsync();
    if (!CanStartWork || execution is not { } current
        || current.Identity.ExecutionId != executionId || current.Identity.HistoryRevision != HistoryRevision
        || current.IsStopping)
      return false;

    SessionSnapshot snapshot = ReadCurrentSnapshot();
    if (snapshot.AdmissionFenced || snapshot.StopRequested || snapshot.UnsettledExecutionId != executionId)
      return false;

    bool removed = WriteToStore(() => store.Executions.RemovePendingInput(current.Identity, submissionId));
    if (removed) {
      current.Wake();
      PublishChange();
    }
    return removed;
  }

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

  private protected async Task<ContinuationReceipt?> AdmitContinuationAsync(ContinuationSubmission submission) {
    ArgumentNullException.ThrowIfNull(submission);
    TaskCompletionSource? executionStart = null;
    ContinuationReceipt? receipt;
    using (await sessionLock.AcquireAsync()) {
      var admission = WriteToStore(() => store.Continuations.Start(Id, HistoryRevision, submission,
          CanStartWork && execution is null));
      if (admission is null)
        return null;

      receipt = admission.Value.Receipt;
      if (admission.Value.New) {
        executionStart = StartExecutionAfterUnlock(receipt.ExecutionId, submission.LlmSelection!);
        PublishChange();
      }
    }

    executionStart?.SetResult();
    return receipt;
  }

  private protected async Task<UserMessageReceipt> AdmitUserMessageAsync(UserMessageSubmission submission) {
    // A new execution starts only after the lock is released.
    TaskCompletionSource? executionStart = null;
    UserMessageReceipt receipt;
    using (await sessionLock.AcquireAsync()) {
      (receipt, bool newlyAccepted) = WriteToStore(
          () => store.UserMessages.Submit(Id, HistoryRevision, submission, IsAcceptingWork, Guid.NewGuid(), execution?.ExecutionId));
      if (receipt.Acceptance == MessageAcceptance.Accepted)
        hasAcceptedInput = true;

      // Steering joins the owned execution; neither it nor a retained receipt starts another driver.
      if (newlyAccepted) {
        if (execution is null)
          executionStart = StartExecutionAfterUnlock(receipt.ExecutionId!.Value, receipt.Submission.LlmSelection!);
        execution!.Wake();
        PublishChange();
      }
    }

    executionStart?.SetResult();
    return receipt;
  }

  // Admits a bounded assignment into its first execution in one lock hold, so every input shares
  // the first request. Refusals throw: a bounded child owns no retries and no resumptions.
  private protected async Task<Guid> AdmitAssignmentAsync(IReadOnlyList<UserMessageSubmission> submissions, Guid parentSessionId) {
    if (submissions.Count == 0)
      throw new ArgumentException("An assignment requires at least one input.", nameof(submissions));

    TaskCompletionSource? executionStart = null;
    Guid executionId;
    using (await sessionLock.AcquireAsync()) {
      UserMessageReceipt lead = WriteToStore(() => store.UserMessages.Submit(
          Id, HistoryRevision, submissions[0], IsAcceptingWork, Guid.NewGuid(),
          provenance: submissions.Count == 1 ? InputProvenance.AgentAssignment : InputProvenance.InheritedContext,
          senderSessionId: parentSessionId)).Receipt;
      if (lead.Acceptance != MessageAcceptance.Accepted)
        throw new InvalidOperationException($"The assignment was refused ({lead.RefusalReason}).");

      executionId = lead.ExecutionId!.Value;
      foreach (UserMessageSubmission steered in submissions.Skip(1)) {
        UserMessageReceipt receipt = WriteToStore(() => store.UserMessages.Submit(
            Id, HistoryRevision, steered, IsAcceptingWork, Guid.NewGuid(), executionId,
            provenance: InputProvenance.AgentAssignment, senderSessionId: parentSessionId)).Receipt;
        if (receipt.Acceptance != MessageAcceptance.Accepted)
          throw new InvalidOperationException($"The assignment was refused ({receipt.RefusalReason}).");
      }

      hasAcceptedInput = true;
      executionStart = StartExecutionAfterUnlock(executionId, submissions[0].LlmSelection!);
      PublishChange();
    }

    executionStart?.SetResult();
    return executionId;
  }

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

      pendingExecution = executionTask!;
      pendingSettlement = execution!.Settlement;
    }

    await pendingExecution.WaitAsync(cancellation);
    return await pendingSettlement.WaitAsync(cancellation);
  }

  internal async Task<AgentQuestionResult> QueueQuestionAsync(HarnessToolContext caller, string prompt,
      bool wake, CancellationToken cancellation) {
    TaskCompletionSource? executionStart = null;
    Task<AgentQuestionResult> waiting;
    using (await sessionLock.AcquireAsync(cancellation)) {
      if (!CanStartWork)
        return new(null, "The recipient agent is not accepting work.");

      AgentQuestions broker = agentQuestions!;
      waiting = broker.Enqueue(Id, caller.SessionId, caller.RequestId, caller.CallId, prompt, wake, cancellation,
          out Guid questionId);
      if (wake && !waiting.IsCompleted) {
        if (execution is { IsStopping: false } active)
          active.Wake();
        else if (execution is null) {
          try {
            executionStart = StartQuestionRun(broker);
          } catch (Exception failure) {
            broker.Fail(Id, questionId, failure.Message);
          }
        }
      }
    }

    executionStart?.SetResult();
    return await waiting;
  }

  // Caller holds the lock. The waking ask can bring an earlier non-waking head along, never skip it.
  private TaskCompletionSource? StartQuestionRun(AgentQuestions broker) {
    LlmSelection selection = store.Executions.ReadLatestSelection(Id)
        ?? throw new InvalidOperationException("The recipient agent has no saved model selection.");
    Guid requestId = Guid.NewGuid();
    if (broker.Present(Id, requestId) is not { } question)
      return null;

    if (!question.Wakes) {
      broker.Withdraw(Id, question, requestId);
      return null;
    }

    var run = new ExecutionIdentity(Id, Guid.NewGuid(), HistoryRevision);
    LlmRequest firstRequest;
    try {
      firstRequest = WriteToStore(() => store.Executions.StartQuestionRun(run, requestId, question, selection));
    } catch {
      broker.Withdraw(Id, question, requestId);
      throw;
    }
    hasAcceptedInput = true;
    TaskCompletionSource executionStart = StartExecutionAfterUnlock(run.ExecutionId, selection, firstRequest: firstRequest);
    PublishChange();
    return executionStart;
  }

  // Caller holds the session lock; the driver publishes after updating its waiting state.
  internal LlmRequest? CreateNextRequest(ExecutionIdentity identity) {
    if (!CanRecordExecution(identity))
      return null;

    Guid requestId = Guid.NewGuid();
    QuestionPresentation? question = agentQuestions?.Present(Id, requestId);
    LlmRequest? next = null;
    try {
      next = store.Executions.CreateNextRequest(identity, requestId, question);
      return next;
    } finally {
      if (next is null && question is not null)
        agentQuestions!.Withdraw(Id, question, requestId);
    }
  }

  // Caller holds the session lock. Abort releases question waiters before joining owned work.
  internal void FailPresentedQuestion() => agentQuestions?.FailPresented(Id);

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

  // Caller holds the lock. The driver starts only after admission releases it.
  private TaskCompletionSource StartExecutionAfterUnlock(Guid executionId, LlmSelection selection,
      ManualCompactionRequest? compaction = null, LlmRequest? firstRequest = null) {
    var executionStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var identity = new ExecutionIdentity(SessionId: Id, ExecutionId: executionId, HistoryRevision: HistoryRevision);
    var recorder = new SessionExecutionRecorder(this, store, sessionLock, identity, subagents);
    var work = new SessionExecution(recorder, this, createChatClient, selection,
        startingContextBuilder, tools.Tools, pending, compaction);
    execution = new SessionExecutionDriver(this, sessionLock, work);
    executionTask = execution.RunAsync(executionStart.Task, firstRequest);
    return executionStart;
  }

  // Caller holds the session lock. Save before releasing resources and publishing;
  // only then may the driver complete its settlement task.
  internal SessionExecutionResult FinishExecution(SessionExecutionDriver execution, ExecutionOutcome outcome) {
    SessionExecutionResult saved = store.Executions.TryFinish(Id, execution.ExecutionId, outcome)
        ?? throw new InvalidOperationException("The current execution could not be settled.");

    if (Kind == SessionKind.Subagent)
      agentQuestions?.CloseRecipient(Id);
    else
      FailPresentedQuestion();
    execution.Release();
    this.execution = null;
    status.Settled(saved);
    PublishChange();
    return saved;
  }

  private bool IsCurrentExecution(Guid executionId) => execution?.ExecutionId == executionId;

  private async Task<T> ReadUnderLockAsync<T>(Func<T> read, CancellationToken cancellation = default) {
    using var _ = await sessionLock.AcquireAsync(cancellation);
    return read();
  }

  // Caller holds the session lock.
  private SessionSnapshot ReadCurrentSnapshot() =>
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
