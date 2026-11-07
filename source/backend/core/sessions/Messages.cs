using Urd.Core.Execution;

namespace Urd.Core.Sessions;

// Identifies one user or assistant message within a history revision.
public sealed record MessageRef(
    Guid ExecutionId, Guid MessageId, MessageRole Role, long HistoryRevision);

// Who wrote a message.
public enum MessageRole { User, Assistant }

// Short prefix of one message in a snapshot, with a cursor for reading the rest.
public sealed record MessagePreview(
    MessageRef Message, string Preview, bool Truncated,
    MessageContentCursor AvailableThrough, bool FinalOutputCommitted,
    ExecutionOutcome? Settlement);

// Position in a message: a persisted segment and an original UTF-8 byte offset within it.
public sealed record MessageContentCursor(MessageRef Message, long SegmentIndex, int Utf8Offset);

// One bounded page of message text. IsClosed means no more message bytes can
// arrive, not that execution or cleanup succeeded.
public sealed record MessageContentPage(
    MessageContentCursor From, string Text, MessageContentCursor Next,
    MessageContentCursor AvailableThrough, bool HasMore,
    bool IsClosed, bool FinalOutputCommitted);
