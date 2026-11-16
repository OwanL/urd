using Urd.Core.Storage;

namespace Urd.Core.Execution;

// One isolated conversation under the parent's lock and recording authority, not a task owner.
// Its caller must join provider/tool work before the parent execution settles.
internal sealed class AuxiliaryConversationRecorder(SessionExecutionRecorder parent,
    AuxiliaryConversationStorage storage, Guid conversationId) {
  internal Guid ConversationId => conversationId;

  internal Task<bool> StartRequestAsync(Guid requestId) =>
    parent.RecordChangeAsync(() => storage.StartRequest(parent.Identity, conversationId, requestId));

  internal Task<bool> RecordOutputAsync(LlmResponseChunk chunk) =>
    parent.RecordChangeAsync(() => storage.Append(parent.Identity, conversationId, chunk));

  internal Task<bool> RecordToolCallAsync(Guid requestId, string callId, string? name) =>
    parent.RecordChangeAsync(() => storage.RecordToolCall(parent.Identity, conversationId, requestId, callId, name));

  internal Task<bool> CompleteRequestAsync(LlmRequestResult result) =>
    parent.RecordChangeAsync(() => storage.CompleteRequest(parent.Identity, conversationId, result),
      allowStopping: result.Outcome != ExecutionOutcome.Completed);

  internal Task<bool> TryStartToolAsync(Guid requestId, string callId) =>
    parent.RecordChangeAsync(() => storage.TryStartTool(parent.Identity, conversationId, requestId, callId));

  internal Task<bool> CompleteToolAsync(Guid requestId, LlmToolResult result) =>
    parent.RecordChangeAsync(() => storage.CompleteTool(parent.Identity, conversationId, requestId, result), allowStopping: true);
}
