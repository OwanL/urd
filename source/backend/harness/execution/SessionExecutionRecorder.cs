using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Concurrency;
using Urd.Core.Sessions;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Core.Tools;

namespace Urd.Core.Execution;

// Persists request and tool progress under the session lock, rejecting stale or stopped work.
internal sealed class SessionExecutionRecorder(SessionBase session, UrdStore store, AsyncLock sessionLock,
    ExecutionIdentity identity) {
  private readonly Dictionary<(string Purpose, string Channel), char> pendingPreviewSurrogates = [];
  internal ExecutionIdentity Identity { get; } = identity;

  internal HarnessToolContext CreateHarnessToolContext(string callId) =>
      new(Identity.SessionId, Identity.ExecutionId, callId, new ToolSessionServices(store.Sessions));

  internal Task<bool> StartRequestAsync(Guid requestId, StartingContext startingContext) =>
    RecordChangeAsync(() => session.WriteToStore(() => store.Executions.StartRequest(Identity, requestId, startingContext)));

  internal async Task<bool> RecordOutputAsync(LlmResponseChunk chunk) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity)
        || !store.LlmResponses.Append(Identity, chunk))
      return false;

    session.PublishChange();
    return true;
  }

  internal Task<bool> RecordToolCallAsync(Guid requestId, string callId, string? name) =>
    RecordChangeAsync(() => store.LlmResponses.RecordToolCall(Identity, requestId, callId, name));

  internal Task<bool> CompleteRequestAsync(LlmRequestResult outcome) =>
    RecordChangeAsync(() => store.LlmResponses.CompleteRequest(Identity, outcome),
      allowStopping: outcome.Outcome != ExecutionOutcome.Completed);

  internal Task<bool> TryStartToolAsync(Guid requestId, string callId, bool runInBackground = false) =>
    RecordChangeAsync(() => store.Tools.TryStart(Identity, requestId, callId, runInBackground));

  internal Task<bool> CompleteToolAsync(Guid requestId, LlmToolResult result) =>
    RecordChangeAsync(() => store.Tools.Complete(Identity, requestId, result), allowStopping: true);

  internal Task<bool> TryClaimTitleAsync() => RecordChangeAsync(() => store.Prepass.TryClaimTitle(Identity));

  internal Task<bool> PublishTitleAsync(string title) => RecordChangeAsync(() => store.Prepass.PublishTitle(Identity, title));

  internal Task<bool> PublishFallbackTitleAsync(string title, string? error) =>
      RecordChangeAsync(() => store.Prepass.PublishFallbackTitle(Identity, title, error));

  internal Task<bool> RecordPruningStatusAsync(Guid boundaryId, StartingResourcePruningStatus status) =>
      RecordChangeAsync(() => store.Prepass.WritePruningStatus(Identity, boundaryId, status),
          allowStopping: status.State != "running");

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

  internal async Task<bool> RecordChangeAsync(Func<bool> record, bool allowStopping = false) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity, allowStopping) || !session.WriteToStore(record))
      return false;

    session.PublishChange();
    return true;
  }
}
