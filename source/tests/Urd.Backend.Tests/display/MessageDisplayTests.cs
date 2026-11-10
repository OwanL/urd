using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Urd.Core.Display;
using Xunit;

namespace Urd.Backend.Tests.Display;

public sealed class MessageDisplayTests {
  [Fact]
  public void ResolvesWholeDocumentSemanticsAndDetachedAncestry() {
    const string source = "# café 😀\n\n> **strong** and *em* `a  b` &amp; [late][ref]  \n> hard\n> soft\n\n---\n\n3. parent\n\n   - child\n     - deep\n\n4. last\n\n[ref]: https://example.test/é \"title\"\n";
    var display = MessageDisplayBuilder.Build(source);
    var contexts = Contexts(display);
    var pieces = Pieces(display);
    Assert.Contains(contexts, c => c.Kind == DisplayKind.Heading && c.Level == 1);
    Assert.Contains(contexts, c => c.Kind == DisplayKind.Strong);
    Assert.Contains(contexts, c => c.Kind == DisplayKind.Emphasis);
    Assert.Contains(contexts, c => c.Kind == DisplayKind.InlineCode);
    Assert.Contains(pieces, p => p.Kind == DisplayTextKind.HardBreak);
    Assert.Contains(pieces, p => p.Kind == DisplayTextKind.SoftBreak);
    Assert.Contains(pieces, p => p.Kind == DisplayTextKind.ThematicBreak);
    Assert.Contains(pieces, p => p.Text == "&" && p.Mapping == DisplayMapping.Containing);
    var link = Assert.Single(contexts, c => c.Kind == DisplayKind.Link);
    Assert.Equal("https://example.test/é", Metadata(display, link.Id, DisplayValueKind.Destination));
    Assert.Equal("title", Metadata(display, link.Id, DisplayValueKind.Title));
    Assert.Contains(Ancestors(display, link.Id), c => c.Kind == DisplayKind.Quote);
    var ordered = Assert.Single(contexts, c => c.Kind == DisplayKind.List && c.Ordered);
    Assert.True(ordered.Loose);
    Assert.Equal("3", Metadata(display, ordered.Id, DisplayValueKind.OrderedStart));
    var deep = Assert.Single(pieces, p => p.Text == "deep");
    Assert.Equal(3, Ancestors(display, deep.Context).Count(c => c.Kind == DisplayKind.List));
    Assert.All(contexts.Where(c => c.Parent >= 0), c => Assert.True(c.Parent < c.Id));
    Assert.DoesNotContain(pieces, p => p.Text.Contains("[ref]:", StringComparison.Ordinal));
  }

  [Theory]
  [InlineData("javascript:alert(1)", false)]
  [InlineData("JaVaScRiPt&#58;alert(1)", false)]
  [InlineData("data:text/html,evil", false)]
  [InlineData("vbscript:evil", false)]
  [InlineData("//example.test/x", false)]
  [InlineData("https://example.test/x", true)]
  [InlineData("https&#58;//example.test/é", true)]
  [InlineData("mailto:a@example.test", true)]
  [InlineData("/local?q=é#β", true)]
  [InlineData("../local", true)]
  [InlineData("#anchor", true)]
  [InlineData("", true)]
  public void UnsafeDestinationsDoNotHideFormattedLabels(string destination, bool allowed) {
    var display = MessageDisplayBuilder.Build($"[visible **label**]({destination})");
    var link = Assert.Single(Contexts(display), c => c.Kind == DisplayKind.Link);
    Assert.Equal("visible label", string.Concat(Pieces(display).Select(p => p.Text)));
    Assert.Contains(Contexts(display), c => c.Kind == DisplayKind.Strong);
    var values = ReadValues(display, link.Id);
    Assert.Equal(allowed, values.Any(v => v.Kind == DisplayValueKind.Destination));
  }

  [Fact]
  public void LiteralHtmlCommentsAndDeferredImagesNeverBecomeResourceMetadata() {
    const string source = "<div>\n<script>bad()</script>\n<img src=x onerror=bad()>\n</div>\n\né😀<!-- comment --><b>x</b> ![**alt**](https://example.test/image)\n";
    var display = MessageDisplayBuilder.Build(source);
    var pieces = Pieces(display);
    Assert.Contains(pieces, p => p.Kind == DisplayTextKind.Literal && p.Text.Contains("<script>", StringComparison.Ordinal));
    Assert.Contains(pieces, p => p.Kind == DisplayTextKind.Literal && p.Text == "<!-- comment -->");
    Assert.Contains(pieces, p => p.Kind == DisplayTextKind.Literal && p.Text == "![**alt**](https://example.test/image)");
    Assert.DoesNotContain(Contexts(display), c => c.Kind == DisplayKind.Link || c.Kind == DisplayKind.Strong);
    Assert.All(pieces.Where(p => p.Kind == DisplayTextKind.Literal), p => Assert.Equal(DisplayMapping.Exact, p.Mapping));
    Assert.Contains("</div>\n", string.Concat(pieces.Select(p => p.Text)), StringComparison.Ordinal);
  }

