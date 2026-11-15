using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Urd.Core.MarkdownRendering;

// Whole-source, synchronous and deliberately unactivated. Call only outside
// session/database gates. The returned render index owns no parser nodes or source slices.
internal sealed class MarkdownParser {
  internal const int FragmentChars = 128;
  const int MaximumDepth = 128;
  // Markdig pipelines own parser instances. Isolate them per calling thread so
  // concurrent scheduler lanes never share mutable extension/parser state.
  [ThreadStatic] static MarkdownPipeline? pipeline;
  readonly string source;
  readonly List<MarkdownRenderContext> contexts = [];
  readonly List<MarkdownTextPiece> pieces = [];
  readonly List<MarkdownValueFragment> values = [];
  readonly Dictionary<int, int> lastChildren = [];

  MarkdownParser(string source) => this.source = source;

  internal static MarkdownRenderIndex Parse(string source) {
    ArgumentNullException.ThrowIfNull(source);
    pipeline ??= new MarkdownPipelineBuilder()
      .UsePipeTables(new PipeTableOptions { UseGfmRules = true })
      .UsePreciseSourceLocation().Build();
    return Extract(source, Markdown.Parse(source, pipeline));
  }

  // Internal extraction boundary also permits checking unknown-node rejection.
  // Supplied parser locations must refer to this exact, unnormalized source.
  internal static MarkdownRenderIndex Extract(string source, MarkdownDocument document) {
    var extent = MarkdownSourceMap.Convert(source, [0, source.Length])[source.Length];
    var parser = new MarkdownParser(source);
    var root = parser.AddContext(MarkdownElementKind.Document, -1, new(0, source.Length));
    parser.ParseBlocks(document, root, 0);
    parser.FinishContext(root);
    return parser.Finish(extent);
  }

  void ParseBlocks(ContainerBlock blocks, int parent, int depth) {
    CheckDepth(depth);
    foreach (var block in blocks) ParseBlock(block, parent, depth + 1);
  }

  void ParseBlock(Block block, int parent, int depth) {
    CheckDepth(depth);
    var range = Range(block, parent);
    switch (block) {
      case ParagraphBlock paragraph:
        Leaf(paragraph, MarkdownElementKind.Paragraph, parent, depth);
        break;
      case HeadingBlock heading:
        var headingId = AddContext(MarkdownElementKind.Heading, parent, range);
        contexts[headingId] = contexts[headingId] with { Level = heading.Level };
        ParseInlines(heading.Inline, headingId, depth);
        FinishContext(headingId);
        break;
      case CodeBlock code:
        Code(code, parent);
        break;
      case HtmlBlock html:
        Html(html, parent);
        break;
      case QuoteBlock quote:
        Container(quote, MarkdownElementKind.Quote, parent, depth);
        break;
      case ListBlock list:
        var listId = AddContext(MarkdownElementKind.List, parent, range);
        contexts[listId] = contexts[listId] with { Ordered = list.IsOrdered, Loose = list.IsLoose };
        if (list.IsOrdered) AddValueFragments(MarkdownValueKind.OrderedStart, list.OrderedStart ?? "1", listId);
        var itemPosition = 0;
        foreach (var child in list) {
          if (child is not ListItemBlock item) throw Unsupported(child);
          var itemId = AddContext(MarkdownElementKind.Item, listId, Range(item, listId));
          contexts[itemId] = contexts[itemId] with { Position = itemPosition++ };
          ParseBlocks(item, itemId, depth + 1);
          FinishContext(itemId);
        }
        FinishContext(listId);
        break;
      case Table table:
        Table(table, parent, depth);
        break;
      case ThematicBreakBlock:
        var thematic = AddContext(MarkdownElementKind.ThematicBreak, parent, range);
        AddPiece(thematic, MarkdownTextKind.ThematicBreak, "", range, MarkdownSourceMapping.Container, 0, false);
        FinishContext(thematic);
        break;
      case LinkReferenceDefinitionGroup:
        // Definitions are non-rendering syntax. Resolved values are detached on links.
        break;
      default:
        throw Unsupported(block);
    }
  }

