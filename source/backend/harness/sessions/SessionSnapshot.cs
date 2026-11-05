using Urd.Core.Execution;

namespace Urd.Core.Sessions;

/// <summary>A bounded committed view, combined with the live session's admission fence.</summary>
public sealed record SessionSnapshot(
    Guid SessionId, long HistoryRevision, bool AdmissionFenced,
    Guid? UnsettledRunId, bool StopRequested, ExecutionResult? LastSettlement,
    IReadOnlyList<MessagePreview> Messages, bool HasOlderMessages);

public sealed record MessagePreview(
    MessageRef Message, string Preview, bool Truncated,
    RowContentCursor AvailableThrough, bool FinalOutputCommitted,
    ExecutionOutcome? Settlement);

public sealed record MessageRef(
    Guid RunId, Guid MessageId, MessageRole Role, long HistoryRevision);

public enum MessageRole { User, Assistant }

/// <summary>Original UTF-8 byte offset within an immutable persisted segment.</summary>
public sealed record RowContentCursor(MessageRef Message, long SegmentIndex, int Utf8Offset);

/// <summary>Closure concerns message bytes, not successful execution or cleanup.</summary>
public sealed record RowContentPage(
    RowContentCursor From, string Text, RowContentCursor Next,
    RowContentCursor AvailableThrough, bool HasMore,
    bool IsClosed, bool FinalOutputCommitted);

public sealed class HistoryRevisionConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException($"History revision {expectedRevision} no longer matches current revision {actualRevision}.")
{
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}
