namespace Urd.Core.Sessions;

// Thrown when a read targets a history revision that is no longer current.
public sealed class HistoryRevisionConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException($"History revision {expectedRevision} no longer matches current revision {actualRevision}.") {
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}