  [Fact]
  public void NestedRawHtmlAndReferenceImagesRemainLiteralInTheirContainer() {
    var display = MessageDisplayBuilder.Build("> <div>\r\n> literal 😀\r\n> </div>\r\n\n![alt][image]\n\n[image]: https://example.test/image\n");
    Assert.Equal("<div>\r\nliteral 😀\r\n</div>\r\n![alt][image]", string.Concat(Pieces(display).Select(p => p.Text)));
    Assert.DoesNotContain(Contexts(display), c => c.Kind == DisplayKind.Link);
    Assert.Contains(Pieces(display), p => p.Text == "literal 😀" && Context(display, p.Context).Kind == DisplayKind.Quote);
    Assert.False(DisplayLinkPolicy.IsAllowed("https:\\example.test"));
    Assert.False(DisplayLinkPolicy.IsAllowed("java\tscript:evil"));
    Assert.False(DisplayLinkPolicy.IsAllowed(" https://example.test"));
  }

  [Theory]
  [InlineData("`é😀|β` | tail", "`é😀", "β`")]
  [InlineData("`é😀\\|β` | tail", "é😀|β", "tail")]
  [InlineData("é😀\\|β | tail", "é😀|β", "tail")]
  public void GfmCellsCarryHeaderAndAlignmentContext(string row, string first, string second) {
    var display = MessageDisplayBuilder.Build($"| A | B |\n| :--- | ---: |\n| {row} | discarded |\n| padded |\n");
    var cells = Contexts(display).Where(c => c.Kind == DisplayKind.Cell).ToArray();
    Assert.Equal(6, cells.Length);
    Assert.Equal(first, ContextText(display, cells[2]));
    Assert.Equal(second, ContextText(display, cells[3]));
    Assert.Equal("", ContextText(display, cells[5]));
    Assert.All(cells.Where(c => !c.Header), c => {
      var header = Context(display, c.HeaderCell);
      Assert.True(header.Header);
      Assert.Equal(c.Position, header.Position);
      Assert.Equal(c.Position == 0 ? DisplayAlignment.Left : DisplayAlignment.Right, c.Alignment);
      Assert.Contains(Ancestors(display, c.Id), ancestor => ancestor.Kind == DisplayKind.Table);
    });
  }

  [Theory]
  [InlineData("| A | B |\n| --- |\n| x | y |")]
  [InlineData("| A | B |\n| --- | |\n| x | y |")]
  public void InvalidTableDelimitersRemainParagraphs(string source) =>
    Assert.DoesNotContain(Contexts(MessageDisplayBuilder.Build(source)), c => c.Kind == DisplayKind.Table);

  [Fact]
  public void SourceRangesAreGlobalScalarSafeUtf8AndTransformsAreHonest() {
    const string source = "# é😀\r\n\r\né😀 &amp; \\* ` a\r\nb `\r\nnext\r\n\r\n```cs extra\r\nα😀\r\n\r\n```\r\n\r\n    indented 😀\r\n";
    var display = MessageDisplayBuilder.Build(source);
    var utf8 = Encoding.UTF8.GetBytes(source);
    var strict = new UTF8Encoding(false, true);
    Assert.Equal(utf8.Length, display.SourceUtf8Bytes);
    foreach (var piece in Pieces(display)) {
      Assert.InRange(piece.Source.Start, 0, utf8.Length);
      Assert.InRange(piece.Source.End, piece.Source.Start, utf8.Length);
      var original = strict.GetString(utf8, piece.Source.Start, piece.Source.End - piece.Source.Start);
      if (piece.Mapping == DisplayMapping.Exact) Assert.Equal(original, piece.Text);
    }
    var entity = Assert.Single(Pieces(display), p => p.Text == "&");
    Assert.Equal("&amp;", strict.GetString(utf8, entity.Source.Start, entity.Source.End - entity.Source.Start));
    var normalizedCode = Assert.Single(Pieces(display), p => p.Text == "a b");
    Assert.Equal(DisplayMapping.Containing, normalizedCode.Mapping);
    var codes = Contexts(display).Where(c => c.Kind == DisplayKind.Code).ToArray();
    Assert.Equal(2, codes.Length);
    Assert.Equal("cs", Metadata(display, codes[0].Id, DisplayValueKind.Language));
    Assert.Equal("extra", Metadata(display, codes[0].Id, DisplayValueKind.FenceInfo));
    Assert.Equal("α😀\n\n", ContextText(display, codes[0]));
    Assert.Equal("indented 😀\n", ContextText(display, codes[1]));
    var unclosed = MessageDisplayBuilder.Build("```\nlast");
    Assert.Equal("last\n", string.Concat(Pieces(unclosed).Select(p => p.Text)));
    Assert.Equal(DisplayMapping.Container, Pieces(unclosed)[^1].Mapping);
    Assert.Throws<ArgumentException>(() => MessageDisplayBuilder.Build("invalid \ud800"));
    Assert.Throws<InvalidOperationException>(() => DisplaySourceMap.Convert("😀", [1]));
  }

