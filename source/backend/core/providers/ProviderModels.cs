namespace Urd.Core.Providers;

// A provider-supplied model ID and display name, without inferred capabilities or pricing.
public sealed record ProviderModel(string Id, string Name);

// The last successfully fetched catalog; a null timestamp means it has never been fetched.
public sealed record ProviderModelCatalog(
    Guid ConnectionId, DateTimeOffset? RefreshedAt, IReadOnlyList<ProviderModel> Models);

// The completed one-request diagnostic, not a saved agent conversation.
public sealed record ProviderModelTestResult(
    Guid ConnectionId, string ModelId, string Text, bool Truncated, ProviderUsage? Usage);

// Missing usage values remain unknown rather than being reported as zero.
public sealed record ProviderUsage(
    long? InputTokens, long? OutputTokens, long? TotalTokens, long? CachedInputTokens);
