namespace Urd.Core.Display;

// Disposable, generation-local structure. Original source belongs to the caller,
// not this index. IDs/cursors must never be persisted as source anchors.
internal enum DisplayKind {
  Document, Paragraph, Heading, Quote, List, Item, Code, CodeLine,
  Table, Row, Cell, Emphasis, Strong, InlineCode, Link, ThematicBreak
}

internal enum DisplayTextKind { Text, Literal, Code, SoftBreak, HardBreak, ThematicBreak }
internal enum DisplayMapping { Exact, Containing, Container }
internal enum DisplayValueKind { Destination, Title, Language, FenceInfo, OrderedStart }
internal enum DisplayAlignment { None, Left, Center, Right }

internal readonly record struct DisplaySourceRange(int Start, int End);
internal readonly record struct DisplayCursor(Guid Generation, int Index);

// Parent/child/sibling links provide ancestry and header access without repeating
// arbitrarily large ancestor chains, headers or column arrays in a page.
// Source is always a structural containing range, never a character mapping.
internal sealed record DisplayContext(
    int Id, DisplayKind Kind, int Parent, int FirstChild, int NextSibling,
    DisplaySourceRange Source, int FirstPiece, int PieceCount,
    int FirstValue, int ValueCount, int Position = 0, int Level = 0,
    bool Ordered = false, bool Loose = false, bool Header = false,
    DisplayAlignment Alignment = DisplayAlignment.None, int HeaderCell = -1);

internal sealed record DisplayPiece(
    int Id, int Context, DisplayTextKind Kind, string Text,
    DisplaySourceRange Source, DisplayMapping Mapping,
    int Part, bool Continued);

// Variable metadata is independently fragmented, even a huge URL or fence info.
internal sealed record DisplayValue(
    DisplayValueKind Kind, string Text, int Part, bool Continued);

internal sealed record DisplayPage(
    Guid Generation, int From, int Next, bool HasMore,
    IReadOnlyList<DisplayPiece> Pieces);

internal sealed record DisplayContextPage(
    Guid Generation, DisplayContext Context, int From, int Next, bool HasMore,
    IReadOnlyList<DisplayValue> Values);

internal sealed record DisplayPageLimits(int Utf8Bytes = 16 * 1024, int Records = 256) {
  internal void Validate() {
    // A fixed descriptor and one worst-case JSON-escaped fragment always fit.
    if (Utf8Bytes < 4096 || Utf8Bytes > 1024 * 1024)
      throw new ArgumentOutOfRangeException(nameof(Utf8Bytes));
    if (Records < 2 || Records > 4096)
      throw new ArgumentOutOfRangeException(nameof(Records));
  }
}
