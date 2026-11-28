namespace Urd.Core.Sessions;

public sealed record SubagentDiscovery(Guid Id, Guid ParentId, string Title, bool TitlePending);
