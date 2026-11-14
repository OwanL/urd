namespace Urd.Core.Usage;

public record UsageTokens(
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? CacheWrite30mTokens = 0,
    long? CacheWrite12hTokens = 0,
    long? ReasoningTokens = null,
    long? TotalTokens = null);