  [Fact]
  public void MiddleReadsUseIndexedWholeDocumentInterpretationAndGenerationCursors() {
    var source = string.Join("\n\n", Enumerable.Range(0, 4000).Select(i => $"paragraph {i} é😀 [resolved][ref]")) + "\n\n[ref]: https://example.test/far\n";
    var display = MessageDisplayBuilder.Build(source);
    var offset = Encoding.UTF8.GetByteCount(source.AsSpan(0, source.IndexOf("paragraph 2500", StringComparison.Ordinal)));
    var cursor = display.Locate(offset);
    var page = MessageDisplayReader.Read(display, cursor, new(4096, 2));
    Assert.StartsWith("paragraph 2500", page.Pieces[0].Text, StringComparison.Ordinal);
    Assert.Equal("resolved", page.Pieces[1].Text);
    var link = Context(display, page.Pieces[1].Context);
    Assert.Equal("https://example.test/far", Metadata(display, link.Id, DisplayValueKind.Destination));
    Assert.True(page.Next > cursor.Index);
    Assert.True(cursor.Index > 4000);
    Assert.Throws<ArgumentException>(() => MessageDisplayReader.Read(display, new(Guid.NewGuid(), 0)));
    Assert.Throws<ArgumentOutOfRangeException>(() => MessageDisplayReader.Read(display, new(display.Generation, -1)));
    Assert.Throws<ArgumentOutOfRangeException>(() => display.Locate(display.SourceUtf8Bytes + 1));
    var end = MessageDisplayReader.Read(display, new(display.Generation, display.PieceCount));
    Assert.False(end.HasMore);
    Assert.Empty(end.Pieces);
    Assert.Throws<ArgumentOutOfRangeException>(() => MessageDisplayReader.Read(display, cursor, new(100, 2)));
    Assert.Throws<ArgumentOutOfRangeException>(() => MessageDisplayReader.Read(display, cursor, new(4096, 1)));
    var empty = MessageDisplayBuilder.Build("");
    Assert.Empty(MessageDisplayReader.Read(empty, new(empty.Generation, 0)).Pieces);
  }

  [Theory]
  [InlineData("paragraph")]
  [InlineData("inline-code")]
  [InlineData("code")]
  [InlineData("cell")]
  [InlineData("header")]
  [InlineData("url")]
  [InlineData("fence-info")]
  public void GiantTextAndMetadataAreFragmentedWithBoundedAdvancingPages(string shape) {
    var giant = string.Concat(Enumerable.Repeat("é😀<>&\"'", 9000));
    var source = shape switch {
      "paragraph" => giant,
      "inline-code" => "`" + giant + "`",
      "code" => "```cs\n" + giant + "\n```",
      "cell" => "| h |\n| --- |\n| " + giant + " |",
      "header" => "| " + giant + " |\n| --- |\n| body |",
      "url" => "[label](https://example.test/" + new string('a', giant.Length) + ")",
      "fence-info" => "```cs " + giant + "\nx\n```",
      _ => throw new InvalidOperationException()
    };
    var display = MessageDisplayBuilder.Build(source);
    var limits = new DisplayPageLimits(4096, 3);
    var pieces = Pieces(display, limits);
    Assert.All(pieces, p => {
      Assert.True(p.Text.Length <= MessageDisplayBuilder.FragmentChars);
      Assert.False(p.Text.Length > 0 && char.IsLowSurrogate(p.Text[0]));
      Assert.False(p.Text.Length > 0 && char.IsHighSurrogate(p.Text[^1]));
    });
    if (shape is not ("url" or "fence-info")) {
      Assert.Contains(giant, string.Concat(pieces.Select(p => p.Text)), StringComparison.Ordinal);
      Assert.Contains(pieces, p => p.Continued && p.Part > 0);
    }
    if (shape == "url") {
      var link = Assert.Single(Contexts(display), c => c.Kind == DisplayKind.Link);
      Assert.Equal("https://example.test/" + new string('a', giant.Length), Metadata(display, link.Id, DisplayValueKind.Destination, limits));
    }
    if (shape == "fence-info") {
      var code = Assert.Single(Contexts(display), c => c.Kind == DisplayKind.Code);
      Assert.Equal(giant, Metadata(display, code.Id, DisplayValueKind.FenceInfo, limits));
    }
    if (shape == "header") {
      var body = Assert.Single(Contexts(display), c => c.Kind == DisplayKind.Cell && !c.Header);
      Assert.Equal(giant, ContextText(display, Context(display, body.HeaderCell)));
    }
  }

