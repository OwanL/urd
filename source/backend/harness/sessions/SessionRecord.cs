namespace Urd.Core.Sessions;

// Stored identity and history revision used to construct a live session.
internal sealed record SessionRecord(Guid Id, long HistoryRevision);
