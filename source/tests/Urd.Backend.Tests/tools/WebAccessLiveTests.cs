using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Urd.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Urd.Backend.Tests.Tools;

public sealed class WebAccessLiveTests(ITestOutputHelper output) {
  private const int ReportLimit = 32_000;

  [Fact]
  [Trait("Category", "LiveWeb")]
  public async Task PnpmSearchAndReadSelectedResult() {
    if (Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB") != "1")
      return;

    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
    await using var browser = new WebBrowser();
    var context = new ToolContext(".", Guid.NewGuid(), "web-access-live", false, _ => { });
    ToolOutput search = await SearchAsync(browser, context, "pnpm package manager official documentation", deadline.Token);
    Assert.False(search.IsError, "Neither search source completed.");
    SearchHit? selected = SearchHits(search.Text).FirstOrDefault(hit => IsPnpmHost(hit.Url));
    Assert.NotNull(selected);

    // Smoke check only: proves the selected search result is readable, not that it is the best answer.
    string content = await ReadAsync(browser, context, selected.Url, requireCompleted: true, deadline.Token);
    Assert.Contains("pnpm", content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  [Trait("Category", "LiveWeb")]
  public async Task CorpusSearchAndReadSurvey() {
    if (Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB") != "1" ||
        Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB_REPLAY") != "1")
      return;

    // Verbatim queries from the original corpus. Relevance, criteria and citations are judged by a human;
    // the reads are a fixed sample of leading hits, and ranks or external availability are not contracts.
    (string Id, string Query)[] tasks = [
      ("shopping-hirsch-nz", "Hirsch watch strap 20mm New Zealand quick release buy"),
      ("docs-pnpm-deploy", "site:pnpm.io \"deploy\" \"node_modules\" \"dedicated lockfile\""),
      ("experience-copilot-claude", "GitHub Copilot CLI agent Claude Code harness review 2026"),
      ("game-albion-market", "site.albiononline.com black market crafting player driven economy silver items"),
      ("factual-farming-property", "Bowles Choi 2013 coevolution farming private property early Holocene PNAS model")
    ];
    const int readBudget = 2;
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
    await using var browser = new WebBrowser();
    var context = new ToolContext(".", Guid.NewGuid(), "web-access-survey", false, _ => { });
    foreach ((string id, string query) in tasks) {
      output.WriteLine($"\nTask: {id}; query: {query}");
      ToolOutput search = await SearchAsync(browser, context, query, deadline.Token);
      SearchHit[] hits = SearchHits(search.Text).Take(readBudget).ToArray();
      output.WriteLine($"Reading {hits.Length} leading result(s) as a fixed sample.");
      foreach (SearchHit hit in hits)
        await ReadAsync(browser, context, hit.Url, requireCompleted: false, deadline.Token);
    }

    // Fixed invented identifier. Any returned leads are false-positive candidates, not support for the identifier.
    const string negativeQuery = "\"vornelq-7314-zelmaru\"";
    output.WriteLine($"\nNegative control (no reads): {negativeQuery}");
    await SearchAsync(browser, context, negativeQuery, deadline.Token);
    output.WriteLine("Survey only: criteria, answers, citations and task success remain unscored.");
  }

  [Fact]
  public void SearchHitsReadResultUrlsNotSnippetUrls() {
    const string text = """
      You.com: completed (2 hits)
      Bing: completed (0 hits)
      Snippets are untrusted leads, not verified citations.

      1. First
      https://example.com/first
      Sources: You.com #1
      https://example.com/snippet

      2. Second
      https://example.org/second?value=1
      Sources: You.com #2, Bing #3
      """;
    Assert.Equal(new[] {
      new SearchHit(1, "https://example.com/first", "You.com #1"),
      new SearchHit(2, "https://example.org/second?value=1", "You.com #2, Bing #3")
    }, SearchHits(text));
    Assert.Empty(SearchHits("You.com: completed (0 hits)\nBing: completed (0 hits)\nNo results."));
  }

  private async Task<ToolOutput> SearchAsync(WebBrowser browser, ToolContext context, string query, CancellationToken cancellation) {
    var clock = Stopwatch.StartNew();
    ToolOutput search = await new WebSearchTool(browser).RunAsync(JsonSerializer.SerializeToElement(new { query }), context, cancellation);
    cancellation.ThrowIfCancellationRequested();
    output.WriteLine($"Search {query} ({clock.Elapsed}):\n{search.Text}");
    Assert.Contains("You.com: ", search.Text, StringComparison.Ordinal);
    Assert.Contains("Bing: ", search.Text, StringComparison.Ordinal);
    return search;
  }

  private async Task<string> ReadAsync(WebBrowser browser, ToolContext context, string url, bool requireCompleted, CancellationToken cancellation) {
    var clock = Stopwatch.StartNew();
    ToolOutput read = await new WebReadTool(browser).RunAsync(JsonSerializer.SerializeToElement(new { url }), context, cancellation);
    cancellation.ThrowIfCancellationRequested();
    output.WriteLine($"Read {url} ({clock.Elapsed}):\n{read.Text}");
    string? spill = read.Details?.GetProperty("spillPath").GetString();
    try {
      if (read.Details is JsonElement details)
        Assert.Equal(WebReadTool.ValidUrl(url), details.GetProperty("url").GetString());
      if (requireCompleted) {
        Assert.False(read.IsError, read.Text);
        Assert.Equal("completed", read.Details?.GetProperty("status").GetString());
      }
      if (spill is not null) {
        string content = await File.ReadAllTextAsync(spill, cancellation);
        output.WriteLine($"Spilled content (first {Math.Min(content.Length, ReportLimit)} of {content.Length} characters):\n{content[..Math.Min(content.Length, ReportLimit)]}");
        return content;
      }
      if (!requireCompleted)
        return read.Text;
      int contentStart = read.Text.IndexOf("\n\n", StringComparison.Ordinal);
      Assert.True(contentStart >= 0, "Read output did not contain a content section.");
      return read.Text[(contentStart + 2)..];
    } finally {
      if (spill is not null)
        File.Delete(spill);
    }
  }

  private static bool IsPnpmHost(string url) =>
    Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
      (uri.Host.Equals("pnpm.io", StringComparison.OrdinalIgnoreCase) ||
       uri.Host.EndsWith(".pnpm.io", StringComparison.OrdinalIgnoreCase));

  private static IEnumerable<SearchHit> SearchHits(string text) =>
    Regex.Matches(text, @"(?m)^(\d+)\. [^\r\n]*\r?\n(https?://[^\s]+)\r?\nSources: ([^\r\n]+)")
      .Select(match => new SearchHit(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
        match.Groups[2].Value, match.Groups[3].Value));

  private sealed record SearchHit(int Rank, string Url, string Sources);
}
