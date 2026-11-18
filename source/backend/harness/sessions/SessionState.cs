using System.Text.Json.Serialization;
using Urd.Core.Execution;
using Urd.Core.Providers;

namespace Urd.Core.Sessions;

// Status and title describe tab metadata. A null status marks a watched content change.
public sealed record SessionChange([property: JsonPropertyName("session")] Guid SessionId, long Version,
    string? Status = null, string? Title = null);

// Bounded committed facts combined with live admission and Stop state by SessionBase.
public sealed record SessionSnapshot(
    Guid SessionId, long HistoryRevision, bool AdmissionFenced, bool CanChangeWorkingDirectory,
    Guid? UnsettledExecutionId, bool StopRequested, SessionExecutionResult? LastSettlement,
    string? FirstUserPreview, IReadOnlyList<PendingInputPreview> PendingInputs,
    LlmSelection? LatestLlmSelection = null, bool IsCompacting = false, SessionWorkingTime? WorkingTime = null,
    SessionContextUsage? ContextUsage = null, string? WorkingDirectory = null, bool CanContinue = false,
    string? Title = null, IReadOnlyList<StartingResourcePruningStatus>? Pruning = null,
    PendingResponse? PendingResponse = null, bool CanCompact = false);

public sealed record StartingResourcePruningStatus(
    string Category, string State, int Total, int Retained, string? Error = null);

public sealed record SessionContextUsage(long UsedTokens, bool IsEstimate);

public sealed record SessionWorkingTime(
    long CompletedMilliseconds, DateTimeOffset? StartedAt, bool HasUnknownDuration = false);

// A bounded preview of an accepted input whose containing request has not started sending.
// Claimed inputs are assigned to a request, but are not yet recorded as attempted.
public sealed record PendingInputPreview(Guid SubmissionId, string Preview, bool CanRemove, bool IsClaimed);
