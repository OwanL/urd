namespace Urd.Core.Providers;

// Connection generation, API and model binding for one retained provider response.
internal sealed record LlmProviderScope(Guid ConnectionId, long Generation, LlmApi Api, string ModelId);

// Only this immutable, secret-free envelope crosses the storage boundary.
internal sealed record LlmProviderResponse(LlmProviderScope Scope, string Json);
