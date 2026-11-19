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
  public void ExtractionKeepsInlineTextAroundNestedBlocksInDocumentOrder() {
    string html = """
      <html><body><main>
        <div class="quote"><span class="text">“The world is our thinking.”</span><span> by <small>Albert Einstein</small></span><div class="tags">Tags: <a href="/tag/thinking">thinking</a></div><span>After tags</span></div>
        <section>Before <em>nested content</em><div><p>Inner paragraph</p></div>After nested content</section>
      </main></body></html>
      """;
    WebPageExtraction extraction = WebPageExtractor.Extract(html, "https://example.com/");
    Assert.Equal("""
      “The world is our thinking.” by Albert Einstein

      Tags: [thinking](https://example.com/tag/thinking)

      After tags

      Before nested content

      Inner paragraph

      After nested content
      """, extraction.Markdown);
    Assert.False(extraction.Truncated);
  }

  [Fact]
  public void EmptyOrEmptyAfterStrippingDocumentsProduceEmptyMarkdown() {
    Assert.Equal("", WebPageExtractor.Extract("<html><body></body></html>", "https://example.com/").Markdown);
    Assert.Equal("", WebPageExtractor.Extract("<html><body><nav>only chrome</nav></body></html>", "https://example.com/").Markdown);
  }
}
