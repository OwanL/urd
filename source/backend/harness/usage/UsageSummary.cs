namespace Urd.Core.Usage;

public sealed record UsageTotals(decimal CostUsd, long Requests, long EstimatedRequests, long UnknownRequests, long ActiveRequests);

public sealed record UsageBreakdown(string Provider, string Model, string Purpose, UsageTotals Totals,
    bool IsOther = false);

public sealed record UsagePoint(DateTimeOffset At, UsageTotals Totals, IReadOnlyList<UsageBreakdown> Breakdown);

// TotalsComplete is false when retained storage cannot answer the selected range, such as a partly
// overlapping closed session; its totals are then unknowable, not zero.
public sealed record UsageSummary(UsageTotals Totals, IReadOnlyList<UsageBreakdown> Breakdown,
    IReadOnlyList<UsagePoint> History, IReadOnlyList<UsageRequest> Recent, bool TotalsComplete = true);

public sealed record UsageRequest(Guid Id, Guid? SessionId, string Provider, string Model, string Purpose,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, decimal? CostUsd, bool IsEstimate, string? Problem,
    UsageTokens? Tokens, UsageRates? Rates, Guid ConnectionId, Guid? ExecutionId = null);
