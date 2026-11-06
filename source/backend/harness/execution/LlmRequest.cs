using Microsoft.Extensions.AI;

namespace Urd.Core.Execution;

// Input for one LLM request. Storage prepares it, and SessionBase hands it over
// only after the request claim is committed.
internal sealed record LlmRequest(
    Guid RequestId,
    IReadOnlyList<ChatMessage> Messages);
