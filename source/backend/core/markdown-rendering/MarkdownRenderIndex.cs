namespace Urd.Core.MarkdownRendering;

internal sealed class MarkdownRenderIndex {
  internal const string PipelineVersion = "markdig-1.6.0-commonmark-gfm-tables-display-1";
  readonly MarkdownTextPiece[] pieces;
  readonly int[] pieceUtf8Bytes;
  readonly MarkdownRenderContext[] contexts;
  readonly int[] childOffsets;
  readonly int[] childIds;
  readonly MarkdownValueFragment[] values;
  readonly (int Start, int Piece)[] anchors;

  internal Guid Generation { get; } = Guid.NewGuid();
  internal int SourceUtf8Bytes { get; }
  internal int PieceCount => pieces.Length;
  internal int ContextCount => contexts.Length;
  internal MarkdownRenderManifest Manifest { get; }

  // Retention accounting, not exact CLR heap size. Charge every array, record,
  // reference, UTF-16 string (including variable metadata), and anchor. Shared
  // strings are deliberately charged per occurrence rather than undercounted.
  internal long AccountedBytes { get; }

  internal MarkdownRenderIndex(int sourceUtf8Bytes, MarkdownTextPiece[] pieces,
      MarkdownRenderContext[] contexts, MarkdownValueFragment[] values) {
    SourceUtf8Bytes = sourceUtf8Bytes;
    this.pieces = pieces;
    pieceUtf8Bytes = pieces.Select(MarkdownPageReader.MeasurePiece).ToArray();
    this.contexts = contexts;
    this.values = values;
    (childOffsets, childIds) = ValidateContexts(contexts);
    Manifest = new(Generation, sourceUtf8Bytes, pieces.Length, contexts.Length);
    anchors = pieces.Select(p => (Start: p.Source.Start, Piece: p.Id))
      .OrderBy(p => p.Start).ThenBy(p => p.Piece).ToArray();
    AccountedBytes = 160L + 7 * 24
        + contexts.Length * (100L + 8)
        + childOffsets.Length * 4L + childIds.Length * 4L
        + pieces.Sum(piece => 80L + 8 + 24 + 4 + 2L * piece.Text.Length)
        + values.Sum(value => 40L + 8 + 24 + 2L * value.Text.Length)
        + anchors.Length * 8L;
  }

  internal MarkdownTextPiece Piece(int index) => pieces[index];
  internal int PieceUtf8Bytes(int index) => pieceUtf8Bytes[index];
  internal MarkdownRenderContext Context(int id) => contexts[id];
  internal MarkdownValueFragment Value(int index) => values[index];

  internal MarkdownRenderCursor LocateChild(int contextId, int childIndex) {
    if (contextId < 0 || contextId >= contexts.Length)
      throw new ArgumentOutOfRangeException(nameof(contextId));
    var context = contexts[contextId];
    if (childIndex < 0 || childIndex >= context.ChildCount)
      throw new ArgumentOutOfRangeException(nameof(childIndex));
    var childId = childIds[childOffsets[contextId] + childIndex];
    return new(Generation, contexts[childId].FirstPiece);
  }

  // A source anchor resolves to the first fragment at the nearest preceding
  // source start. Transformed fragments share a containing range, not fake offsets.
  static (int[] Offsets, int[] ChildIds) ValidateContexts(MarkdownRenderContext[] contexts) {
    if (contexts.Length == 0) throw new InvalidOperationException("A Markdown index must have a document context.");
    var linked = new bool[contexts.Length];
    var childOffsets = new int[contexts.Length + 1];
    var childIds = new List<int>(contexts.Length - 1);
    for (var id = 0; id < contexts.Length; id++) {
      var context = contexts[id];
      childOffsets[id] = childIds.Count;
      if (context.Id != id || context.ChildCount < 0 || context.ChildCount > contexts.Length
          || (id == 0 && context.Parent != -1) || (id > 0 && (context.Parent < 0 || context.Parent >= id)))
        throw new InvalidOperationException("Markdown context identity or child count is invalid.");
      if ((context.ChildCount == 0) != (context.FirstChild == -1))
        throw new InvalidOperationException("Markdown context child links do not match its child count.");
      var childId = context.FirstChild;
      for (var childIndex = 0; childIndex < context.ChildCount; childIndex++) {
        if (childId <= id || childId >= contexts.Length || linked[childId])
          throw new InvalidOperationException("Markdown context child links are invalid.");
        var child = contexts[childId];
        if (child.Parent != id) throw new InvalidOperationException("Markdown context parent and child links disagree.");
        linked[childId] = true;
        childIds.Add(childId);
        childId = child.NextSibling;
      }
      if (childId != -1) throw new InvalidOperationException("Markdown context sibling links exceed its child count.");
      childOffsets[id + 1] = childIds.Count;
    }
    for (var id = 1; id < contexts.Length; id++)
      if (!linked[id]) throw new InvalidOperationException("Markdown context is missing from its parent's child links.");
    return (childOffsets, childIds.ToArray());
  }

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
