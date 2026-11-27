using System.Text.Json.Serialization;
using Urd.Core.Execution;
using Urd.Core.Providers;

namespace Urd.Core.Sessions;

// Status, title and question availability describe tab metadata. A null status marks a watched update.
public sealed record SessionChange([property: JsonPropertyName("session")] Guid SessionId, long Version,
    string? Status = null, string? Title = null,
    [property: JsonPropertyName("toolProgress")] ToolProgressUpdate? ToolProgress = null,
    bool HasPendingUserQuestion = false);

// Transient output for one running tool call. A null Text clears its displayed progress.
public sealed record ToolProgressUpdate(Guid ExecutionId, Guid RequestId, string CallId, string? Text);

// Bounded committed facts combined with live admission and Stop state by SessionBase.
public sealed record SessionSnapshot(
    Guid SessionId, long HistoryRevision, bool AdmissionFenced, bool CanChangeWorkingDirectory,
    Guid? UnsettledExecutionId, bool StopRequested, SessionExecutionResult? LastSettlement,
    string? FirstUserPreview, IReadOnlyList<PendingInputPreview> PendingInputs,
    LlmSelection? LatestLlmSelection = null, bool IsCompacting = false, SessionWorkingTime? WorkingTime = null,
    SessionContextUsage? ContextUsage = null, string? WorkingDirectory = null, bool CanContinue = false,
    string? Title = null, IReadOnlyList<StartingResourcePruningStatus>? Pruning = null,
    PendingResponse? PendingResponse = null, bool CanCompact = false, Guid? QueuedCompactionId = null,
    string? CompactionUnavailableReason = null, Guid? ManualCompactionId = null, bool RequiresRecovery = false,
    bool CanCloseAfterFailure = false);

public sealed record StartingResourcePruningStatus(
    string Category, string State, int Total, int Retained, string? Error = null);

public sealed record SessionContextUsage(long UsedTokens, SessionContextUsageSource Source);

public enum SessionContextUsageSource { Measured, Estimated, LastMeasured }

public sealed record SessionWorkingTime(
    long CompletedMilliseconds, DateTimeOffset? StartedAt, bool HasUnknownDuration = false);

// A bounded preview of an accepted input whose containing request has not started sending.
// Claimed inputs are assigned to a request, but are not yet recorded as attempted.
public sealed record PendingInputPreview(Guid SubmissionId, string Preview, bool CanRemove, bool IsClaimed);
