using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Urd.Core.MarkdownRendering;
using Xunit;

namespace Urd.Backend.Tests.MarkdownRendering;

public sealed class MarkdownRenderingTests {
  [Fact]
  public void ResolvesWholeDocumentSemanticsAndDetachedAncestry() {
    const string source = "# café 😀\n\n> **strong** and *em* `a  b` &amp; [late][ref]  \n> hard\n> soft\n\n---\n\n3. parent\n\n   - child\n     - deep\n\n4. last\n\n[ref]: https://example.test/é \"title\"\n";
    var index = MarkdownParser.Parse(source);
    var contexts = Contexts(index);
    var pieces = Pieces(index);
    Assert.Contains(contexts, c => c.Kind == MarkdownElementKind.Heading && c.Level == 1);
    Assert.Contains(contexts, c => c.Kind == MarkdownElementKind.Strong);
    Assert.Contains(contexts, c => c.Kind == MarkdownElementKind.Emphasis);
    Assert.Contains(contexts, c => c.Kind == MarkdownElementKind.InlineCode);
    Assert.Contains(pieces, p => p.Kind == MarkdownTextKind.HardBreak);
    Assert.Contains(pieces, p => p.Kind == MarkdownTextKind.SoftBreak);
    Assert.Contains(pieces, p => p.Kind == MarkdownTextKind.ThematicBreak);
    Assert.Contains(pieces, p => p.Text == "&" && p.Mapping == MarkdownSourceMapping.Containing);
    var link = Assert.Single(contexts, c => c.Kind == MarkdownElementKind.Link);
    Assert.Equal("https://example.test/é", Metadata(index, link.Id, MarkdownValueKind.Destination));
    Assert.Equal("title", Metadata(index, link.Id, MarkdownValueKind.Title));
    Assert.Contains(Ancestors(index, link.Id), c => c.Kind == MarkdownElementKind.Quote);
    var ordered = Assert.Single(contexts, c => c.Kind == MarkdownElementKind.List && c.Ordered);
    Assert.True(ordered.Loose);
    Assert.Equal("3", Metadata(index, ordered.Id, MarkdownValueKind.OrderedStart));
    var deep = Assert.Single(pieces, p => p.Text == "deep");
    Assert.Equal(3, Ancestors(index, deep.Context).Count(c => c.Kind == MarkdownElementKind.List));
    Assert.All(contexts.Where(c => c.Parent >= 0), c => Assert.True(c.Parent < c.Id));
    Assert.DoesNotContain(pieces, p => p.Text.Contains("[ref]:", StringComparison.Ordinal));
  }

  [Fact]
  public void ChildCountsManifestAndContextJsonMatchTheDetachedIndex() {
    var index = MarkdownParser.Parse("# heading\n\n> quote\n\n| a | b |\n| --- | --- |\n| c | d |\n");
    Assert.Equal(new MarkdownRenderManifest(index.Generation, index.SourceUtf8Bytes, index.PieceCount, index.ContextCount), index.Manifest);
    foreach (var context in Contexts(index)) {
      var childId = context.FirstChild;
      var count = 0;
      while (childId >= 0) {
        var child = Context(index, childId);
        Assert.Equal(context.Id, child.Parent);
        childId = child.NextSibling;
        count++;
      }
      Assert.Equal(context.ChildCount, count);
      using var json = JsonDocument.Parse(JsonSerializer.Serialize(context, MarkdownRenderJson.Options));
      Assert.Equal(context.ChildCount, json.RootElement.GetProperty("childCount").GetInt32());
    }

    var root = new MarkdownRenderContext(0, MarkdownElementKind.Document, -1, 1, -1, 0,
        new(0, 0), 0, 0, 0, 0);
    var invalidChild = new MarkdownRenderContext(1, MarkdownElementKind.Paragraph, 0, -1, -1, 0,
        new(0, 0), 0, 0, 0, 0);
    Assert.Throws<InvalidOperationException>(() => new MarkdownRenderIndex(0, [], [root, invalidChild], []));
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
    var index = MarkdownParser.Parse($"[visible **label**]({destination})");
    var link = Assert.Single(Contexts(index), c => c.Kind == MarkdownElementKind.Link);
    Assert.Equal("visible label", string.Concat(Pieces(index).Select(p => p.Text)));
    Assert.Contains(Contexts(index), c => c.Kind == MarkdownElementKind.Strong);
    var values = ReadValues(index, link.Id);
    Assert.Equal(allowed, values.Any(v => v.Kind == MarkdownValueKind.Destination));
  }

