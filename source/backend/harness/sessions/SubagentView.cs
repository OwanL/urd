namespace Urd.Core.Sessions;

// Pollable metadata for the read-only view of a retained child.
public sealed record SubagentView(long HistoryRevision, bool Live, string Title, Guid ParentId,
    string? ModelId, string? ReasoningEffort, int? ContextWindow, SessionContextUsage? ContextUsage,
    SessionWorkingTime? WorkingTime, bool AdmissionFenced);
