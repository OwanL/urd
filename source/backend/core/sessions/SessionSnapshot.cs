using Urd.Core.Execution;
using Urd.Core.Providers;

namespace Urd.Core.Sessions;

// Bounded committed facts combined with live admission and Stop state by SessionBase.
public sealed record SessionSnapshot(
    Guid SessionId, long HistoryRevision, bool AdmissionFenced,
    Guid? UnsettledExecutionId, bool StopRequested, SessionExecutionResult? LastSettlement,
    string? FirstUserPreview, IReadOnlyList<PendingInputPreview> PendingInputs,
    LlmSelection? LatestLlmSelection = null);

// A bounded preview of an accepted input that has not yet been assigned to a request.
public sealed record PendingInputPreview(Guid SubmissionId, string Preview);