  void Leaf(LeafBlock block, MarkdownElementKind kind, int parent, int depth) {
    var id = AddContext(kind, parent, Range(block, parent));
    ParseInlines(block.Inline, id, depth);
    FinishContext(id);
  }

  void Container(ContainerBlock block, MarkdownElementKind kind, int parent, int depth) {
    var id = AddContext(kind, parent, Range(block, parent));
    ParseBlocks(block, id, depth);
    FinishContext(id);
  }

  void Html(HtmlBlock html, int parent) {
    // Parser line slices exclude quote/list markers. A contiguous block source
    // substring would incorrectly render those markers on subsequent lines.
    for (var index = 0; index < html.Lines.Count; index++) {
      var line = html.Lines.Lines[index];
      var slice = line.Slice;
      var original = ReferenceEquals(slice.Text, source);
      var range = original ? new MarkdownSourceRange(slice.Start, slice.End + 1) : Range(html, parent);
      AddTextPieces(slice.ToString(), parent, MarkdownTextKind.Literal, range, original);
      var newline = line.NewLine.AsString();
      if (newline.Length == 0) continue;
      var newlineRange = original ? new MarkdownSourceRange(range.End, range.End + newline.Length) : range;
      if (newlineRange.End > source.Length || !source.AsSpan(newlineRange.Start, newlineRange.End - newlineRange.Start).SequenceEqual(newline))
        newlineRange = Range(html, parent);
      AddTextPieces(newline, parent, MarkdownTextKind.Literal, newlineRange);
    }
  }

  void Code(CodeBlock code, int parent) {
    var id = AddContext(MarkdownElementKind.Code, parent, Range(code, parent));
    if (code is FencedCodeBlock fenced) {
      AddValueFragments(MarkdownValueKind.Language, fenced.Info ?? "", id);
      AddValueFragments(MarkdownValueKind.FenceInfo, fenced.Arguments ?? "", id);
    }
    for (var index = 0; index < code.Lines.Count; index++) {
      var line = code.Lines.Lines[index];
      var slice = line.Slice;
      var original = ReferenceEquals(slice.Text, source);
      var lineRange = original ? new MarkdownSourceRange(slice.Start, slice.End + 1) : contexts[id].Source;
      var lineId = AddContext(MarkdownElementKind.CodeLine, id, lineRange);
      contexts[lineId] = contexts[lineId] with { Position = index };
      AddTextPieces(slice.ToString(), lineId, MarkdownTextKind.Code, lineRange, original);
      var newline = line.NewLine.AsString();
      var newlineRange = original ? new MarkdownSourceRange(slice.End + 1, slice.End + 1 + newline.Length) : lineRange;
      if (newlineRange.End > source.Length || !source.AsSpan(newlineRange.Start, newlineRange.End - newlineRange.Start).SequenceEqual(newline))
        newlineRange = contexts[id].Source;
      // CommonMark code text ends each logical line with LF. CRLF/CR is a
      // transformation; a final missing line ending is generated, not exact text.
      if (newline.Length == 0)
        AddPiece(lineId, MarkdownTextKind.Code, "\n", newlineRange, MarkdownSourceMapping.Container, 0, false);
      else
        AddTextPieces("\n", lineId, MarkdownTextKind.Code, newlineRange);
      contexts[lineId] = contexts[lineId] with { Source = new(lineRange.Start, Math.Max(lineRange.End, newlineRange.End)) };
      FinishContext(lineId);
    }
    FinishContext(id);
  }

