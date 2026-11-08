namespace Urd.Core.Providers;

// Only this immutable, secret-free envelope crosses the storage boundary.
internal sealed record LlmProviderResponse(LlmProviderScope Scope, string Json);
