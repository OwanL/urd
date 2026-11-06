using Urd.Core.Execution;

namespace Urd.Core.Sessions;

// Bounded committed view, combined with the live session's admission fence.
public sealed record SessionSnapshot(
    Guid SessionId, long HistoryRevision, bool AdmissionFenced,
    Guid? UnsettledRunId, bool StopRequested, ExecutionResult? LastSettlement,
    IReadOnlyList<MessagePreview> Messages, bool HasOlderMessages);

public sealed record MessagePreview(
    MessageRef Message, string Preview, bool Truncated,
    MessageContentCursor AvailableThrough, bool FinalOutputCommitted,
    ExecutionOutcome? Settlement);

public sealed record MessageRef(
    Guid RunId, Guid MessageId, MessageRole Role, long HistoryRevision);

public enum MessageRole { User, Assistant }

// Original UTF-8 byte offset within an immutable persisted segment.
public sealed record MessageContentCursor(MessageRef Message, long SegmentIndex, int Utf8Offset);

// IsClosed means no more message bytes can arrive, not that execution or cleanup succeeded.
public sealed record MessageContentPage(
    MessageContentCursor From, string Text, MessageContentCursor Next,
    MessageContentCursor AvailableThrough, bool HasMore,
    bool IsClosed, bool FinalOutputCommitted);

public sealed class HistoryRevisionConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException($"History revision {expectedRevision} no longer matches current revision {actualRevision}.")
{
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}
