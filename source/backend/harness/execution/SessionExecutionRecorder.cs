using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Concurrency;
using Urd.Core.Sessions;
using Urd.Core.Providers;
using Urd.Core.Storage;

namespace Urd.Core.Execution;

// Persists request and tool progress under the session lock, rejecting stale or stopped work.
internal sealed class SessionExecutionRecorder(SessionBase session, UrdStore store, AsyncLock sessionLock,
    ExecutionIdentity identity) {
  internal ExecutionIdentity Identity { get; } = identity;

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

  internal async Task<AuxiliaryConversationRecorder?> CreateConversationAsync(Guid conversationId, string purpose,
      LlmSelection selection, IReadOnlyList<ChatMessage> input, IReadOnlyList<AIFunctionDeclaration> tools) {
    bool created = await RecordChangeAsync(() => store.AuxiliaryConversations.Create(
        Identity, conversationId, purpose, selection, input, tools));
    return created ? new(this, store.AuxiliaryConversations, conversationId) : null;
  }

  internal async Task<bool> RecordChangeAsync(Func<bool> record, bool allowStopping = false) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity, allowStopping) || !session.WriteToStore(record))
      return false;

    session.PublishChange();
    return true;
  }
}
