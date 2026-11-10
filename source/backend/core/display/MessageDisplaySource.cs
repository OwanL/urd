using Urd.Core.Sessions;

namespace Urd.Core.Display;

// Exact, unnormalized source and its immutable captured content extent. Unlike a
// display index this owns source text, so its lifetime belongs to the future scheduler.
internal sealed record MessageDisplaySource(
    Guid SessionId, MessageRef Message, MessageContentCursor From, MessageContentCursor AvailableThrough,
    int Utf8Bytes, bool IsClosed, bool FinalOutputCommitted, string Source,
    IReadOnlyList<DisplaySourceSegment> Segments) {
  internal long HistoryRevision => Message.HistoryRevision;

  // Display ranges are scalar-aligned global UTF-8 offsets. At a segment boundary
  // prefer the following segment; at EOF always return the captured end cursor.
  internal MessageContentCursor ContentCursor(int utf8Offset) {
    if (utf8Offset < 0 || utf8Offset > Utf8Bytes)
      throw new ArgumentOutOfRangeException(nameof(utf8Offset));
    if (utf8Offset == Utf8Bytes)
      return AvailableThrough;

    int low = 0, high = Segments.Count;
    while (low < high) {
      int middle = low + (high - low) / 2;
      if (Segments[middle].Utf8Start <= utf8Offset)
        low = middle + 1;
      else
        high = middle;
    }
    DisplaySourceSegment segment = Segments[low - 1];
    return new(Message, segment.SegmentIndex, utf8Offset - segment.Utf8Start);
  }

  internal (MessageContentCursor From, MessageContentCursor Through) ContentRange(DisplaySourceRange range) {
    if (range.End < range.Start)
      throw new ArgumentException("The source range must be ordered.", nameof(range));
    return (ContentCursor(range.Start), ContentCursor(range.End));
  }
}

// One start per original segment, including empty segments. Offsets are ordered,
// not necessarily distinct. No per-character map or copied segment text is retained.
internal sealed record DisplaySourceSegment(int Utf8Start, long SegmentIndex);
