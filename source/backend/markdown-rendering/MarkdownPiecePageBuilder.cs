namespace Urd.Core.MarkdownRendering;

// Packs a contiguous piece range with its descriptor closure when it fits.
internal sealed class MarkdownPiecePageBuilder(MarkdownRenderIndex index, MarkdownPageLimits limits, int envelopeBytes) {
  readonly List<MarkdownTextPiece> pieces = [];
  readonly List<MarkdownRenderContext> contexts = [];
  readonly HashSet<int> included = [];
  readonly int dependencyByteLimit = (limits.Utf8Bytes - envelopeBytes) / 2;
  int bytes = envelopeBytes;
  bool bundleComplete = true;

  internal bool TryAdd(int pieceIndex) {
    var pieceBytes = index.PieceUtf8Bytes(pieceIndex) + (pieces.Count == 0 ? 0 : 1);
    if (!Fits(pieceBytes)) return false;
    var piece = index.Piece(pieceIndex);
    bytes += pieceBytes;
    pieces.Add(piece);
    if (!bundleComplete) return true;

    var contextCount = contexts.Count;
    var bytesWithPiece = bytes;
    if (TryAddAncestry(piece.Context) && TryAddTableLinks(contextCount)
        && FitsDependencyGroup(contexts.Count - contextCount, bytes - bytesWithPiece)) return true;

    // Never keep hints for a rejected piece. If the first piece needs an unusually
    // large closure, pack pieces first and use any space left over for partial hints.
    RemoveContextsFrom(contextCount);
    if (pieces.Count == 1) {
      bytes = bytesWithPiece;
      bundleComplete = false;
      return true;
    }
    bytes = bytesWithPiece - pieceBytes;
    pieces.RemoveAt(pieces.Count - 1);
    return false;
  }

  internal MarkdownPiecePage Build(int from, int next, bool reverse = false) {
    if (!bundleComplete) AddPartialBundle();
    if (reverse) pieces.Reverse();
    return new(index.Generation, from, next, next < index.PieceCount, pieces.AsReadOnly()) {
      Contexts = contexts.AsReadOnly()
    };
  }

  void AddPartialBundle() {
    // Seed each visible context before following parents or detached table links.
    for (var i = 0; i < pieces.Count; i++)
      if (!TryAddContext(pieces[i].Context)) return;
    for (var i = 0; i < contexts.Count; i++)
      if (!TryAddAncestry(contexts[i].Parent)) return;
    TryAddTableLinks(0);
  }

  bool TryAddAncestry(int id) {
    while (id >= 0 && !included.Contains(id)) {
      if (!TryAddContext(id)) return false;
      id = contexts[^1].Parent;
    }
    return true;
  }

  bool TryAddTableLinks(int from) {
    // Only expand newly included descriptors. For a visible row, this reaches all
    // its cells (including empty ones), the referenced headers and their row.
    // Every new descriptor consumes budget, so even wide rows are bounded reads.
    for (var i = from; i < contexts.Count; i++) {
      var context = contexts[i];
      var fits = context.Kind switch {
        MarkdownElementKind.Row => TryAddAncestry(context.FirstChild),
        MarkdownElementKind.Cell => TryAddAncestry(context.HeaderCell) && TryAddAncestry(context.NextSibling),
        _ => true
      };
      if (!fits) return false;
    }
    return true;
  }

  bool TryAddContext(int id) {
    if (included.Contains(id)) return true;
    if (!Fits(0)) return false;
    var context = index.Context(id);
    var contextBytes = MarkdownPageReader.MeasureContext(context) + (contexts.Count == 0 ? 0 : 1);
    if (!Fits(contextBytes)) return false;
    contexts.Add(context);
    included.Add(id);
    bytes += contextBytes;
    return true;
  }

  void RemoveContextsFrom(int from) {
    for (var i = from; i < contexts.Count; i++) included.Remove(contexts[i].Id);
    contexts.RemoveRange(from, contexts.Count - from);
  }

  bool Fits(int recordBytes) => pieces.Count + contexts.Count < limits.Records && bytes + recordBytes <= limits.Utf8Bytes;

  // Do not repeatedly spend most of a page on one wide row or deep ancestor chain
  // while reading a long run of pieces in the same context.
  bool FitsDependencyGroup(int records, int additionalBytes) =>
      records <= limits.Records / 2 && additionalBytes <= dependencyByteLimit;
}