  void Table(Table table, int parent, int depth) {
    var tableId = AddContext(MarkdownElementKind.Table, parent, Range(table, parent));
    var headers = new Dictionary<int, int>();
    for (var rowIndex = 0; rowIndex < table.Count; rowIndex++) {
      if (table[rowIndex] is not TableRow row) throw Unsupported(table[rowIndex]);
      var rowId = AddContext(MarkdownElementKind.Row, tableId, Range(row, tableId));
      contexts[rowId] = contexts[rowId] with { Position = rowIndex, Header = row.IsHeader };
      for (var column = 0; column < row.Count; column++) {
        if (row[column] is not TableCell cell) throw Unsupported(row[column]);
        var cellId = AddContext(MarkdownElementKind.Cell, rowId, Range(cell, rowId));
        if (row.IsHeader) headers[column] = cellId;
        var alignment = column < table.ColumnDefinitions.Count ? table.ColumnDefinitions[column].Alignment : null;
        contexts[cellId] = contexts[cellId] with {
          Position = column,
          Header = row.IsHeader,
          Alignment = alignment switch {
            TableColumnAlign.Left => MarkdownTableAlignment.Left,
            TableColumnAlign.Center => MarkdownTableAlignment.Center,
            TableColumnAlign.Right => MarkdownTableAlignment.Right,
            _ => MarkdownTableAlignment.None
          },
          HeaderCell = headers.GetValueOrDefault(column, -1)
        };
        ParseBlocks(cell, cellId, depth + 1);
        FinishContext(cellId);
      }
      FinishContext(rowId);
    }
    FinishContext(tableId);
  }

  void ParseInlines(ContainerInline? container, int parent, int depth) {
    CheckDepth(depth);
    for (var inline = container?.FirstChild; inline is not null; inline = inline.NextSibling)
      ParseInline(inline, parent, depth + 1);
  }

  void ParseInline(Inline inline, int parent, int depth) {
    CheckDepth(depth);
    var range = Range(inline, parent);
    switch (inline) {
      case LiteralInline literal:
        AddTextPieces(literal.Content.ToString(), parent, MarkdownTextKind.Text, range);
        break;
      case CodeInline code:
        var codeId = AddContext(MarkdownElementKind.InlineCode, parent, range);
        AddTextPieces(code.Content, codeId, MarkdownTextKind.Code, range);
        FinishContext(codeId);
        break;
      case EmphasisInline emphasis when emphasis.DelimiterCount is 1 or 2:
        var emphasisId = AddContext(emphasis.DelimiterCount == 1 ? MarkdownElementKind.Emphasis : MarkdownElementKind.Strong, parent, range);
        ParseInlines(emphasis, emphasisId, depth);
        FinishContext(emphasisId);
        break;
      case LinkInline link when link.IsImage:
        AddLiteralSource(parent, range);
        break;
      case LinkInline link:
        ParseLink(link, parent, range, depth);
        break;
      case AutolinkInline auto:
        ParseAutolink(auto, parent, range);
        break;
      case HtmlEntityInline entity:
        AddTextPieces(entity.Transcoded.ToString(), parent, MarkdownTextKind.Text, range);
        break;
      case HtmlInline:
        AddLiteralSource(parent, range);
        break;
      case LineBreakInline lineBreak:
        AddTextPieces("\n", parent, lineBreak.IsHard ? MarkdownTextKind.HardBreak : MarkdownTextKind.SoftBreak, range);
        break;
      case DelimiterInline delimiter when inline.GetType() == typeof(DelimiterInline):
        AddTextPieces(delimiter.ToLiteral(), parent, MarkdownTextKind.Text, range);
        ParseInlines(delimiter, parent, depth);
        break;
      default:
        throw Unsupported(inline);
    }
  }

  void ParseLink(LinkInline link, int parent, MarkdownSourceRange range, int depth) {
    var linkId = AddContext(MarkdownElementKind.Link, parent, range);
    if (link.Url is not null) {
      AddValueFragments(MarkdownValueKind.Destination, link.Url, linkId);
      if (link.Title is not null) AddValueFragments(MarkdownValueKind.Title, link.Title, linkId);
    }
    ParseInlines(link, linkId, depth);
    FinishContext(linkId);
  }

  void ParseAutolink(AutolinkInline auto, int parent, MarkdownSourceRange range) {
    var autoId = AddContext(MarkdownElementKind.Link, parent, range);
    var destination = (auto.IsEmail ? "mailto:" : "") + auto.Url;
    AddValueFragments(MarkdownValueKind.Destination, destination, autoId);
    AddTextPieces(auto.Url, autoId, MarkdownTextKind.Text, range);
    FinishContext(autoId);
  }

