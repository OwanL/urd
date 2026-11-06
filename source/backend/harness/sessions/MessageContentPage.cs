namespace Urd.Core.Sessions;

// One bounded page of message text. IsClosed means no more message bytes can
// arrive, not that execution or cleanup succeeded.
public sealed record MessageContentPage(
    MessageContentCursor From, string Text, MessageContentCursor Next,
    MessageContentCursor AvailableThrough, bool HasMore,
    bool IsClosed, bool FinalOutputCommitted);
