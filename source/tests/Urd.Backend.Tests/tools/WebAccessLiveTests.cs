using System.Diagnostics;
using System.Text.Json;
using Urd.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace Urd.Backend.Tests.Tools;

public sealed class WebAccessLiveTests(ITestOutputHelper output) {
  [Fact]
  [Trait("Category", "LiveWeb")]
  public async Task PnpmSearchAndVerifiedRead() {
    if (Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB") != "1")
      return;

    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
    await using var browser = new WebBrowser();
    await SearchAndReadAsync(browser, "pnpm package manager official documentation", deadline.Token);
  }

  [Fact]
  [Trait("Category", "LiveWeb")]
  public async Task FiveTaskReplay() {
    if (Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB") != "1" ||
        Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB_REPLAY") != "1")
      return;

    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
    await using var browser = new WebBrowser();
    string[] queries = ["pnpm package manager official documentation", "pnpm installation documentation",
      "pnpm workspace documentation", "pnpm cli documentation", "pnpm lockfile documentation"];
    await Task.WhenAll(queries.Select(query => SearchAndReadAsync(browser, query, deadline.Token)));
  }

  private async Task SearchAndReadAsync(WebBrowser browser, string query, CancellationToken cancellation) {
    var clock = Stopwatch.StartNew();
    var context = new ToolContext(".", Guid.NewGuid(), "web-access-live", false, _ => { });
    ToolOutput search = await new WebSearchTool(browser).RunAsync(JsonSerializer.SerializeToElement(new { query }), context, cancellation);
    output.WriteLine($"Search {query} ({clock.Elapsed}):\n{search.Text}");
    Assert.Contains("You.com: ", search.Text, StringComparison.Ordinal);
    Assert.Contains("Bing: ", search.Text, StringComparison.Ordinal);
    Assert.False(search.IsError, "Neither search source completed.");
    string? url = search.Text.Split('\n', StringSplitOptions.TrimEntries)
      .FirstOrDefault(line => Uri.TryCreate(line, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme is "http" or "https" && (uri.Host.Equals("pnpm.io", StringComparison.OrdinalIgnoreCase) ||
          uri.Host.EndsWith(".pnpm.io", StringComparison.OrdinalIgnoreCase)));
    Assert.True(url is not null, "Search did not return a pnpm.io result to verify.");
    ToolOutput read = await new WebReadTool(browser).RunAsync(JsonSerializer.SerializeToElement(new { url }), context, cancellation);
    output.WriteLine($"Read {url} ({clock.Elapsed}):\n{read.Text}");
    string? spill = read.Details?.GetProperty("spillPath").GetString();
    try {
      Assert.False(read.IsError, "The returned search result could not be read.");
      Assert.Equal("completed", read.Details?.GetProperty("status").GetString());
      Assert.Equal(url, read.Details?.GetProperty("url").GetString());
      int contentStart = read.Text.IndexOf("\n\n", StringComparison.Ordinal);
      Assert.True(contentStart >= 0, "Read output did not contain a content section.");
      string content = spill is null ? read.Text[(contentStart + 2)..] : await File.ReadAllTextAsync(spill, cancellation);
      Assert.Contains("pnpm", content, StringComparison.OrdinalIgnoreCase);
    } finally {
      if (spill is not null)
        File.Delete(spill);
    }
  }
}
