using Urd.Core.Execution;
using Urd.Core.Providers;

namespace Urd.Core.Sessions;

// Status is present for initial registration and status transitions; null marks a watched content change.
public sealed record SessionChange(Guid Session, long Version, string? Status = null);

// Bounded committed facts combined with live admission and Stop state by SessionBase.
public sealed record SessionSnapshot(
    Guid SessionId, long HistoryRevision, bool AdmissionFenced,
    Guid? UnsettledExecutionId, bool StopRequested, SessionExecutionResult? LastSettlement,
    string? FirstUserPreview, IReadOnlyList<PendingInputPreview> PendingInputs,
    LlmSelection? LatestLlmSelection = null, bool IsCompacting = false, SessionWorkingTime? WorkingTime = null,
    SessionContextUsage? ContextUsage = null);

public sealed record SessionContextUsage(long UsedTokens, bool IsEstimate);

public sealed record SessionWorkingTime(
    long CompletedMilliseconds, DateTimeOffset? StartedAt, bool HasUnknownDuration = false);

// A bounded preview of an accepted input whose containing request has not started sending.
public sealed record PendingInputPreview(Guid SubmissionId, string Preview);
