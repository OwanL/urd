using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class WebReadOutputTests {
  [Fact]
  public async Task SmallHtmlReadIsInlineWithFinalUrlTitleMarkdownAndDetails() {
    var visit = new WebPageVisit(
      FinalUrl: "https://example.com/final", StatusCode: 200, ContentType: "text/html; charset=utf-8",
      Title: "Example article",
      Html: "<html><head><title>Example article</title></head><body><nav>junk navigation</nav>" +
        "<main><h1>Example article</h1><p>First paragraph with <a href=\"/related\">related page</a>.</p></main></body></html>",
      Body: null, BlockReason: null, TextSettled: true);
    ToolOutput result = await Present(visit);
    Assert.False(result.IsError, result.Text);
    Assert.StartsWith("web_read completed: https://example.com/final\nTitle: Example article\n\n# Example article", result.Text, StringComparison.Ordinal);
    Assert.Contains("[related page](https://example.com/related)", result.Text, StringComparison.Ordinal);
    Assert.DoesNotContain("junk navigation", result.Text, StringComparison.Ordinal);
    JsonElement details = result.Details!.Value;
    Assert.Equal("completed", details.GetProperty("status").GetString());
    Assert.Equal("html", details.GetProperty("kind").GetString());
    Assert.Equal(200, details.GetProperty("statusCode").GetInt32());
    Assert.Equal("https://example.com/source", details.GetProperty("url").GetString());
    Assert.Equal("Example article", details.GetProperty("title").GetString());
    Assert.False(details.GetProperty("previewOmitted").GetBoolean());
    Assert.Equal(JsonValueKind.Null, details.GetProperty("spillPath").ValueKind);
  }

  [Fact]
  public async Task PdfTextIsExtractedAndScannedPdfsAreUnsupported() {
    var readable = new WebPageVisit("https://example.com/final", 200, "application/pdf", "", null,
      WebReadTestPdf.WithTextLayer(), null, true);
    ToolOutput text = await Present(readable);
    Assert.False(text.IsError, text.Text);
    Assert.Contains("web_read completed: https://example.com/final", text.Text, StringComparison.Ordinal);
    Assert.Contains("Hello web read", text.Text, StringComparison.Ordinal);
    var scanned = new WebPageVisit("https://example.com/final", 200, "application/pdf", "", null,
      WebReadTestPdf.WithoutTextLayer(), null, true);
    ToolOutput unsupported = await Present(scanned);
    Assert.True(unsupported.IsError);
    Assert.StartsWith("web_read unsupported: https://example.com/final", unsupported.Text, StringComparison.Ordinal);
    Assert.Contains("no text layer; scanned PDFs are unsupported", unsupported.Text, StringComparison.Ordinal);
  }

  private static Task<ToolOutput> Present(WebPageVisit visit) =>
    WebReadOutput.Present(visit, "https://example.com/source", CancellationToken.None);
}
