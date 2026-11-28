namespace Urd.Core.Sessions;

// A retained call gets the same model, duration and ledger facts without reopening the child.
public sealed record SubagentCall(Guid Id, Guid ParentId, string Title, bool TitlePending, bool Live,
    string? ModelId, DateTimeOffset? StartedAt, long? ElapsedMilliseconds, bool DurationIncomplete, SubagentCost Cost);

public sealed record SubagentCost(SubagentCostAmount Own, SubagentCostAmount Descendants,
    SubagentCostAmount Total, bool HasDescendants);

public sealed record SubagentCostAmount(decimal? Usd, bool Partial);
