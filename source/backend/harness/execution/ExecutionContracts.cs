using Microsoft.Extensions.AI;

namespace Urd.Core.Execution;

// LLM input, buffered reply text, request outcomes and saved execution results.
// SessionBase coordinates the handoffs between SessionExecution and SessionStore.

/// <summary>How a local LLM request attempt or an entire session execution ended.</summary>
public enum ExecutionOutcome
{
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// Final result of a session execution, saved before SessionBase returns it to
/// waiters or includes it in a snapshot. Owned work and cleanup have finished.
/// </summary>
public sealed record ExecutionResult(
    Guid RunId,
    ExecutionOutcome Outcome);

/// <summary>
/// Input prepared by SessionStore for one LLM request. SessionBase supplies it to
/// SessionExecution for IChatClient after the request claim is committed.
/// </summary>
internal sealed record LlmRequest(
    Guid RequestId,
    IReadOnlyList<ChatMessage> Messages);

/// <summary>
/// LLM reply text buffered by SessionExecution and sent to SessionBase to save.
/// IsFinal marks the end of the text, not the execution; the last chunk may be empty.
/// </summary>
internal sealed record LlmResponseChunk(
    Guid RequestId,
    Guid ResponseId,
    long ChunkIndex,
    string Text,
    bool IsFinal);

/// <summary>
/// How one LLM request attempt ended, reported by SessionExecution to SessionBase.
/// This is our final outcome, not live provider status; SessionBase still has to save it.
/// </summary>
internal sealed record LlmRequestOutcome(
    Guid RequestId,
    ExecutionOutcome Outcome,
    string? Error = null);
