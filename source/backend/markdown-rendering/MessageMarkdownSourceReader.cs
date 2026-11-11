using System.Buffers;
using System.Text;
using Urd.Core.Sessions;
using Urd.Core.Storage;

namespace Urd.Core.MarkdownRendering;

// Deliberately synchronous and unscheduled. Never call under a session/database
// gate. Storage releases its lock after each bounded read, before any decoding.
internal static class MessageMarkdownSourceReader {
  internal static MessageMarkdownSource Read(MessageContentReader reader, MessageContentExtent extent,
      CancellationToken cancellation = default) {
    var source = new StringBuilder();
    var segments = new List<MarkdownSourceSegment>();
    MessageContentCursor cursor = extent.From;
    int bytes = 0;
    while (cursor.SegmentIndex < extent.AvailableThrough.SegmentIndex) {
      cancellation.ThrowIfCancellationRequested();
      if (cursor.Utf8Offset == 0)
        segments.Add(new(bytes, cursor.SegmentIndex));
      MessageContentSegment segment = reader.ReadSourceSegment(extent, cursor);
      int consumed = Append(segment.Prefix, source);
      bytes = checked(bytes + consumed);
      int offset = checked(cursor.Utf8Offset + consumed);
      if (offset == segment.ByteLength) {
        cursor = new(cursor.Message, cursor.SegmentIndex + 1, 0);
        continue;
      }
      if (consumed == 0)
        throw new InvalidDataException("A committed message segment could not be read.");
      cursor = cursor with { Utf8Offset = offset };
    }

    cancellation.ThrowIfCancellationRequested();
    return new(extent.SessionId, extent.From.Message, extent.From, extent.AvailableThrough,
        bytes, extent.IsClosed, extent.FinalOutputCommitted, source.ToString(), segments.AsReadOnly());
  }

  // Three lookahead bytes from storage complete any scalar starting before the
  // byte budget. Decode only here, outside the database transaction and lock.
  static int Append(byte[] prefix, StringBuilder source) {
    int consumed = 0;
    Span<char> scalar = stackalloc char[2];
    while (consumed < prefix.Length && consumed < StorageText.MaximumBytes) {
      if (Rune.DecodeFromUtf8(prefix.AsSpan(consumed), out Rune rune, out int scalarBytes) != OperationStatus.Done)
        throw new InvalidDataException("A message segment contains invalid UTF-8.");
      if (scalarBytes > StorageText.MaximumBytes - consumed)
        break;
      source.Append(scalar[..rune.EncodeToUtf16(scalar)]);
      consumed += scalarBytes;
    }
    return consumed;
  }
}
