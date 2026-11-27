using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Compaction;
using Urd.Core.Concurrency;
using Urd.Core.MarkdownRendering;
using Urd.Core.Sessions;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Core.Tools;

namespace Urd.Core.Execution;

// Persists request and tool progress under the session lock, rejecting stale or stopped work.
internal sealed class SessionExecutionRecorder(SessionBase session, UrdStore store, AsyncLock sessionLock,
    ExecutionIdentity identity, ISubagentRunner? subagents = null) {
  private readonly Dictionary<(string Purpose, string Channel), char> pendingPreviewSurrogates = [];
  private readonly MarkdownImageImports.Observer imageImports = store.MarkdownImageImports.ObserveExecution(identity, session.WorkingDirectory);
  internal ExecutionIdentity Identity { get; } = identity;

  // One calling context per tool call. The manager owns both bounded child runs and the
  // question broker, so ask support derives from the same instance as subagent runs.
  internal HarnessToolContext CreateHarnessToolContext(string callId, bool background) =>
      new(Identity.SessionId, Identity.ExecutionId, callId,
          new ToolSessionServices(store.Sessions, subagents, (IAgentQuestions?)subagents)) { Background = background };

  internal async Task<ModelHistory> ReadModelHistoryAsync(Guid? requestId) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity.ExecutionId))
      throw new OperationCanceledException("The execution no longer accepts compaction.");
    return store.Compactions.ReadContext(session.Id, session.HistoryRevision,
        requestId is null ? null : Identity.ExecutionId, requestId);
  }

  internal async Task<IReadOnlyList<BackgroundToolCallInput>> ReadCompactionBackgroundCallsAsync(
      HistoryMessageLocation firstRetained) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity.ExecutionId))
      throw new OperationCanceledException("The execution no longer accepts compaction.");
    return store.Compactions.ReadBackgroundToolCalls(session.Id, session.HistoryRevision, firstRetained);
  }

  internal async Task<bool> SaveCompactionAsync(HistoryCompactionPlan plan, GeneratedCompactionSummary result) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity.ExecutionId))
      return false;
    bool saved = session.WriteToStore(() => store.Compactions.Save(
        session.Id, Identity.ExecutionId, session.HistoryRevision, plan, result));
    if (saved)
      session.PublishChange();
    return saved;
  }

  internal async Task SetCompactingAsync(bool compacting) {
    using var _ = await sessionLock.AcquireAsync();
    if (session.CanRecordExecution(Identity.ExecutionId, allowStopping: true)) {
      session.CurrentExecution!.SetCompacting(compacting);
      session.PublishChange();
    }
  }

  internal async Task RecordCompactionErrorAsync(string? error) {
    using var _ = await sessionLock.AcquireAsync();
    if (session.CanRecordExecution(Identity.ExecutionId, allowStopping: true))
      session.WriteToStore(() => store.Compactions.RecordError(session.Id, Identity.ExecutionId, error));
  }

  internal async Task<bool> CanRetryOverflowAsync(Guid requestId) {
    using var _ = await sessionLock.AcquireAsync();
    return session.CanRecordExecution(Identity.ExecutionId)
        && store.Executions.CanRetryOverflow(session.Id, Identity.ExecutionId, requestId);
  }

  internal Task<bool> StartRequestAsync(Guid requestId, StartingContext startingContext) =>
    RecordChangeAsync(() => session.WriteToStore(() => store.Executions.StartRequest(Identity, requestId, startingContext)));

  internal async Task<bool> RecordOutputAsync(LlmResponseChunk chunk) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity)
        || !store.LlmResponses.Append(Identity, chunk))
      return false;

    imageImports.Observe(chunk);
    session.PublishChange();
    return true;
  }

  internal async Task<bool> RecordToolCallAsync(Guid requestId, string callId, string? name, ToolDraftBuffer? drafts = null) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity))
      return false;

    Guid? entryId = drafts?.FindEntryId(callId);
    if (session.WriteToStore(() => store.LlmResponses.RecordToolCall(Identity, requestId, callId, name, entryId))
        is not { } reservedId)
      return false;

    // Stop takes this same lock, so it cannot retire a draft between commit and promotion.
    if (drafts is not null && reservedId == entryId)
      drafts.Promote(reservedId);
    imageImports.CloseResponse();
    session.PublishChange();
    return true;
  }

  internal Task<bool> CompleteRequestAsync(LlmRequestResult outcome, ToolDraftBuffer? drafts = null) =>
    RecordChangeAsync(() => store.LlmResponses.CompleteRequest(Identity, outcome),
      allowStopping: outcome.Outcome != ExecutionOutcome.Completed, afterSave: () => {
        session.CurrentExecution!.RetireContextEstimate();
        drafts?.Retire();
        imageImports.CloseResponse();
      });

  internal ToolDraftBuffer CreateToolDraftBuffer(Guid requestId) =>
      new(Identity.ExecutionId, requestId, session.ToolDraftGate, session.PublishToolDraft);

  internal async Task PublishContextEstimateAsync(RequestContextEstimate estimate, CancellationToken cancellation) {
    using var _ = await sessionLock.AcquireAsync(cancellation);
    if (session.CanRecordExecution(Identity) && ReferenceEquals(session.CurrentExecution!.ContextEstimate, estimate))
      session.PublishChange();
  }

  internal Task JoinImageImportsAsync() => imageImports.JoinAsync();

  internal Task<bool> TryStartToolAsync(Guid requestId, string callId, bool runInBackground = false) =>
    RecordChangeAsync(() => store.Tools.TryStart(Identity, requestId, callId, runInBackground));

  internal Task<bool> CompleteToolAsync(Guid requestId, LlmToolResult result) =>
    RecordChangeAsync(() => store.Tools.Complete(Identity, requestId, result), allowStopping: true);

  internal void ReportToolProgress(Guid requestId, string callId, ToolOutput output) =>
      session.PublishToolProgress(new(Identity.ExecutionId, requestId, callId, output.Text));

  internal void ClearToolProgress(Guid requestId, string callId) =>
      session.PublishToolProgress(new(Identity.ExecutionId, requestId, callId, null));

  internal Task<bool> TryClaimTitleAsync() => RecordChangeAsync(() => store.Prepass.TryClaimTitle(Identity));

  internal Task<bool> PublishTitleAsync(string title) => RecordTitleAsync(() => store.Prepass.PublishTitle(Identity, title));

  internal Task<bool> PublishFallbackTitleAsync(string title, string? error) =>
      RecordTitleAsync(() => store.Prepass.PublishFallbackTitle(Identity, title, error), allowStopping: true);

  private async Task<bool> RecordTitleAsync(Func<string?> publish, bool allowStopping = false) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity, allowStopping) || session.WriteToStore(publish) is not { } title)
      return false;

    session.PublishChange(title);
    return true;
  }

  // Running updates are fenced by Stop; terminal results may commit while Stop drains the parent.
  internal Task<bool> RecordPruningProgressAsync(Guid boundaryId, StartingResourcePruningStatus status,
      IReadOnlyList<PrepassResource> candidates, IReadOnlyList<string>? resultIds) =>
      RecordChangeAsync(() => store.Prepass.WritePruningProgress(Identity, boundaryId, status, candidates, resultIds),
          allowStopping: status.State != "running");

  internal Task<bool> RecordTitleOutcomeAsync(string state, string? error) =>
      RecordChangeAsync(() => store.Prepass.RecordTitleOutcome(Identity, state, error), allowStopping: true);

  internal async Task<bool> RecordPrepassPreviewAsync(string purpose, string? output = null, string? reasoning = null) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity))
      return false;

    char? pendingOutput = ReadPending(purpose, "output");
    char? pendingReasoning = ReadPending(purpose, "reasoning");
    PrepassPreviewWrite recorded = session.WriteToStore(() => store.Prepass.RecordPreview(
        Identity, purpose, output, reasoning, pendingOutput, pendingReasoning));
    if (!recorded.Accepted)
      return false;

    SavePending(purpose, "output", recorded.PendingOutput);
    SavePending(purpose, "reasoning", recorded.PendingReasoning);
    session.PublishChange();
    return true;
  }

  internal Task ReportAuxiliaryFailureAsync(Exception failure) => session.ReportToolOutcomeAsync(Identity.ExecutionId, failure);

  internal async Task<AuxiliaryConversationRecorder?> CreateConversationAsync(Guid conversationId, string purpose,
      AuxiliaryModelResolution model, IReadOnlyList<ChatMessage> input, IReadOnlyList<AIFunctionDeclaration> tools) {
    bool created = await RecordChangeAsync(() => store.Prepass.RecordModel(Identity, purpose, model)
        && store.AuxiliaryConversations.Create(Identity, conversationId, purpose, model.Selection, input, tools));
    return created ? new(this, store.AuxiliaryConversations, conversationId) : null;
  }

  private char? ReadPending(string purpose, string channel) =>
      pendingPreviewSurrogates.TryGetValue((purpose, channel), out char pending) ? pending : null;

  private void SavePending(string purpose, string channel, char? pending) {
    var key = (purpose, channel);
    if (pending is { } character)
      pendingPreviewSurrogates[key] = character;
    else
      pendingPreviewSurrogates.Remove(key);
  }

  internal async Task<bool> RecordChangeAsync(Func<bool> record, bool allowStopping = false, Action? afterSave = null) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity, allowStopping) || !session.WriteToStore(record))
      return false;

    afterSave?.Invoke();
    session.PublishChange();
    return true;
  }
}
