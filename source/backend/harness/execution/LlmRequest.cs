using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Compaction;

namespace Urd.Core.Execution;

// Input for one LLM request. Storage prepares it, and SessionBase hands it over
// only after the request claim is committed.
internal sealed record LlmRequest(
    Guid RequestId,
    IReadOnlyList<ChatMessage> Messages,
    CompactionContext? Context = null);

// Buffered response text sent through SessionBase for persistence.
// IsFinal closes the text, not the execution; the final chunk may be empty.
internal sealed record LlmResponseChunk(
    Guid RequestId,
    Guid ResponseId,
    long ChunkIndex,
    string Text,
    bool IsFinal);

// Local result of one LLM request attempt, not live provider status.
// SessionExecution reports it to SessionBase, which still has to save it.
internal sealed record LlmRequestResult(
    Guid RequestId,
    ExecutionOutcome Outcome,
    string? Error = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    LlmProviderResponse? ProviderResponse = null,
    UsageDetails? Usage = null,
    ProviderFailure? Failure = null,
    bool OutputObserved = false);
