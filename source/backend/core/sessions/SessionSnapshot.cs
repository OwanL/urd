using Urd.Core.Execution;

namespace Urd.Core.Sessions;

// Bounded committed view of a session, combined with its live admission fence.
public sealed record SessionSnapshot(
    Guid SessionId, long HistoryRevision, bool AdmissionFenced,
    Guid? UnsettledExecutionId, bool StopRequested, SessionExecutionResult? LastSettlement,
    IReadOnlyList<MessagePreview> Messages, bool HasOlderMessages);
