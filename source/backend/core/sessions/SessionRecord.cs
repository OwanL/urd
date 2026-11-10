namespace Urd.Core.Sessions;

// Stored identity, history revision and chat-local path default.
internal sealed record SessionRecord(Guid Id, long HistoryRevision, string? WorkingDirectory);
