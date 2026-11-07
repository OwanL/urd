using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Carries a stored session's bounded history and execution state.
internal sealed record StoredSessionSnapshot(
    long HistoryRevision, Guid? UnsettledRunId, bool StopRequested,
    ExecutionResult? LastSettlement, IReadOnlyList<MessagePreview> Messages, bool HasOlderMessages);
