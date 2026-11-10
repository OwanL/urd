namespace Urd.Core.MarkdownRendering;

// Disposable, generation-local structure. Original source belongs to the caller,
// not this index. IDs/cursors must never be persisted as source anchors.
public enum MarkdownElementKind {
  Document, Paragraph, Heading, Quote, List, Item, Code, CodeLine,
  Table, Row, Cell, Emphasis, Strong, InlineCode, Link, ThematicBreak
}

public enum MarkdownTextKind { Text, Literal, Code, SoftBreak, HardBreak, ThematicBreak }
public enum MarkdownSourceMapping { Exact, Containing, Container }
public enum MarkdownValueKind { Destination, Title, Language, FenceInfo, OrderedStart }
public enum MarkdownTableAlignment { None, Left, Center, Right }

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
