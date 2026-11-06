namespace Urd.Core.Sessions;

// Identifies one user or assistant message within a history revision.
public sealed record MessageRef(
    Guid RunId, Guid MessageId, MessageRole Role, long HistoryRevision);
