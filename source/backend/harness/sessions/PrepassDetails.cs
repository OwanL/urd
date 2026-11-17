namespace Urd.Core.Sessions;

public sealed record ResponseHeaderInfo(string ModelId, string ReasoningEffort, DateTimeOffset? StartedAt);

public sealed record PrepassResource(string Id, string Label, string? Path = null);

// Slim boundary metadata; resource lists and debugging output are read only on demand.
public sealed record PrepassSummary(Guid BoundaryId, string Category, string State,
    int? CandidateCount, int? ResultCount, string? Error = null);

public sealed record PendingResponse(ResponseHeaderInfo Header, IReadOnlyList<PrepassSummary> Preparation);

public sealed record PrepassDetails(Guid ExecutionId, Guid BoundaryId, string Category, string State,
    string? ModelId, string? ReasoningEffort, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    decimal? CostUsd, bool CostIsEstimated, int CandidateCount, int? ResultCount,
    IReadOnlyList<PrepassResource> Candidates, IReadOnlyList<PrepassResource> Results,
    int CandidateOffset, int ResultOffset, string Reasoning, string Output,
    bool ReasoningTruncated, bool OutputTruncated, string? Title = null, string? Error = null);
