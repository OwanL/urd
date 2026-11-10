namespace Urd.Core.MarkdownRendering;

// Disposable, generation-local structure. Original source belongs to the caller,
// not this index. IDs/cursors must never be persisted as source anchors.
internal enum MarkdownElementKind {
  Document, Paragraph, Heading, Quote, List, Item, Code, CodeLine,
  Table, Row, Cell, Emphasis, Strong, InlineCode, Link, ThematicBreak
}

internal enum MarkdownTextKind { Text, Literal, Code, SoftBreak, HardBreak, ThematicBreak }
internal enum MarkdownSourceMapping { Exact, Containing, Container }
internal enum MarkdownValueKind { Destination, Title, Language, FenceInfo, OrderedStart }
internal enum MarkdownTableAlignment { None, Left, Center, Right }

internal readonly record struct MarkdownSourceRange(int Start, int End);
internal readonly record struct MarkdownRenderCursor(Guid Generation, int Index);

// Parent/child/sibling links provide ancestry and header access without repeating
// arbitrarily large ancestor chains, headers or column arrays in a page.
// Source is always a structural containing range, never a character mapping.
internal sealed record MarkdownRenderContext(
    int Id, MarkdownElementKind Kind, int Parent, int FirstChild, int NextSibling,
    MarkdownSourceRange Source, int FirstPiece, int PieceCount,
    int FirstValue, int ValueCount, int Position = 0, int Level = 0,
    bool Ordered = false, bool Loose = false, bool Header = false,
    MarkdownTableAlignment Alignment = MarkdownTableAlignment.None, int HeaderCell = -1);

internal sealed record MarkdownTextPiece(
    int Id, int Context, MarkdownTextKind Kind, string Text,
    MarkdownSourceRange Source, MarkdownSourceMapping Mapping,
    int Part, bool Continued);

// Variable metadata is independently fragmented, even a huge URL or fence info.
internal sealed record MarkdownValueFragment(
    MarkdownValueKind Kind, string Text, int Part, bool Continued);

internal sealed record MarkdownPiecePage(
    Guid Generation, int From, int Next, bool HasMore,
    IReadOnlyList<MarkdownTextPiece> Pieces);

internal sealed record MarkdownContextPage(
    Guid Generation, MarkdownRenderContext Context, int From, int Next, bool HasMore,
    IReadOnlyList<MarkdownValueFragment> Values);

internal sealed record MarkdownPageLimits(int Utf8Bytes = 16 * 1024, int Records = 256) {
  internal void Validate() {
    // A fixed descriptor and one worst-case JSON-escaped fragment always fit.
    if (Utf8Bytes < 4096 || Utf8Bytes > 1024 * 1024)
      throw new ArgumentOutOfRangeException(nameof(Utf8Bytes));
    if (Records < 2 || Records > 4096)
      throw new ArgumentOutOfRangeException(nameof(Records));
  }
}
