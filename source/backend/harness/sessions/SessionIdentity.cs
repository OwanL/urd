using System.Text.Json.Serialization;
using Urd.Core.Context;
using Urd.Core.Providers;

namespace Urd.Core.Sessions;

// Stored identity, ownership, history revision and chat-local path default.
internal sealed record SessionRecord(
    Guid Id, long HistoryRevision, string? WorkingDirectory, SessionKind Kind, Guid? ParentSessionId,
    string? OwningCallId, Guid? OwningRequestId);

// A bounded batch of saved identities, newest-created first. Copy the opaque cursor to read older IDs.
public sealed record SessionIdPage(IReadOnlyList<Guid> SessionIds, string? NextCursor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LlmSelection? LatestLlmSelection = null);
