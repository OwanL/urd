namespace Urd.Core.Sessions;

// Display-only snapshot; it is not execution or transcript evidence.
public sealed record ToolDraftUpdate(Guid ExecutionId, Guid RequestId, Guid EntryId, string CallId, string Name,
    int Order, string State, long ArgumentBytes, string Head, string Tail, string? Field = null);
