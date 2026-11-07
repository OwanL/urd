using Microsoft.Extensions.AI;

namespace Urd.Core.Execution;

// Input for one LLM request. Storage prepares it, and SessionBase hands it over
// only after the request claim is committed.
internal sealed record LlmRequest(
    Guid RequestId,
    IReadOnlyList<ChatMessage> Messages);

// Buffered response text sent through SessionBase for persistence.
// IsFinal closes the text, not the run; the final chunk may be empty.
internal sealed record LlmResponseChunk(
    Guid RequestId,
    Guid ResponseId,
    long ChunkIndex,
    string Text,
    bool IsFinal);

// Local outcome of one LLM request attempt, not live provider status.
// SessionExecution reports it to SessionBase, which still has to save it.
internal sealed record LlmRequestOutcome(
    Guid RequestId,
    ExecutionOutcome Outcome,
    string? Error = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null);
