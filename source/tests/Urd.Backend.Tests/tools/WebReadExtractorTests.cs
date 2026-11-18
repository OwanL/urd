using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

// Readable-Markdown extraction fidelity over rendered document HTML.
public sealed class WebReadExtractorTests {
  [Fact]
  public void ExtractionKeepsStructureAndDropsBoilerplate() {
    string html = """
      <html>
        <head><script>callHome();</script><style>.hidden { color: red }</style></head>
        <body>
          <nav><a href="/news">News</a><span>menu chrome</span></nav>
          <header><h6>site banner</h6></header>
          <main>
            <h2>Setup</h2>
            <p>Install the tool with <a href="/docs/install">the installer</a>, then continue.</p>
            <ul><li>first item</li><li>second item</li></ul>
            <blockquote>quoted text</blockquote>
            <pre>keep   raw
      text</pre>
            <table><tr><td>alpha</td><td>beta</td></tr><tr><td>gamma</td><td></td></tr></table>
          </main>
          <footer><span>footer chrome</span></footer>
        </body>
      </html>
      """;
    WebPageExtraction extraction = WebPageExtractor.Extract(html, "https://example.com/a/page.html");
    Assert.Equal("""
      ## Setup

      Install the tool with [the installer](https://example.com/docs/install), then continue.

      - first item

      - second item

      quoted text

      ```
      keep   raw
      text
      ```

      alpha | beta

      gamma
      """, extraction.Markdown);
    Assert.False(extraction.Truncated);
  }

  [Fact]
  public void ExtractionResolvesAbsoluteLinksAndRendersMeaninglessAnchorsAsText() {
    string html = """
      <html><body><article>
        <p>Read <a href="../guides/index.html">the guide</a>, <a>an anchor without href</a>,
        <a href="mailto:support@example.com">mail support</a>, <a href="javascript:void(0)">do it</a> or
        <a href="https://other.example.org/">an absolute site</a>.</p>
        <div><p>only the inner paragraph</p></div>
        <div>standalone wrapper text</div>
      </article></body></html>
      """;
    WebPageExtraction extraction = WebPageExtractor.Extract(html, "https://example.com/a/page.html");
    Assert.DoesNotContain("[an anchor without href]", extraction.Markdown, StringComparison.Ordinal);
    Assert.Contains("[the guide](https://example.com/guides/index.html)", extraction.Markdown, StringComparison.Ordinal);
    Assert.Contains("[an absolute site](https://other.example.org/)", extraction.Markdown, StringComparison.Ordinal);
    Assert.DoesNotContain("[mail support]", extraction.Markdown, StringComparison.Ordinal);
    Assert.DoesNotContain("[do it]", extraction.Markdown, StringComparison.Ordinal);
    Assert.Contains("only the inner paragraph", extraction.Markdown, StringComparison.Ordinal);
    Assert.Contains("standalone wrapper text", extraction.Markdown, StringComparison.Ordinal);
  }

  [Fact]
  public void EmptyOrEmptyAfterStrippingDocumentsProduceEmptyMarkdown() {
    Assert.Equal("", WebPageExtractor.Extract("<html><body></body></html>", "https://example.com/").Markdown);
    Assert.Equal("", WebPageExtractor.Extract("<html><body><nav>only chrome</nav></body></html>", "https://example.com/").Markdown);
  }
}
