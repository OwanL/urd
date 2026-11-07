using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Carries a stored session's bounded history and execution state.
internal sealed record StoredSessionSnapshot(
    long HistoryRevision, Guid? UnsettledExecutionId, bool StopRequested,
    SessionExecutionResult? LastSettlement, IReadOnlyList<MessagePreview> Messages, bool HasOlderMessages);
