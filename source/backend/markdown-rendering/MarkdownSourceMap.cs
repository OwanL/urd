using System.Buffers;
using System.Text;

namespace Urd.Core.MarkdownRendering;

// One ordered scalar walk, not a prefix byte count for every parser node.
// The temporary map is released after projection; no source string is retained.
internal static class MarkdownSourceMap {
  internal static Dictionary<int, int> Convert(string source, IEnumerable<int> positions) {
    var boundaries = positions.Append(0).Append(source.Length).Distinct().Order().ToArray();
    var result = new Dictionary<int, int>(boundaries.Length);
    var position = 0;
    var bytes = 0;
    foreach (var boundary in boundaries) {
      if (boundary < 0 || boundary > source.Length)
        throw new InvalidOperationException("Parser source boundary is outside the source.");
      while (position < boundary) {
        var status = Rune.DecodeFromUtf16(source.AsSpan(position), out var rune, out var consumed);
        if (status != OperationStatus.Done)
          throw new ArgumentException("Markdown source must contain valid Unicode scalars.", nameof(source));
        position += consumed;
        bytes = checked(bytes + rune.Utf8SequenceLength);
      }
      if (position != boundary)
        throw new InvalidOperationException("Parser source boundary splits a Unicode scalar.");
      result.Add(boundary, bytes);
    }
    return result;
  }
}
