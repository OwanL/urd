namespace Urd.Core.Execution;

// Identifies a persisted execution tuple; it does not grant live execution authority.
internal readonly record struct ExecutionIdentity(Guid SessionId, Guid ExecutionId, long HistoryRevision);
