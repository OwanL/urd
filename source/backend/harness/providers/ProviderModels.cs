namespace Urd.Core.Providers;

// Provider-supplied catalog metadata, enriched with reference prices on reads.
public sealed record ProviderModel(string Id, string Name, int? ContextWindow = null,
    LlmApi? Api = null, int? MaxOutputTokens = null, IReadOnlyList<string>? ReasoningEfforts = null,
    ProviderModelPrice? Price = null);

// Reference API rates per million tokens, not a statement of subscription billing.
public sealed record ProviderModelPrice(decimal? InputPerMillion, decimal? OutputPerMillion,
    string Source, DateTimeOffset CapturedAt);

// The last successfully fetched catalog; a null timestamp means it has never been fetched.
public sealed record ProviderModelCatalog(
    Guid ConnectionId, DateTimeOffset? RefreshedAt, IReadOnlyList<ProviderModel> Models);

// The completed one-request diagnostic, not a saved agent conversation.
public sealed record ProviderModelTestResult(
    Guid ConnectionId, string ModelId, string Text, bool Truncated, ProviderUsage? Usage);

// Missing usage values remain unknown rather than being reported as zero.
public sealed record ProviderUsage(
    long? InputTokens, long? OutputTokens, long? TotalTokens, long? CachedInputTokens);
