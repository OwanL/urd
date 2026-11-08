using Urd.Core.Execution;
using Urd.Core.Providers;

namespace Urd.Core.Sessions;

// Bounded committed facts combined with live admission and Stop state by SessionBase.
public sealed record SessionSnapshot(
    Guid SessionId, long HistoryRevision, bool AdmissionFenced,
    Guid? UnsettledExecutionId, bool StopRequested, SessionExecutionResult? LastSettlement,
    IReadOnlyList<MessagePreview> Messages, bool HasOlderMessages, LlmSelection? LatestLlmSelection = null);
