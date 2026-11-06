using Microsoft.Extensions.AI;

namespace Urd.Core.Execution;

// LLM input, buffered reply text, request outcomes and saved execution results.
// SessionBase coordinates the handoffs between SessionExecution and SessionStore.

// Outcome of a local LLM request attempt or a whole session execution.
public enum ExecutionOutcome
{
    Completed,
    Cancelled,
    Failed
}

// Saved execution result, published only after owned work and cleanup finish.
public sealed record ExecutionResult(
    Guid RunId,
    ExecutionOutcome Outcome);

// Input for one LLM request, prepared by SessionStore and supplied through
// SessionBase only after the request claim is committed.
internal sealed record LlmRequest(
    Guid RequestId,
    IReadOnlyList<ChatMessage> Messages);

// Buffered response text sent through SessionBase for persistence.
// IsFinal closes the text, not the execution; the final chunk may be empty.
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
    string? Error = null);
