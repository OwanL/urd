using System.Text.Json;

namespace Urd.Core.Display;

// Bounded indexed reads only: no tree walk, whole-projection scan or source read.
// The serialization options are part of this internal byte-ceiling contract.
internal static class MessageDisplayReader {
  internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

  internal static DisplayPage Read(MessageDisplayIndex display, DisplayCursor cursor, DisplayPageLimits? limits = null) {
    limits ??= new();
    limits.Validate();
    Validate(display, cursor, display.PieceCount);
    var pieces = new List<DisplayPiece>();
    var next = cursor.Index;
    // Max-width Next and false (longer than true) conservatively cover the envelope.
    var bytes = Size(new DisplayPage(display.Generation, cursor.Index, int.MaxValue, false, []));
    while (next < display.PieceCount && pieces.Count < limits.Records) {
      var piece = display.Piece(next);
      var size = Size(piece) + (pieces.Count == 0 ? 0 : 1);
      if (bytes + size > limits.Utf8Bytes) break;
      pieces.Add(piece);
      bytes += size;
      next++;
    }
    EnsureAdvance(cursor.Index, next, display.PieceCount);
    return new(display.Generation, cursor.Index, next, next < display.PieceCount, pieces.AsReadOnly());
  }

  // Fetch a single constant-size context by ID and bounded variable metadata.
  // Parent, sibling, header-cell and piece ranges are explicit follow-up addresses.
  internal static DisplayContextPage ReadContext(MessageDisplayIndex display, int contextId,
      DisplayCursor cursor, DisplayPageLimits? limits = null) {
    limits ??= new();
    limits.Validate();
    if (contextId < 0 || contextId >= display.ContextCount) throw new ArgumentOutOfRangeException(nameof(contextId));
    var context = display.Context(contextId);
    Validate(display, cursor, context.ValueCount);
    var values = new List<DisplayValue>();
    var next = cursor.Index;
    var bytes = Size(new DisplayContextPage(display.Generation, context, cursor.Index, int.MaxValue, false, []));
    while (next < context.ValueCount && values.Count + 1 < limits.Records) {
      var value = display.Value(context.FirstValue + next);
      var size = Size(value) + (values.Count == 0 ? 0 : 1);
      if (bytes + size > limits.Utf8Bytes) break;
      values.Add(value);
      bytes += size;
      next++;
    }
    EnsureAdvance(cursor.Index, next, context.ValueCount);
    return new(display.Generation, context, cursor.Index, next, next < context.ValueCount, values.AsReadOnly());
  }

  static int Size<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Length;

  static void Validate(MessageDisplayIndex display, DisplayCursor cursor, int count) {
    if (cursor.Generation != display.Generation) throw new ArgumentException("Display cursor belongs to a different derivation.", nameof(cursor));
    if (cursor.Index < 0 || cursor.Index > count) throw new ArgumentOutOfRangeException(nameof(cursor));
  }

  static void EnsureAdvance(int from, int next, int count) {
    if (from == next && from < count)
      throw new InvalidOperationException("Display record cannot fit within the page limits.");
  }
}
