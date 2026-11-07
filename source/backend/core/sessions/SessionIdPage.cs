namespace Urd.Core.Sessions;

// A bounded batch of saved identities, newest-created first. Copy the opaque cursor to read older IDs.
public sealed record SessionIdPage(IReadOnlyList<Guid> SessionIds, string? NextCursor);