  [Fact]
  public void UnknownNodesFailAndParserSourceAndTreesAreCollectible() {
    // A nested document is not a supported semantic container.
    var unknown = new MarkdownDocument { new MarkdownDocument() };
    Assert.Throws<NotSupportedException>(() => MessageDisplayBuilder.Extract("x", unknown));
    var (display, references) = Detached();
    for (var attempt = 0; attempt < 3 && references.Any(r => r.IsAlive); attempt++) {
#pragma warning disable S1215 // This test explicitly verifies release of parser/source references.
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
#pragma warning restore S1215
    }
    Assert.All(references, reference => Assert.False(reference.IsAlive));
    Assert.Contains("retained", string.Concat(Pieces(display).Select(p => p.Text)), StringComparison.Ordinal);
    GC.KeepAlive(display);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static (MessageDisplayIndex Display, WeakReference[] References) Detached() {
    var source = "retained é😀 " + new string('a', 4000);
    var pipeline = new MarkdownPipelineBuilder().UsePipeTables(new PipeTableOptions { UseGfmRules = true }).UsePreciseSourceLocation().Build();
    var document = Markdown.Parse(source, pipeline);
    var paragraph = (ParagraphBlock)document[0];
    return (MessageDisplayBuilder.Extract(source, document),
      [new(source), new(document), new(paragraph), new(paragraph.Inline!)]);
  }

  static DisplayContext Context(MessageDisplayIndex display, int id) =>
    MessageDisplayReader.ReadContext(display, id, new(display.Generation, 0)).Context;

  static DisplayContext[] Contexts(MessageDisplayIndex display) =>
    Enumerable.Range(0, display.ContextCount).Select(id => Context(display, id)).ToArray();

  static IEnumerable<DisplayContext> Ancestors(MessageDisplayIndex display, int id) {
    for (var context = Context(display, id); context.Parent >= 0; context = Context(display, context.Parent))
      yield return Context(display, context.Parent);
  }

  static string ContextText(MessageDisplayIndex display, DisplayContext context) =>
    string.Concat(Enumerable.Range(context.FirstPiece, context.PieceCount).Select(i => display.Piece(i).Text));

  static DisplayPiece[] Pieces(MessageDisplayIndex display, DisplayPageLimits? limits = null) {
    limits ??= new();
    var result = new List<DisplayPiece>();
    var cursor = new DisplayCursor(display.Generation, 0);
    while (true) {
      var page = MessageDisplayReader.Read(display, cursor, limits);
      Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, MessageDisplayReader.JsonOptions).Length <= limits.Utf8Bytes);
      Assert.True(page.Pieces.Count <= limits.Records);
      result.AddRange(page.Pieces);
      if (!page.HasMore) return result.ToArray();
      Assert.True(page.Next > cursor.Index);
      cursor = new(display.Generation, page.Next);
    }
  }

  static DisplayValue[] ReadValues(MessageDisplayIndex display, int id, DisplayPageLimits? limits = null) {
    limits ??= new();
    var result = new List<DisplayValue>();
    var cursor = new DisplayCursor(display.Generation, 0);
    while (true) {
      var page = MessageDisplayReader.ReadContext(display, id, cursor, limits);
      Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, MessageDisplayReader.JsonOptions).Length <= limits.Utf8Bytes);
      Assert.True(page.Values.Count + 1 <= limits.Records);
      result.AddRange(page.Values);
      if (!page.HasMore) return result.ToArray();
      Assert.True(page.Next > cursor.Index);
      cursor = new(display.Generation, page.Next);
    }
  }

  static string Metadata(MessageDisplayIndex display, int id, DisplayValueKind kind, DisplayPageLimits? limits = null) =>
    string.Concat(ReadValues(display, id, limits).Where(v => v.Kind == kind).Select(v => v.Text));
}
