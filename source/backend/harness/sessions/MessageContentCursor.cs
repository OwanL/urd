namespace Urd.Core.Sessions;

// Position in a message: a persisted segment and an original UTF-8 byte offset within it.
public sealed record MessageContentCursor(MessageRef Message, long SegmentIndex, int Utf8Offset);
