namespace Urd.Core.Sessions;

// Stored identity, history revision and chat-local path default.
internal sealed record SessionRecord(Guid Id, long HistoryRevision, string? WorkingDirectory);

// A bounded batch of saved identities, newest-created first. Copy the opaque cursor to read older IDs.
public sealed record SessionIdPage(IReadOnlyList<Guid> SessionIds, string? NextCursor);