  void AddLiteralSource(int parent, MarkdownSourceRange range) =>
    AddTextPieces(source.Substring(range.Start, range.End - range.Start), parent, MarkdownTextKind.Literal, range);

  int AddContext(MarkdownElementKind kind, int parent, MarkdownSourceRange range) {
    var id = contexts.Count;
    contexts.Add(new(id, kind, parent, -1, -1, 0, range, pieces.Count, 0, values.Count, 0));
    if (parent < 0) return id;
    var parentContext = contexts[parent];
    if (lastChildren.TryGetValue(parent, out var previous))
      contexts[previous] = contexts[previous] with { NextSibling = id };
    contexts[parent] = parentContext with {
      FirstChild = parentContext.ChildCount == 0 ? id : parentContext.FirstChild,
      ChildCount = checked(parentContext.ChildCount + 1)
    };
    lastChildren[parent] = id;
    return id;
  }

  void FinishContext(int id) => contexts[id] = contexts[id] with { PieceCount = pieces.Count - contexts[id].FirstPiece };

  void AddValueFragments(MarkdownValueKind kind, string text, int id) {
    var part = 0;
    if (text.Length == 0) values.Add(new(kind, "", 0, false));
    for (var offset = 0; offset < text.Length;) {
      var length = FragmentLength(text, offset);
      values.Add(new(kind, new string(text.AsSpan(offset, length)), part++, offset + length < text.Length));
      offset += length;
    }
    contexts[id] = contexts[id] with { ValueCount = values.Count - contexts[id].FirstValue };
  }

  void AddTextPieces(string text, int parent, MarkdownTextKind kind, MarkdownSourceRange range, bool allowExact = true) {
    var exact = allowExact && range.End - range.Start == text.Length && source.AsSpan(range.Start, text.Length).SequenceEqual(text);
    var part = 0;
    for (var offset = 0; offset < text.Length;) {
      var length = FragmentLength(text, offset);
      var fragmentRange = exact ? new MarkdownSourceRange(range.Start + offset, range.Start + offset + length) : range;
      AddPiece(parent, kind, new string(text.AsSpan(offset, length)), fragmentRange,
        exact ? MarkdownSourceMapping.Exact : MarkdownSourceMapping.Containing, part++, offset + length < text.Length);
      offset += length;
    }
  }

  void AddPiece(int parent, MarkdownTextKind kind, string text, MarkdownSourceRange range, MarkdownSourceMapping mapping, int part, bool continued) =>
    pieces.Add(new(pieces.Count, parent, kind, text, range, mapping, part, continued));

  static int FragmentLength(string text, int offset) {
    var length = Math.Min(FragmentChars, text.Length - offset);
    if (char.IsHighSurrogate(text[offset + length - 1])) length--;
    return length;
  }

  MarkdownSourceRange Range(MarkdownObject node, int parent) {
    var span = node.Span;
    if (span.Start >= 0 && span.End >= span.Start && span.End < source.Length)
      return new(span.Start, span.End + 1);
    // Empty/padded table cells have no original characters. Their context range
    // is their containing row, not an invented zero-offset exact source location.
    return contexts[parent].Source;
  }

  MarkdownRenderIndex Finish(int extent) {
    var boundaries = contexts.SelectMany(c => new[] { c.Source.Start, c.Source.End })
      .Concat(pieces.SelectMany(p => new[] { p.Source.Start, p.Source.End }));
    var map = MarkdownSourceMap.Convert(source, boundaries);
    MarkdownSourceRange Convert(MarkdownSourceRange range) => new(map[range.Start], map[range.End]);
    return new(extent, pieces.Select(p => p with { Source = Convert(p.Source) }).ToArray(),
      contexts.Select(c => c with { Source = Convert(c.Source) }).ToArray(), values.ToArray());
  }

  static void CheckDepth(int depth) {
    if (depth > MaximumDepth) throw new NotSupportedException("Markdown ancestry exceeds 128 levels.");
  }

  static NotSupportedException Unsupported(MarkdownObject node) => new($"Unsupported Markdown node: {node.GetType().Name}.");
}