  [Fact]
  public void LiteralHtmlCommentsAndDeferredImagesNeverBecomeResourceMetadata() {
    const string source = "<div>\n<script>bad()</script>\n<img src=x onerror=bad()>\n</div>\n\né😀<!-- comment --><b>x</b> ![**alt**](https://example.test/image)\n";
    var index = MarkdownParser.Parse(source);
    var pieces = Pieces(index);
    Assert.Contains(pieces, p => p.Kind == MarkdownTextKind.Literal && p.Text.Contains("<script>", StringComparison.Ordinal));
    Assert.Contains(pieces, p => p.Kind == MarkdownTextKind.Literal && p.Text == "<!-- comment -->");
    Assert.Contains(pieces, p => p.Kind == MarkdownTextKind.Literal && p.Text == "![**alt**](https://example.test/image)");
    Assert.DoesNotContain(Contexts(index), c => c.Kind == MarkdownElementKind.Link || c.Kind == MarkdownElementKind.Strong);
    Assert.All(pieces.Where(p => p.Kind == MarkdownTextKind.Literal), p => Assert.Equal(MarkdownSourceMapping.Exact, p.Mapping));
    Assert.Contains("</div>\n", string.Concat(pieces.Select(p => p.Text)), StringComparison.Ordinal);
  }

  [Fact]
  public void NestedRawHtmlAndReferenceImagesRemainLiteralInTheirContainer() {
    var index = MarkdownParser.Parse("> <div>\r\n> literal 😀\r\n> </div>\r\n\n![alt][image]\n\n[image]: https://example.test/image\n");
    Assert.Equal("<div>\r\nliteral 😀\r\n</div>\r\n![alt][image]", string.Concat(Pieces(index).Select(p => p.Text)));
    Assert.DoesNotContain(Contexts(index), c => c.Kind == MarkdownElementKind.Link);
    Assert.Contains(Pieces(index), p => p.Text == "literal 😀" && Context(index, p.Context).Kind == MarkdownElementKind.Quote);
    Assert.False(MarkdownLinkDestinationPolicy.IsAllowed("https:\\example.test"));
    Assert.False(MarkdownLinkDestinationPolicy.IsAllowed("java\tscript:evil"));
    Assert.False(MarkdownLinkDestinationPolicy.IsAllowed(" https://example.test"));
  }

  [Theory]
  [InlineData("`é😀|β` | tail", "`é😀", "β`")]
  [InlineData("`é😀\\|β` | tail", "é😀|β", "tail")]
  [InlineData("é😀\\|β | tail", "é😀|β", "tail")]
  public void GfmCellsCarryHeaderAndAlignmentContext(string row, string first, string second) {
    var index = MarkdownParser.Parse($"| A | B |\n| :--- | ---: |\n| {row} | discarded |\n| padded |\n");
    var cells = Contexts(index).Where(c => c.Kind == MarkdownElementKind.Cell).ToArray();
    Assert.Equal(6, cells.Length);
    Assert.Equal(first, ContextText(index, cells[2]));
    Assert.Equal(second, ContextText(index, cells[3]));
    Assert.Equal("", ContextText(index, cells[5]));
    Assert.All(cells.Where(c => !c.Header), c => {
      var header = Context(index, c.HeaderCell);
      Assert.True(header.Header);
      Assert.Equal(c.Position, header.Position);
      Assert.Equal(c.Position == 0 ? MarkdownTableAlignment.Left : MarkdownTableAlignment.Right, c.Alignment);
      Assert.Contains(Ancestors(index, c.Id), ancestor => ancestor.Kind == MarkdownElementKind.Table);
    });
  }

