using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Urd.Core.Display;

// Whole-source, synchronous and deliberately unactivated. Call only outside
// session/database gates. The returned index owns no parser nodes or source slices.
internal sealed class MessageDisplayBuilder {
  internal const int FragmentChars = 128;
  const int MaximumDepth = 128;
  static readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
    .UsePipeTables(new PipeTableOptions { UseGfmRules = true })
    .UsePreciseSourceLocation().Build();
  readonly string source;
  readonly List<DisplayContext> contexts = [];
  readonly List<DisplayPiece> pieces = [];
  readonly List<DisplayValue> values = [];
  readonly Dictionary<int, int> lastChildren = [];

  MessageDisplayBuilder(string source) => this.source = source;

  internal static MessageDisplayIndex Build(string source) {
    ArgumentNullException.ThrowIfNull(source);
    return Extract(source, Markdown.Parse(source, pipeline));
  }

  // Internal extraction boundary also permits checking unknown-node rejection.
  // Supplied parser locations must refer to this exact, unnormalized source.
  internal static MessageDisplayIndex Extract(string source, MarkdownDocument document) {
    var extent = DisplaySourceMap.Convert(source, [0, source.Length])[source.Length];
    var builder = new MessageDisplayBuilder(source);
    var root = builder.Open(DisplayKind.Document, -1, new(0, source.Length));
    builder.Blocks(document, root, 0);
    builder.Close(root);
    return builder.Finish(extent);
  }

  void Blocks(ContainerBlock blocks, int parent, int depth) {
    CheckDepth(depth);
    foreach (var block in blocks) Block(block, parent, depth + 1);
  }

  void Block(Block block, int parent, int depth) {
    CheckDepth(depth);
    var range = Range(block, parent);
    switch (block) {
      case ParagraphBlock paragraph:
        Leaf(paragraph, DisplayKind.Paragraph, parent, depth);
        break;
      case HeadingBlock heading:
        var headingId = Open(DisplayKind.Heading, parent, range);
        contexts[headingId] = contexts[headingId] with { Level = heading.Level };
        Inlines(heading.Inline, headingId, depth);
        Close(headingId);
        break;
      case CodeBlock code:
        Code(code, parent);
        break;
      case HtmlBlock html:
        Html(html, parent);
        break;
      case QuoteBlock quote:
        Container(quote, DisplayKind.Quote, parent, depth);
        break;
      case ListBlock list:
        var listId = Open(DisplayKind.List, parent, range);
        contexts[listId] = contexts[listId] with { Ordered = list.IsOrdered, Loose = list.IsLoose };
        if (list.IsOrdered) Value(DisplayValueKind.OrderedStart, list.OrderedStart ?? "1", listId);
        var itemPosition = 0;
        foreach (var child in list) {
          if (child is not ListItemBlock item) throw Unsupported(child);
          var itemId = Open(DisplayKind.Item, listId, Range(item, listId));
          contexts[itemId] = contexts[itemId] with { Position = itemPosition++ };
          Blocks(item, itemId, depth + 1);
          Close(itemId);
        }
        Close(listId);
        break;
      case Table table:
        Table(table, parent, depth);
        break;
      case ThematicBreakBlock:
        var thematic = Open(DisplayKind.ThematicBreak, parent, range);
        AddPiece(thematic, DisplayTextKind.ThematicBreak, "", range, DisplayMapping.Container, 0, false);
        Close(thematic);
        break;
      case LinkReferenceDefinitionGroup:
        // Definitions are non-rendering syntax. Resolved values are detached on links.
        break;
      default:
        throw Unsupported(block);
    }
  }

  void Leaf(LeafBlock block, DisplayKind kind, int parent, int depth) {
    var id = Open(kind, parent, Range(block, parent));
    Inlines(block.Inline, id, depth);
    Close(id);
  }

  void Container(ContainerBlock block, DisplayKind kind, int parent, int depth) {
    var id = Open(kind, parent, Range(block, parent));
    Blocks(block, id, depth);
    Close(id);
  }

  void Html(HtmlBlock html, int parent) {
    // Parser line slices exclude quote/list markers. A contiguous block source
    // substring would incorrectly display those markers on subsequent lines.
    for (var index = 0; index < html.Lines.Count; index++) {
      var line = html.Lines.Lines[index];
      var slice = line.Slice;
      var original = ReferenceEquals(slice.Text, source);
      var range = original ? new DisplaySourceRange(slice.Start, slice.End + 1) : Range(html, parent);
      Text(slice.ToString(), parent, DisplayTextKind.Literal, range, original);
      var newline = line.NewLine.AsString();
      if (newline.Length == 0) continue;
      var newlineRange = original ? new DisplaySourceRange(range.End, range.End + newline.Length) : range;
      if (newlineRange.End > source.Length || !source.AsSpan(newlineRange.Start, newlineRange.End - newlineRange.Start).SequenceEqual(newline))
        newlineRange = Range(html, parent);
      Text(newline, parent, DisplayTextKind.Literal, newlineRange);
    }
  }

