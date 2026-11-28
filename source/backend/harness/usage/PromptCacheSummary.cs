namespace Urd.Core.Usage;

public sealed record PromptCacheTotals(long InputTokens, long CachedInputTokens, long ReportedRequests,
    long TotalRequests);

public sealed record PromptCacheSummary(PromptCacheTotals? LastTurn, PromptCacheTotals Session,
    PromptCacheTotals? Warning = null);
