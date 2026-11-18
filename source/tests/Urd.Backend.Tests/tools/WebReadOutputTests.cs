using System.Text;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

// Settlement, bounds and spill read-back for one captured web page.
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
  public async Task HtmlAttachmentBodyIsExtractedAndChallengesAreStillRecognized() {
    var visit = new WebPageVisit("https://example.com/report", 200, "text/html", "", null,
      Encoding.UTF8.GetBytes("<main><p>Downloaded report</p></main>"), null, true);
    ToolOutput result = await Present(visit);
    Assert.False(result.IsError, result.Text);
    Assert.Contains("Downloaded report", result.Text, StringComparison.Ordinal);
    var challenge = visit with { Body = Encoding.UTF8.GetBytes("<body>cf-chl-1: verify you are human</body>") };
    Assert.Equal("blocked", (await Present(challenge)).Details!.Value.GetProperty("status").GetString());
  }

  [Fact]
  public async Task LargeHtmlReadSpillsFullMarkdownAndTheSpillReadsBack() {
    string html = "<html><head><title>Spilled page</title></head><body><main>" +
      string.Join("", Enumerable.Range(1, 40).Select(n => $"<p>Paragraph {n} {new string('y', 300)}</p>")) +
      "</main></body></html>";
    var visit = new WebPageVisit("https://example.com/final", 200, "text/html", "Spilled page", html, null, null, true);
    ToolOutput result = await Present(visit);
    Assert.False(result.IsError, result.Text);
    string spillPath = result.Details.GetValueOrDefault().GetProperty("spillPath").GetString()!;
    Assert.True(result.Details.GetValueOrDefault().GetProperty("previewOmitted").GetBoolean());
    try {
      Assert.True(File.Exists(spillPath), spillPath);
      string expected = WebPageExtractor.Extract(html, "https://example.com/final").Markdown;
      Assert.Equal(expected, await File.ReadAllTextAsync(spillPath));
      int spillBytes = result.Details.GetValueOrDefault().GetProperty("spillBytes").GetInt32();
      Assert.Equal(Encoding.UTF8.GetByteCount(expected), spillBytes);
      Assert.Contains("Paragraph 1 ", result.Text, StringComparison.Ordinal);
      Assert.DoesNotContain("Paragraph 39 ", result.Text, StringComparison.Ordinal);
      Assert.Contains($"[Full text: {spillPath} ", result.Text, StringComparison.Ordinal);
      Assert.Contains($"({spillBytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes). Source: https://example.com/source", result.Text, StringComparison.Ordinal);
    } finally {
      File.Delete(spillPath);
    }
  }

  [Fact]
  public async Task PlainTextPassesThroughAndJsonIsPrettyPrinted() {
    var text = new WebPageVisit("https://example.com/final", 200, "text/plain; charset=utf-8", "", null,
      Encoding.UTF8.GetBytes("alpha\nbeta"), null, true);
    ToolOutput plain = await Present(text);
    Assert.False(plain.IsError, plain.Text);
    Assert.Contains("web_read completed: https://example.com/final\n\nalpha\nbeta", plain.Text, StringComparison.Ordinal);
    var json = new WebPageVisit("https://example.com/final", 200, "application/json; charset=utf-8", "", null,
      Encoding.UTF8.GetBytes("""{"b":1,"a":[true,null]}"""), null, true);
    ToolOutput pretty = await Present(json);
    Assert.False(pretty.IsError, pretty.Text);
    string normalized = pretty.Text.Replace("\r\n", "\n");
    Assert.Contains("\"b\": 1,\n  \"a\": [\n    true,\n    null\n  ]", normalized, StringComparison.Ordinal);
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

  [Fact]
  public void PdfExtractionStopsAtTheOutputLimit() {
    (string text, bool hasTextLayer, bool truncated) = PdfText.Read(WebReadTestPdf.WithTextLayer(), 8, CancellationToken.None);
    Assert.True(hasTextLayer);
    Assert.True(truncated);
    Assert.Equal("Hello we", text);
  }

  [Fact]
  public async Task SniffingRoutesOctetStreamsAndRejectsUnknownBinaries() {
    var pdfBytes = new WebPageVisit("https://example.com/final", 200, "application/octet-stream", "", null,
      WebReadTestPdf.WithTextLayer(), null, true);
    ToolOutput pdf = await Present(pdfBytes);
    Assert.False(pdf.IsError, pdf.Text);
    Assert.Contains("Hello web read", pdf.Text, StringComparison.Ordinal);
    var mislabeled = new WebPageVisit("https://example.com/final", 200, "text/plain", "", null,
      WebReadTestPdf.WithTextLayer(), null, true);
    Assert.Contains("Hello web read", (await Present(mislabeled)).Text, StringComparison.Ordinal);
    var png = new WebPageVisit("https://example.com/final", 200, "image/png", "", null, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x0A], null, true);
    ToolOutput rejected = await Present(png);
    Assert.True(rejected.IsError);
    Assert.StartsWith("web_read unsupported: https://example.com/final", rejected.Text, StringComparison.Ordinal);
    Assert.Equal("unsupported", rejected.Details!.Value.GetProperty("status").GetString());
  }

  [Fact]
  public async Task HttpErrorCodesSettleDistinctly() {
    var blocked = new WebPageVisit("https://example.com/final", 403, "text/html", "Forbidden", "<html></html>", null, null, true);
    ToolOutput refusal = await Present(blocked);
    Assert.True(refusal.IsError);
    Assert.StartsWith("web_read blocked: https://example.com/final", refusal.Text, StringComparison.Ordinal);
    Assert.Equal("blocked", refusal.Details!.Value.GetProperty("status").GetString());

    var limited = new WebPageVisit("https://example.com/final", 429, "text/html", "Busy", "<html></html>", null, null, true);
    Assert.StartsWith("web_read rate-limited:", (await Present(limited)).Text, StringComparison.Ordinal);

    foreach (int status in new[] { 404, 500 }) {
      var failed = new WebPageVisit("https://example.com/final", status, "text/html", "Oops", "<html></html>", null, null, true);
      ToolOutput settled = await Present(failed);
      Assert.True(settled.IsError);
      Assert.StartsWith($"web_read failed: https://example.com/final\nThe server returned HTTP {status}.", settled.Text, StringComparison.Ordinal);
    }
  }

  [Fact]
  public async Task EgressRefusalAndBrowserChallengesSettleAsBlocked() {
    var refused = new WebPageVisit("https://127.0.0.1:8443/", 451, "", "", null, null,
      "The destination was refused by the public-web egress restriction.", true);
    ToolOutput refusal = await Present(refused);
    Assert.True(refusal.IsError);
    Assert.StartsWith("web_read blocked: https://127.0.0.1:8443/", refusal.Text, StringComparison.Ordinal);
    Assert.Contains("public-web egress restriction", refusal.Text, StringComparison.Ordinal);

    var challenge = new WebPageVisit("https://example.com/gated", 200, "text/html", "Just a moment...",
      "<html><head><title>Just a moment...</title></head><body><div>Checking your browser.</div></body></html>", null, null, true);
    ToolOutput gate = await Present(challenge);
    Assert.True(gate.IsError);
    Assert.StartsWith("web_read blocked: https://example.com/gated", gate.Text, StringComparison.Ordinal);
    Assert.Contains("browser challenge", gate.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ChallengePhrasesInOrdinaryContentAreNotBlocked() {
    var article = new WebPageVisit("https://example.com/article", 200, "text/html", "Browser challenges explained",
      "<main><p>Just a moment: attention required while configuring challenge-platform.</p></main>", null, null, true);
    ToolOutput result = await Present(article);
    Assert.False(result.IsError, result.Text);
    Assert.Equal("completed", result.Details!.Value.GetProperty("status").GetString());
  }

  [Fact]
  public async Task CancelledOutputDoesNotStartExtraction() {
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();
    var visit = new WebPageVisit("https://example.com/final", 200, "application/pdf", "", null,
      WebReadTestPdf.WithTextLayer(), null, true);
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WebReadOutput.Present(visit, visit.FinalUrl, cancellation.Token));
  }

  [Fact]
  public async Task NoReadableTextAndUnsettledContentAreReported() {
    var empty = new WebPageVisit("https://example.com/final", 200, "text/html", "", "<html><body></body></html>", null, null, true);
    ToolOutput hollow = await Present(empty);
    Assert.False(hollow.IsError, hollow.Text);
    Assert.StartsWith("web_read empty: https://example.com/final", hollow.Text, StringComparison.Ordinal);

    var unset = new WebPageVisit("https://example.com/final", 200, "text/html", "Moving", "<body><p>content</p></body>", null, null, false);
    ToolOutput unfinished = await Present(unset);
    Assert.False(unfinished.IsError, unfinished.Text);
    Assert.Contains("Page content kept changing; the text may be incomplete.", unfinished.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InputAboveTheDocumentLimitTruncatesExtractionWithoutLosingTheSpill() {
    string raw = "{\"v\":\"" + new string('x', 10 * 1024 * 1024 + 500_000) + "\"}";
    var visit = new WebPageVisit("https://example.com/final", 200, "application/json", "", null,
      Encoding.UTF8.GetBytes(raw), null, true);
    ToolOutput result = await Present(visit);
    Assert.False(result.IsError, result.Text);
    JsonElement details = result.Details!.Value;
    Assert.True(details.GetProperty("extractionTruncated").GetBoolean());
    Assert.True(details.GetProperty("previewOmitted").GetBoolean());
    Assert.Contains("[Extraction stopped at the 10MiB document limit.]", result.Text, StringComparison.Ordinal);
    string spillPath = details.GetProperty("spillPath").GetString()!;
    try {
      Assert.True(File.Exists(spillPath));
      string spilled = await File.ReadAllTextAsync(spillPath);
      Assert.True(Encoding.UTF8.GetByteCount(spilled) <= 10 * 1024 * 1024);
      Assert.DoesNotContain("Extraction", spilled, StringComparison.Ordinal);
    } finally {
      File.Delete(spillPath);
    }
  }

  [Fact]
  public async Task SpilledTextKeepsFullExtractedMarkdownBeyondTheInlineLimit() {
    string html = "<html><body><main><p>" + new string('w', 20 * 1024) + "</p></main></body></html>";
    var visit = new WebPageVisit("https://example.com/final", 200, "text/html", "", html, null, null, true);
    ToolOutput result = await Present(visit);
    string spillPath = result.Details!.Value.GetProperty("spillPath").GetString()!;
    try {
      Assert.Equal(new string('w', 20 * 1024), await File.ReadAllTextAsync(spillPath));
    } finally {
      File.Delete(spillPath);
    }
  }

  private static Task<ToolOutput> Present(WebPageVisit visit) =>
    WebReadOutput.Present(visit, "https://example.com/source", CancellationToken.None);
}