  void Code(CodeBlock code, int parent) {
    var id = Open(DisplayKind.Code, parent, Range(code, parent));
    if (code is FencedCodeBlock fenced) {
      Value(DisplayValueKind.Language, fenced.Info ?? "", id);
      Value(DisplayValueKind.FenceInfo, fenced.Arguments ?? "", id);
    }
    for (var index = 0; index < code.Lines.Count; index++) {
      var line = code.Lines.Lines[index];
      var slice = line.Slice;
      var original = ReferenceEquals(slice.Text, source);
      var lineRange = original ? new DisplaySourceRange(slice.Start, slice.End + 1) : contexts[id].Source;
      var lineId = Open(DisplayKind.CodeLine, id, lineRange);
      contexts[lineId] = contexts[lineId] with { Position = index };
      Text(slice.ToString(), lineId, DisplayTextKind.Code, lineRange, original);
      var newline = line.NewLine.AsString();
      var newlineRange = original ? new DisplaySourceRange(slice.End + 1, slice.End + 1 + newline.Length) : lineRange;
      if (newlineRange.End > source.Length || !source.AsSpan(newlineRange.Start, newlineRange.End - newlineRange.Start).SequenceEqual(newline))
        newlineRange = contexts[id].Source;
      // CommonMark code text ends each logical line with LF. CRLF/CR is a
      // transformation; a final missing line ending is generated, not exact text.
      if (newline.Length == 0)
        AddPiece(lineId, DisplayTextKind.Code, "\n", newlineRange, DisplayMapping.Container, 0, false);
      else
        Text("\n", lineId, DisplayTextKind.Code, newlineRange);
      contexts[lineId] = contexts[lineId] with { Source = new(lineRange.Start, Math.Max(lineRange.End, newlineRange.End)) };
      Close(lineId);
    }
    Close(id);
  }

  void Table(Table table, int parent, int depth) {
    var tableId = Open(DisplayKind.Table, parent, Range(table, parent));
    var headers = new Dictionary<int, int>();
    for (var rowIndex = 0; rowIndex < table.Count; rowIndex++) {
      if (table[rowIndex] is not TableRow row) throw Unsupported(table[rowIndex]);
      var rowId = Open(DisplayKind.Row, tableId, Range(row, tableId));
      contexts[rowId] = contexts[rowId] with { Position = rowIndex, Header = row.IsHeader };
      for (var column = 0; column < row.Count; column++) {
        if (row[column] is not TableCell cell) throw Unsupported(row[column]);
        var cellId = Open(DisplayKind.Cell, rowId, Range(cell, rowId));
        if (row.IsHeader) headers[column] = cellId;
        var alignment = column < table.ColumnDefinitions.Count ? table.ColumnDefinitions[column].Alignment : null;
        contexts[cellId] = contexts[cellId] with {
          Position = column,
          Header = row.IsHeader,
          Alignment = alignment switch {
            TableColumnAlign.Left => DisplayAlignment.Left,
            TableColumnAlign.Center => DisplayAlignment.Center,
            TableColumnAlign.Right => DisplayAlignment.Right,
            _ => DisplayAlignment.None
          },
          HeaderCell = headers.GetValueOrDefault(column, -1)
        };
        Blocks(cell, cellId, depth + 1);
        Close(cellId);
      }
      Close(rowId);
    }
    Close(tableId);
  }

  void Inlines(ContainerInline? container, int parent, int depth) {
    CheckDepth(depth);
    for (var inline = container?.FirstChild; inline is not null; inline = inline.NextSibling)
      Inline(inline, parent, depth + 1);
  }