  [Theory]
  [InlineData("| A | B |\n| --- |\n| x | y |")]
  [InlineData("| A | B |\n| --- | |\n| x | y |")]
  public void InvalidTableDelimitersRemainParagraphs(string source) =>
    Assert.DoesNotContain(Contexts(MarkdownParser.Parse(source)), c => c.Kind == MarkdownElementKind.Table);

  [Fact]
  public void SourceRangesAreGlobalScalarSafeUtf8AndTransformsAreHonest() {
    const string source = "# é😀\r\n\r\né😀 &amp; \\* ` a\r\nb `\r\nnext\r\n\r\n```cs extra\r\nα😀\r\n\r\n```\r\n\r\n    indented 😀\r\n";
    var index = MarkdownParser.Parse(source);
    var utf8 = Encoding.UTF8.GetBytes(source);
    var strict = new UTF8Encoding(false, true);
    Assert.Equal(utf8.Length, index.SourceUtf8Bytes);
    foreach (var piece in Pieces(index)) {
      Assert.InRange(piece.Source.Start, 0, utf8.Length);
      Assert.InRange(piece.Source.End, piece.Source.Start, utf8.Length);
      var original = strict.GetString(utf8, piece.Source.Start, piece.Source.End - piece.Source.Start);
      if (piece.Mapping == MarkdownSourceMapping.Exact) Assert.Equal(original, piece.Text);
    }
    var entity = Assert.Single(Pieces(index), p => p.Text == "&");
    Assert.Equal("&amp;", strict.GetString(utf8, entity.Source.Start, entity.Source.End - entity.Source.Start));
    var normalizedCode = Assert.Single(Pieces(index), p => p.Text == "a b");
    Assert.Equal(MarkdownSourceMapping.Containing, normalizedCode.Mapping);
    var codes = Contexts(index).Where(c => c.Kind == MarkdownElementKind.Code).ToArray();
    Assert.Equal(2, codes.Length);
    Assert.Equal("cs", Metadata(index, codes[0].Id, MarkdownValueKind.Language));
    Assert.Equal("extra", Metadata(index, codes[0].Id, MarkdownValueKind.FenceInfo));
    Assert.Equal("α😀\n\n", ContextText(index, codes[0]));
    Assert.Equal("indented 😀\n", ContextText(index, codes[1]));
    var unclosed = MarkdownParser.Parse("```\nlast");
    Assert.Equal("last\n", string.Concat(Pieces(unclosed).Select(p => p.Text)));
    Assert.Equal(MarkdownSourceMapping.Container, Pieces(unclosed)[^1].Mapping);
    Assert.Throws<ArgumentException>(() => MarkdownParser.Parse("invalid \ud800"));
    Assert.Throws<InvalidOperationException>(() => MarkdownSourceMap.Convert("😀", [1]));
  }

