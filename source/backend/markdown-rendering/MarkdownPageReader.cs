using System.Text.Json;

namespace Urd.Core.MarkdownRendering;

// Bounded indexed reads only: no tree walk, whole-projection scan or source read.
// The serialization options are part of this internal byte-ceiling contract.
internal static class MarkdownPageReader {
  internal static MarkdownPiecePage Read(MarkdownRenderIndex index, MarkdownRenderCursor cursor, MarkdownPageLimits? limits = null) {
    limits ??= new();
    limits.Validate();
    Validate(index, cursor, index.PieceCount);
    var pieces = new List<MarkdownTextPiece>();
    var next = cursor.Index;
    // Max-width Next and false (longer than true) conservatively cover the envelope.
    var bytes = Size(new MarkdownPiecePage(index.Generation, cursor.Index, int.MaxValue, false, []));
    while (next < index.PieceCount && pieces.Count < limits.Records) {
      var size = index.PieceUtf8Bytes(next) + (pieces.Count == 0 ? 0 : 1);
      if (bytes + size > limits.Utf8Bytes) break;
      pieces.Add(index.Piece(next));
      bytes += size;
      next++;
    }
    EnsureAdvance(cursor.Index, next, index.PieceCount);
    return new(index.Generation, cursor.Index, next, next < index.PieceCount, pieces.AsReadOnly());
  }

  internal static MarkdownPiecePage ReadBefore(MarkdownRenderIndex index,
      MarkdownRenderCursor exclusiveEndCursor, MarkdownPageLimits? limits = null) {
    limits ??= new();
    limits.Validate();
    Validate(index, exclusiveEndCursor, index.PieceCount);
    var pieces = new List<MarkdownTextPiece>();
    var from = exclusiveEndCursor.Index;
    var hasMore = exclusiveEndCursor.Index < index.PieceCount;
    // From is not known until the reverse walk completes; reserve its widest JSON form.
    var bytes = Size(new MarkdownPiecePage(index.Generation, int.MaxValue, exclusiveEndCursor.Index, hasMore, []));
    while (from > 0 && pieces.Count < limits.Records) {
      var size = index.PieceUtf8Bytes(from - 1) + (pieces.Count == 0 ? 0 : 1);
      if (bytes + size > limits.Utf8Bytes) break;
      pieces.Add(index.Piece(from - 1));
      bytes += size;
      from--;
    }
    if (from == exclusiveEndCursor.Index && exclusiveEndCursor.Index > 0)
      throw new InvalidOperationException("Markdown record cannot fit within the page limits.");
    pieces.Reverse();
    return new(index.Generation, from, exclusiveEndCursor.Index, hasMore, pieces.AsReadOnly());
  }

  // Fetch requested constant-size context descriptors in request order. Metadata values
  // remain an explicit follow-up read through ReadContext.
  internal static MarkdownContextDescriptorPage ReadContexts(MarkdownRenderIndex index,
      IReadOnlyList<int> contextIds, MarkdownRenderCursor cursor, MarkdownPageLimits? limits = null) {
    ArgumentNullException.ThrowIfNull(contextIds);
    limits ??= new();
    limits.Validate();
    if (contextIds.Count > 256) throw new ArgumentOutOfRangeException(nameof(contextIds), "At most 256 context IDs may be requested.");
    var distinct = new HashSet<int>();
    foreach (var contextId in contextIds) {
      if (contextId < 0 || contextId >= index.ContextCount)
        throw new ArgumentOutOfRangeException(nameof(contextIds), contextId, "Context ID is outside this derivation.");
      if (!distinct.Add(contextId)) throw new ArgumentException("Context IDs must be distinct.", nameof(contextIds));
    }
    Validate(index, cursor, contextIds.Count);
    var contexts = new List<MarkdownRenderContext>();
    var next = cursor.Index;
    // Reserve the widest Next and HasMore envelope while bounding the actual JSON page.
    var bytes = Size(new MarkdownContextDescriptorPage(index.Generation, cursor.Index, int.MaxValue, false, []));
    while (next < contextIds.Count && contexts.Count < limits.Records) {
      var context = index.Context(contextIds[next]);
      var size = Size(context) + (contexts.Count == 0 ? 0 : 1);
      if (bytes + size > limits.Utf8Bytes) break;
      contexts.Add(context);
      bytes += size;
      next++;
    }
    EnsureAdvance(cursor.Index, next, contextIds.Count);
    return new(index.Generation, cursor.Index, next, next < contextIds.Count, contexts.AsReadOnly());
  }

  // Fetch a single constant-size context by ID and bounded variable metadata.
  // Parent, sibling, header-cell and piece ranges are explicit follow-up addresses.
  internal static MarkdownContextPage ReadContext(MarkdownRenderIndex index, int contextId,
      MarkdownRenderCursor cursor, MarkdownPageLimits? limits = null) {
    limits ??= new();
    limits.Validate();
    if (contextId < 0 || contextId >= index.ContextCount) throw new ArgumentOutOfRangeException(nameof(contextId));
    var context = index.Context(contextId);
    Validate(index, cursor, context.ValueCount);
    var values = new List<MarkdownValueFragment>();
    var next = cursor.Index;
    var bytes = Size(new MarkdownContextPage(index.Generation, context, cursor.Index, int.MaxValue, false, []));
    while (next < context.ValueCount && values.Count + 1 < limits.Records) {
      var value = index.Value(context.FirstValue + next);
      var size = Size(value) + (values.Count == 0 ? 0 : 1);
      if (bytes + size > limits.Utf8Bytes) break;
      values.Add(value);
      bytes += size;
      next++;
    }
    EnsureAdvance(cursor.Index, next, context.ValueCount);
    return new(index.Generation, context, cursor.Index, next, next < context.ValueCount, values.AsReadOnly());
  }

  internal static int MeasurePiece(MarkdownTextPiece piece) => Size(piece);

  static int Size<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, MarkdownRenderJson.Options).Length;

  static void Validate(MarkdownRenderIndex index, MarkdownRenderCursor cursor, int count) {
    if (cursor.Generation != index.Generation) throw new ArgumentException("Markdown cursor belongs to a different derivation.", nameof(cursor));
    if (cursor.Index < 0 || cursor.Index > count) throw new ArgumentOutOfRangeException(nameof(cursor));
  }

  static void EnsureAdvance(int from, int next, int count) {
    if (from == next && from < count)
      throw new InvalidOperationException("Markdown record cannot fit within the page limits.");
  }
}