  void Inline(Inline inline, int parent, int depth) {
    CheckDepth(depth);
    var range = Range(inline, parent);
    switch (inline) {
      case LiteralInline literal:
        Text(literal.Content.ToString(), parent, DisplayTextKind.Text, range);
        break;
      case CodeInline code:
        var codeId = Open(DisplayKind.InlineCode, parent, range);
        Text(code.Content, codeId, DisplayTextKind.Code, range);
        Close(codeId);
        break;
      case EmphasisInline emphasis when emphasis.DelimiterCount is 1 or 2:
        var emphasisId = Open(emphasis.DelimiterCount == 1 ? DisplayKind.Emphasis : DisplayKind.Strong, parent, range);
        Inlines(emphasis, emphasisId, depth);
        Close(emphasisId);
        break;
      case LinkInline link when link.IsImage:
        Text(source.Substring(range.Start, range.End - range.Start), parent, DisplayTextKind.Literal, range);
        break;
      case LinkInline link:
        var linkId = Open(DisplayKind.Link, parent, range);
        if (link.Url is not null && DisplayLinkPolicy.IsAllowed(link.Url)) {
          Value(DisplayValueKind.Destination, link.Url, linkId);
          if (link.Title is not null) Value(DisplayValueKind.Title, link.Title, linkId);
        }
        Inlines(link, linkId, depth);
        Close(linkId);
        break;
      case AutolinkInline auto:
        var autoId = Open(DisplayKind.Link, parent, range);
        var destination = (auto.IsEmail ? "mailto:" : "") + auto.Url;
        if (DisplayLinkPolicy.IsAllowed(destination)) Value(DisplayValueKind.Destination, destination, autoId);
        Text(auto.Url, autoId, DisplayTextKind.Text, range);
        Close(autoId);
        break;
      case HtmlEntityInline entity:
        Text(entity.Transcoded.ToString(), parent, DisplayTextKind.Text, range);
        break;
      case HtmlInline:
        Text(source.Substring(range.Start, range.End - range.Start), parent, DisplayTextKind.Literal, range);
        break;
      case LineBreakInline lineBreak:
        Text("\n", parent, lineBreak.IsHard ? DisplayTextKind.HardBreak : DisplayTextKind.SoftBreak, range);
        break;
      case DelimiterInline delimiter when inline.GetType() == typeof(DelimiterInline):
        Text(delimiter.ToLiteral(), parent, DisplayTextKind.Text, range);
        Inlines(delimiter, parent, depth);
        break;
      default:
        throw Unsupported(inline);
    }
  }

  int Open(DisplayKind kind, int parent, DisplaySourceRange range) {
    var id = contexts.Count;
    contexts.Add(new(id, kind, parent, -1, -1, range, pieces.Count, 0, values.Count, 0));
    if (parent < 0) return id;
    if (lastChildren.TryGetValue(parent, out var previous))
      contexts[previous] = contexts[previous] with { NextSibling = id };
    else
      contexts[parent] = contexts[parent] with { FirstChild = id };
    lastChildren[parent] = id;
    return id;
  }

  void Close(int id) => contexts[id] = contexts[id] with { PieceCount = pieces.Count - contexts[id].FirstPiece };

  void Value(DisplayValueKind kind, string text, int id) {
    var part = 0;
    if (text.Length == 0) values.Add(new(kind, "", 0, false));
    for (var offset = 0; offset < text.Length;) {
      var length = FragmentLength(text, offset);
      values.Add(new(kind, new string(text.AsSpan(offset, length)), part++, offset + length < text.Length));
      offset += length;
    }
    contexts[id] = contexts[id] with { ValueCount = values.Count - contexts[id].FirstValue };
  }

  void Text(string text, int parent, DisplayTextKind kind, DisplaySourceRange range, bool allowExact = true) {
    var exact = allowExact && range.End - range.Start == text.Length && source.AsSpan(range.Start, text.Length).SequenceEqual(text);
    var part = 0;
    for (var offset = 0; offset < text.Length;) {
      var length = FragmentLength(text, offset);
      var fragmentRange = exact ? new DisplaySourceRange(range.Start + offset, range.Start + offset + length) : range;
      AddPiece(parent, kind, new string(text.AsSpan(offset, length)), fragmentRange,
        exact ? DisplayMapping.Exact : DisplayMapping.Containing, part++, offset + length < text.Length);
      offset += length;
    }
  }

  void AddPiece(int parent, DisplayTextKind kind, string text, DisplaySourceRange range, DisplayMapping mapping, int part, bool continued) =>
    pieces.Add(new(pieces.Count, parent, kind, text, range, mapping, part, continued));

  static int FragmentLength(string text, int offset) {
    var length = Math.Min(FragmentChars, text.Length - offset);
    if (char.IsHighSurrogate(text[offset + length - 1])) length--;
    return length;
  }

  DisplaySourceRange Range(MarkdownObject node, int parent) {
    var span = node.Span;
    if (span.Start >= 0 && span.End >= span.Start && span.End < source.Length)
      return new(span.Start, span.End + 1);
    // Empty/padded table cells have no original characters. Their context range
    // is their containing row, not an invented zero-offset exact source location.
    return contexts[parent].Source;
  }

  MessageDisplayIndex Finish(int extent) {
    var boundaries = contexts.SelectMany(c => new[] { c.Source.Start, c.Source.End })
      .Concat(pieces.SelectMany(p => new[] { p.Source.Start, p.Source.End }));
    var map = DisplaySourceMap.Convert(source, boundaries);
    DisplaySourceRange Convert(DisplaySourceRange range) => new(map[range.Start], map[range.End]);
    return new(extent, pieces.Select(p => p with { Source = Convert(p.Source) }).ToArray(),
      contexts.Select(c => c with { Source = Convert(c.Source) }).ToArray(), values.ToArray());
  }

  static void CheckDepth(int depth) {
    if (depth > MaximumDepth) throw new NotSupportedException("Display ancestry exceeds 128 levels.");
  }

  static NotSupportedException Unsupported(MarkdownObject node) => new($"Unsupported display node: {node.GetType().Name}.");
}
