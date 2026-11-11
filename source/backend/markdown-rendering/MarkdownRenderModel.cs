namespace Urd.Core.MarkdownRendering;

// These numbers are deliberate wire identifiers and must remain stable.
// Disposable, generation-local structure. Original source belongs to the caller,
// not this index. IDs/cursors must never be persisted as source anchors.
public enum MarkdownElementKind {
  Document = 0, Paragraph = 1, Heading = 2, Quote = 3, List = 4, Item = 5, Code = 6, CodeLine = 7,
  Table = 8, Row = 9, Cell = 10, Emphasis = 11, Strong = 12, InlineCode = 13, Link = 14, ThematicBreak = 15
}

public enum MarkdownTextKind { Text = 0, Literal = 1, Code = 2, SoftBreak = 3, HardBreak = 4, ThematicBreak = 5 }
public enum MarkdownSourceMapping { Exact = 0, Containing = 1, Container = 2 }
public enum MarkdownValueKind { Destination = 0, Title = 1, Language = 2, FenceInfo = 3, OrderedStart = 4 }
public enum MarkdownTableAlignment { None = 0, Left = 1, Center = 2, Right = 3 }

public readonly record struct MarkdownSourceRange(int Start, int End);
public readonly record struct MarkdownRenderCursor(Guid Generation, int Index);
public readonly record struct MarkdownRenderManifest(Guid Generation, int SourceUtf8Bytes, int PieceCount, int ContextCount);

// Parent/child/sibling links provide ancestry and header access without repeating
// arbitrarily large ancestor chains, headers or column arrays in a page.
// Source is always a structural containing range, never a character mapping.
public sealed record MarkdownRenderContext(
    int Id, MarkdownElementKind Kind, int Parent, int FirstChild, int NextSibling, int ChildCount,
    MarkdownSourceRange Source, int FirstPiece, int PieceCount,
    int FirstValue, int ValueCount, int Position = 0, int Level = 0,
    bool Ordered = false, bool Loose = false, bool Header = false,
    MarkdownTableAlignment Alignment = MarkdownTableAlignment.None, int HeaderCell = -1);

public sealed record MarkdownTextPiece(
    int Id, int Context, MarkdownTextKind Kind, string Text,
    MarkdownSourceRange Source, MarkdownSourceMapping Mapping,
    int Part, bool Continued);

// Variable metadata is independently fragmented, even a huge URL or fence info.
public sealed record MarkdownValueFragment(
    MarkdownValueKind Kind, string Text, int Part, bool Continued);

public sealed record MarkdownPiecePage(
    Guid Generation, int From, int Next, bool HasMore,
    IReadOnlyList<MarkdownTextPiece> Pieces);

public sealed record MarkdownContextPage(
    Guid Generation, MarkdownRenderContext Context, int From, int Next, bool HasMore,
    IReadOnlyList<MarkdownValueFragment> Values);

public sealed record MarkdownContextDescriptorPage(
    Guid Generation, int From, int Next, bool HasMore,
    IReadOnlyList<MarkdownRenderContext> Contexts);

internal sealed record MarkdownPageLimits(int Utf8Bytes = 16 * 1024, int Records = 256) {
  internal void Validate() {
    // A fixed descriptor and one worst-case JSON-escaped fragment always fit.
    if (Utf8Bytes < 4096 || Utf8Bytes > 1024 * 1024)
      throw new ArgumentOutOfRangeException(nameof(Utf8Bytes));
    if (Records < 2 || Records > 4096)
      throw new ArgumentOutOfRangeException(nameof(Records));
  }
}