  [Fact]
  public void MiddleReadsUseIndexedWholeDocumentInterpretationAndGenerationCursors() {
    var source = string.Join("\n\n", Enumerable.Range(0, 4000).Select(i => $"paragraph {i} é😀 [resolved][ref]")) + "\n\n[ref]: https://example.test/far\n";
    var index = MarkdownParser.Parse(source);
    var offset = Encoding.UTF8.GetByteCount(source.AsSpan(0, source.IndexOf("paragraph 2500", StringComparison.Ordinal)));
    var cursor = index.Locate(offset);
    var page = MarkdownPageReader.Read(index, cursor, new(4096, 2));
    Assert.StartsWith("paragraph 2500", page.Pieces[0].Text, StringComparison.Ordinal);
    Assert.Equal("resolved", page.Pieces[1].Text);
    var link = Context(index, page.Pieces[1].Context);
    Assert.Equal("https://example.test/far", Metadata(index, link.Id, MarkdownValueKind.Destination));
    Assert.True(page.Next > cursor.Index);
    Assert.True(cursor.Index > 4000);
    Assert.Throws<ArgumentException>(() => MarkdownPageReader.Read(index, new(Guid.NewGuid(), 0)));
    Assert.Throws<ArgumentOutOfRangeException>(() => MarkdownPageReader.Read(index, new(index.Generation, -1)));
    Assert.Throws<ArgumentOutOfRangeException>(() => index.Locate(index.SourceUtf8Bytes + 1));
    var end = MarkdownPageReader.Read(index, new(index.Generation, index.PieceCount));
    Assert.False(end.HasMore);
    Assert.Empty(end.Pieces);
    Assert.Throws<ArgumentOutOfRangeException>(() => MarkdownPageReader.Read(index, cursor, new(100, 2)));
    Assert.Throws<ArgumentOutOfRangeException>(() => MarkdownPageReader.Read(index, cursor, new(4096, 1)));
    var empty = MarkdownParser.Parse("");
    Assert.Empty(MarkdownPageReader.Read(empty, new(empty.Generation, 0)).Pieces);
  }

