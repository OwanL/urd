using System.Text.Json;

namespace Urd.Core.MarkdownRendering;

// Bounded indexed reads only: no tree walk, whole-projection scan or source read.
// The serialization options are part of this internal byte-ceiling contract.
internal static class MarkdownPageReader {
  internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

  internal static MarkdownPiecePage Read(MarkdownRenderIndex index, MarkdownRenderCursor cursor, MarkdownPageLimits? limits = null) {
    limits ??= new();
    limits.Validate();
    Validate(index, cursor, index.PieceCount);
    var pieces = new List<MarkdownTextPiece>();
    var next = cursor.Index;
    // Max-width Next and false (longer than true) conservatively cover the envelope.
    var bytes = Size(new MarkdownPiecePage(index.Generation, cursor.Index, int.MaxValue, false, []));
    while (next < index.PieceCount && pieces.Count < limits.Records) {
      var piece = index.Piece(next);
      var size = Size(piece) + (pieces.Count == 0 ? 0 : 1);
      if (bytes + size > limits.Utf8Bytes) break;
      pieces.Add(piece);
      bytes += size;
      next++;
    }
    EnsureAdvance(cursor.Index, next, index.PieceCount);
    return new(index.Generation, cursor.Index, next, next < index.PieceCount, pieces.AsReadOnly());
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

  static int Size<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Length;

  static void Validate(MarkdownRenderIndex index, MarkdownRenderCursor cursor, int count) {
    if (cursor.Generation != index.Generation) throw new ArgumentException("Markdown cursor belongs to a different derivation.", nameof(cursor));
    if (cursor.Index < 0 || cursor.Index > count) throw new ArgumentOutOfRangeException(nameof(cursor));
  }

  static void EnsureAdvance(int from, int next, int count) {
    if (from == next && from < count)
      throw new InvalidOperationException("Markdown record cannot fit within the page limits.");
  }
}
