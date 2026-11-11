using System.Buffers;
using System.Text;
using Urd.Core.Sessions;
using Urd.Core.Storage;

namespace Urd.Core.MarkdownRendering;

// Captures and revalidates guarded content bounds; assembly is unscheduled and
// outside the session/database gates. The Markdown scheduler owns worker admission.
internal static class MessageMarkdownSourceReader {
  internal static async Task<MessageMarkdownSource> ReadAsync(SessionBase session, MessageMarkdownKey key,
      CancellationToken cancellation = default) {
    if (session.Id != key.SessionId)
      throw new ArgumentException("The session does not match the markdown key.", nameof(key));
    SessionTranscript transcript = session.Transcript;
    MessageContentExtent extent = await transcript.CaptureContentExtentAsync(key.Message, key.TextFacts, cancellation);
    MessageMarkdownSource source = Read(cursor => transcript.ReadContentSegment(extent, cursor), extent, cancellation);
    await transcript.ValidateContentExtentAsync(extent, cancellation);
    return source;
  }

  internal static MessageMarkdownSource Read(MessageContentReader reader, MessageContentExtent extent,
      CancellationToken cancellation = default) =>
      Read(cursor => reader.ReadSourceSegment(extent, cursor), extent, cancellation);

  private static MessageMarkdownSource Read(Func<MessageContentCursor, MessageContentSegment> readSegment,
      MessageContentExtent extent, CancellationToken cancellation) {
    var source = new StringBuilder();
    var segments = new List<MarkdownSourceSegment>();
    MessageContentCursor cursor = extent.From;
    int bytes = 0;
    while (cursor.SegmentIndex < extent.AvailableThrough.SegmentIndex) {
      cancellation.ThrowIfCancellationRequested();
      if (cursor.Utf8Offset == 0)
        segments.Add(new(bytes, cursor.SegmentIndex));
      MessageContentSegment segment = readSegment(cursor);
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