  [Fact]
  public void ReadContextsReturnsRequestedDescriptorsInBoundedContinuations() {
    var index = MarkdownParser.Parse(string.Join("\n\n", Enumerable.Range(0, 20).Select(i => $"paragraph {i}")));
    int[] ids = [20, 0, 5, 3, 2];
    var expected = ids.Select(id => Context(index, id)).ToArray();
    var contexts = new List<MarkdownRenderContext>();
    var cursor = new MarkdownRenderCursor(index.Generation, 0);
    while (true) {
      var page = MarkdownPageReader.ReadContexts(index, ids, cursor, new(4096, 2));
      Assert.Equal(cursor.Index, page.From);
      Assert.Equal(page.From + page.Contexts.Count, page.Next);
      Assert.Equal(page.Next < ids.Length, page.HasMore);
      Assert.InRange(page.Contexts.Count, 1, 2);
      Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, MarkdownRenderJson.Options).Length <= 4096);
      Assert.Equal(ids.Skip(page.From).Take(page.Contexts.Count), page.Contexts.Select(context => context.Id));
      contexts.AddRange(page.Contexts);
      if (!page.HasMore) break;
      Assert.True(page.Next > cursor.Index);
      cursor = new(index.Generation, page.Next);
    }
    Assert.Equal(expected, contexts);
  }

  [Fact]
  public void ReadContextsHonorsByteLimitAndRejectsInvalidRequests() {
    var index = MarkdownParser.Parse(string.Join("\n\n", Enumerable.Range(0, 300).Select(i => $"paragraph {i}")));
    int[] ids = Enumerable.Range(0, 256).ToArray();
    var contexts = new List<MarkdownRenderContext>();
    var cursor = new MarkdownRenderCursor(index.Generation, 0);
    while (true) {
      var page = MarkdownPageReader.ReadContexts(index, ids, cursor, new(4096, 256));
      Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, MarkdownRenderJson.Options).Length <= 4096);
      Assert.InRange(page.Contexts.Count, 1, 256);
      Assert.Equal(page.From + page.Contexts.Count, page.Next);
      Assert.Equal(ids.Skip(page.From).Take(page.Contexts.Count), page.Contexts.Select(context => context.Id));
      if (contexts.Count == 0) Assert.True(page.HasMore);
      contexts.AddRange(page.Contexts);
      if (!page.HasMore) break;
      Assert.True(page.Next > cursor.Index);
      cursor = new(index.Generation, page.Next);
    }
    Assert.Equal(ids, contexts.Select(context => context.Id));
    Assert.Throws<ArgumentOutOfRangeException>(() => MarkdownPageReader.ReadContexts(index, [-1], new(index.Generation, 0)));
    Assert.Throws<ArgumentOutOfRangeException>(() => MarkdownPageReader.ReadContexts(index, [index.ContextCount], new(index.Generation, 0)));
    Assert.Throws<ArgumentException>(() => MarkdownPageReader.ReadContexts(index, [0, 0], new(index.Generation, 0)));
    Assert.Throws<ArgumentOutOfRangeException>(() => MarkdownPageReader.ReadContexts(index,
        Enumerable.Range(0, 257).ToArray(), new(index.Generation, 0)));
    Assert.Throws<ArgumentException>(() => MarkdownPageReader.ReadContexts(index, [0], new(Guid.NewGuid(), 0)));
    Assert.Throws<ArgumentOutOfRangeException>(() => MarkdownPageReader.ReadContexts(index, [0], new(index.Generation, 2)));
  }

  [Fact]
  public void ReadBeforeReturnsAscendingBoundedContinuationAndHandlesEnds() {
    var index = MarkdownParser.Parse(string.Join("\n\n", Enumerable.Range(0, 10).Select(i => $"paragraph {i}")));
    var atStart = MarkdownPageReader.ReadBefore(index, new(index.Generation, 0));
    Assert.Equal((0, 0, index.PieceCount > 0), (atStart.From, atStart.Next, atStart.HasMore));
    Assert.Empty(atStart.Pieces);

    var cursor = new MarkdownRenderCursor(index.Generation, index.PieceCount);
    var backwards = new List<MarkdownTextPiece>();
    while (true) {
      var page = MarkdownPageReader.ReadBefore(index, cursor, new(4096, 2));
      Assert.Equal(cursor.Index, page.Next);
      Assert.Equal(page.Next < index.PieceCount, page.HasMore);
      Assert.True(page.Pieces.Count <= 2);
      Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, MarkdownRenderJson.Options).Length <= 4096);
      Assert.Equal(Enumerable.Range(page.From, page.Next - page.From), page.Pieces.Select(piece => piece.Id));
      backwards.InsertRange(0, page.Pieces);
      if (page.From == 0) break;
      cursor = new(index.Generation, page.From);
    }
    Assert.Equal(Enumerable.Range(0, index.PieceCount), backwards.Select(piece => piece.Id));
    Assert.Equal(string.Concat(Pieces(index).Select(piece => piece.Text)), string.Concat(backwards.Select(piece => piece.Text)));
    var empty = MarkdownParser.Parse("");
    var emptyPage = MarkdownPageReader.ReadBefore(empty, new(empty.Generation, 0));
    Assert.Empty(emptyPage.Pieces);
    Assert.False(emptyPage.HasMore);
    Assert.Throws<ArgumentException>(() => MarkdownPageReader.ReadBefore(index, new(Guid.NewGuid(), index.PieceCount)));
    Assert.Throws<ArgumentOutOfRangeException>(() => MarkdownPageReader.ReadBefore(index, new(index.Generation, index.PieceCount + 1)));
  }

  [Fact]
  public void ReadBeforeHonorsByteBoundaryAndRejectsOneOversizedRecord() {
    var index = MarkdownParser.Parse(string.Join("\n\n", Enumerable.Range(0, 80).Select(i => new string((char)('a' + i % 20), 128))));
    var page = MarkdownPageReader.ReadBefore(index, new(index.Generation, index.PieceCount), new(4096, 100));
    Assert.InRange(page.Pieces.Count, 1, 99);
    Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, MarkdownRenderJson.Options).Length <= 4096);
    var previous = index.Piece(page.From - 1);
    var withPrevious = page with { From = page.From - 1, Pieces = new[] { previous }.Concat(page.Pieces).ToArray() };
    Assert.True(JsonSerializer.SerializeToUtf8Bytes(withPrevious, MarkdownRenderJson.Options).Length > 4096);

    var context = new MarkdownRenderContext(0, MarkdownElementKind.Document, -1, -1, -1, 0,
        new(0, 0), 0, 0, 0, 0);
    var oversizedPiece = new MarkdownTextPiece(0, 0, MarkdownTextKind.Text, new string('x', 5000),
        new(0, 0), MarkdownSourceMapping.Containing, 0, false);
    var oversized = new MarkdownRenderIndex(0, [oversizedPiece], [context], []);
    Assert.Throws<InvalidOperationException>(() => MarkdownPageReader.ReadBefore(oversized,
        new(oversized.Generation, 1), new(4096, 2)));
    var trailing = new MarkdownTextPiece(1, 0, MarkdownTextKind.Text, "fits",
        new(0, 0), MarkdownSourceMapping.Containing, 0, false);
    var withUnreturnedOversized = new MarkdownRenderIndex(0, [oversizedPiece, trailing], [context], []);
    var trailingPage = MarkdownPageReader.ReadBefore(withUnreturnedOversized,
        new(withUnreturnedOversized.Generation, 2), new(4096, 2));
    Assert.Equal([trailing], trailingPage.Pieces);
    Assert.Equal(1, trailingPage.From);
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
    var index = MarkdownParser.Parse(source);
    var limits = new MarkdownPageLimits(4096, 3);
    var pieces = Pieces(index, limits);
    Assert.All(pieces, p => {
      Assert.True(p.Text.Length <= MarkdownParser.FragmentChars);
      Assert.False(p.Text.Length > 0 && char.IsLowSurrogate(p.Text[0]));
      Assert.False(p.Text.Length > 0 && char.IsHighSurrogate(p.Text[^1]));
    });
    if (shape is not ("url" or "fence-info")) {
      Assert.Contains(giant, string.Concat(pieces.Select(p => p.Text)), StringComparison.Ordinal);
      Assert.Contains(pieces, p => p.Continued && p.Part > 0);
    }
    if (shape == "url") {
      var link = Assert.Single(Contexts(index), c => c.Kind == MarkdownElementKind.Link);
      Assert.Equal("https://example.test/" + new string('a', giant.Length), Metadata(index, link.Id, MarkdownValueKind.Destination, limits));
    }
    if (shape == "fence-info") {
      var code = Assert.Single(Contexts(index), c => c.Kind == MarkdownElementKind.Code);
      Assert.Equal(giant, Metadata(index, code.Id, MarkdownValueKind.FenceInfo, limits));
    }
    if (shape == "header") {
      var body = Assert.Single(Contexts(index), c => c.Kind == MarkdownElementKind.Cell && !c.Header);
      Assert.Equal(giant, ContextText(index, Context(index, body.HeaderCell)));
    }
  }

  [Fact]
  public void UnknownNodesFailAndParserSourceAndTreesAreCollectible() {
    // A nested document is not a supported semantic container.
    var unknown = new MarkdownDocument { new MarkdownDocument() };
    Assert.Throws<NotSupportedException>(() => MarkdownParser.Extract("x", unknown));
    var (index, references) = Detached();
    for (var attempt = 0; attempt < 3 && references.Any(r => r.IsAlive); attempt++) {
#pragma warning disable S1215 // This test explicitly verifies release of parser/source references.
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
#pragma warning restore S1215
    }
    Assert.All(references, reference => Assert.False(reference.IsAlive));
    Assert.Contains("retained", string.Concat(Pieces(index).Select(p => p.Text)), StringComparison.Ordinal);
    GC.KeepAlive(index);
  }

  [Fact]
  public async Task ConcurrentSharedParserMatchesSerialContextsPiecesMetadataAndSourceRanges() {
    string[] corpus = [
      "# café 😀\n\n> **bold** &amp; [late][ref]\n\n[ref]: https://example.test/é \"title\"\n",
      "| A | B |\n| :--- | ---: |\n| `é\\|β` | **tail** |\n",
      "3. parent\n\n   - child\n\n```csharp\nvar x = 1;\n```\n",
      "<div>literal</div>\n\n![alt](https://example.test/image)\n",
      string.Concat(Enumerable.Repeat("paragraph *é🙂* [link](../local)\n\n", 512))
    ];
    string Snapshot(MarkdownRenderIndex index) => JsonSerializer.Serialize(new {
      index.SourceUtf8Bytes, index.AccountedBytes, Contexts = Contexts(index), Pieces = Pieces(index),
      Values = Enumerable.Range(0, index.ContextCount).Select(id => ReadValues(index, id)).ToArray()
    });
    var expected = corpus.Select(source => Snapshot(MarkdownParser.Parse(source))).ToArray();
    using var start = new Barrier(2);
    var workers = Enumerable.Range(0, 2).Select(lane => Task.Factory.StartNew(() => {
      for (int iteration = 0; iteration < 20; iteration++) {
        int sample = (iteration + lane) % corpus.Length;
        Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
        var actual = MarkdownParser.Parse(corpus[sample]);
        Assert.Equal(expected[sample], Snapshot(actual));
      }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
    await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static (MarkdownRenderIndex Index, WeakReference[] References) Detached() {
    var source = "retained é😀 " + new string('a', 4000);
    var pipeline = new MarkdownPipelineBuilder().UsePipeTables(new PipeTableOptions { UseGfmRules = true }).UsePreciseSourceLocation().Build();
    var document = Markdown.Parse(source, pipeline);
    var paragraph = (ParagraphBlock)document[0];
    return (MarkdownParser.Extract(source, document),
      [new(source), new(document), new(paragraph), new(paragraph.Inline!)]);
  }

  static MarkdownRenderContext Context(MarkdownRenderIndex index, int id) =>
    MarkdownPageReader.ReadContext(index, id, new(index.Generation, 0)).Context;

  static MarkdownRenderContext[] Contexts(MarkdownRenderIndex index) =>
    Enumerable.Range(0, index.ContextCount).Select(id => Context(index, id)).ToArray();

  static IEnumerable<MarkdownRenderContext> Ancestors(MarkdownRenderIndex index, int id) {
    for (var context = Context(index, id); context.Parent >= 0; context = Context(index, context.Parent))
      yield return Context(index, context.Parent);
  }

  static string ContextText(MarkdownRenderIndex index, MarkdownRenderContext context) =>
    string.Concat(Enumerable.Range(context.FirstPiece, context.PieceCount).Select(i => index.Piece(i).Text));

  static MarkdownTextPiece[] Pieces(MarkdownRenderIndex index, MarkdownPageLimits? limits = null) {
    limits ??= new();
    var result = new List<MarkdownTextPiece>();
    var cursor = new MarkdownRenderCursor(index.Generation, 0);
    while (true) {
      var page = MarkdownPageReader.Read(index, cursor, limits);
      Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, MarkdownRenderJson.Options).Length <= limits.Utf8Bytes);
      Assert.True(page.Pieces.Count <= limits.Records);
      result.AddRange(page.Pieces);
      if (!page.HasMore) return result.ToArray();
      Assert.True(page.Next > cursor.Index);
      cursor = new(index.Generation, page.Next);
    }
  }

  static MarkdownValueFragment[] ReadValues(MarkdownRenderIndex index, int id, MarkdownPageLimits? limits = null) {
    limits ??= new();
    var result = new List<MarkdownValueFragment>();
    var cursor = new MarkdownRenderCursor(index.Generation, 0);
    while (true) {
      var page = MarkdownPageReader.ReadContext(index, id, cursor, limits);
      Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, MarkdownRenderJson.Options).Length <= limits.Utf8Bytes);
      Assert.True(page.Values.Count + 1 <= limits.Records);
      result.AddRange(page.Values);
      if (!page.HasMore) return result.ToArray();
      Assert.True(page.Next > cursor.Index);
      cursor = new(index.Generation, page.Next);
    }
  }

  static string Metadata(MarkdownRenderIndex index, int id, MarkdownValueKind kind, MarkdownPageLimits? limits = null) =>
    string.Concat(ReadValues(index, id, limits).Where(v => v.Kind == kind).Select(v => v.Text));
}
