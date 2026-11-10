namespace Urd.Core.MarkdownRendering;

internal sealed class MarkdownRenderIndex {
  internal const string PipelineVersion = "markdig-1.6.0-commonmark-gfm-tables-display-1";
  readonly MarkdownTextPiece[] pieces;
  readonly MarkdownRenderContext[] contexts;
  readonly MarkdownValueFragment[] values;
  readonly (int Start, int Piece)[] anchors;

  internal Guid Generation { get; } = Guid.NewGuid();
  internal int SourceUtf8Bytes { get; }
  internal int PieceCount => pieces.Length;
  internal int ContextCount => contexts.Length;

  // Retention accounting, not exact CLR heap size. Charge every array, record,
  // reference, UTF-16 string (including variable metadata), and anchor. Shared
  // strings are deliberately charged per occurrence rather than undercounted.
  internal long AccountedBytes { get; }

  internal MarkdownRenderIndex(int sourceUtf8Bytes, MarkdownTextPiece[] pieces,
      MarkdownRenderContext[] contexts, MarkdownValueFragment[] values) {
    SourceUtf8Bytes = sourceUtf8Bytes;
    this.pieces = pieces;
    this.contexts = contexts;
    this.values = values;
    anchors = pieces.Select(p => (Start: p.Source.Start, Piece: p.Id))
      .OrderBy(p => p.Start).ThenBy(p => p.Piece).ToArray();
    AccountedBytes = 128L + 4 * 24
        + contexts.Length * (96L + 8)
        + pieces.Sum(piece => 80L + 8 + 24 + 2L * piece.Text.Length)
        + values.Sum(value => 40L + 8 + 24 + 2L * value.Text.Length)
        + anchors.Length * 8L;
  }

  internal MarkdownTextPiece Piece(int index) => pieces[index];
  internal MarkdownRenderContext Context(int id) => contexts[id];
  internal MarkdownValueFragment Value(int index) => values[index];

  // A source anchor resolves to the first fragment at the nearest preceding
  // source start. Transformed fragments share a containing range, not fake offsets.
  internal MarkdownRenderCursor Locate(int sourceUtf8Offset) {
    if (sourceUtf8Offset < 0 || sourceUtf8Offset > SourceUtf8Bytes)
      throw new ArgumentOutOfRangeException(nameof(sourceUtf8Offset));
    if (anchors.Length == 0) return new(Generation, 0);
    var low = 0;
    var high = anchors.Length;
    while (low < high) {
      var middle = low + ((high - low) / 2);
      if (anchors[middle].Start < sourceUtf8Offset) low = middle + 1;
      else high = middle;
    }
    if (low == anchors.Length || anchors[low].Start != sourceUtf8Offset) low = Math.Max(0, low - 1);
    var start = anchors[low].Start;
    high = low;
    low = 0;
    while (low < high) {
      var middle = low + ((high - low) / 2);
      if (anchors[middle].Start < start) low = middle + 1;
      else high = middle;
    }
    return new(Generation, anchors[low].Piece);
  }
}
