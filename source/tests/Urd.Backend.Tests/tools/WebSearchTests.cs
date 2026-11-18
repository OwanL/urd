using System.Text;
using System.Text.Json;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class WebSearchTests {
  private static readonly string[] ExpectedMerged = ["First", "Second", "Fourth", "Third"];
  private static readonly string[] ExpectedRanked = ["B1", "Y2", "B2"];
  [Fact]
  public void MergesRanksAndPreservesBothSourcesWithoutCollapsingDistinctDocuments() {
    string redirect = "https://www.bing.com/ck/a?u=a1" + Convert.ToBase64String(Encoding.UTF8.GetBytes("https://EXAMPLE.com:443/a?item=1&utm_source=bing#part")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    WebSearchSourceResult you = Completed(
      new("First", "https://example.com/a?item=1#part", "snippet", null, 1),
      new("Second", "https://example.com/a?item=2#part", "", null, 2),
      new("Third", "https://example.com/a?item=1#other", "", null, 3));
    WebSearchSourceResult bing = Completed(
      new("Duplicate", redirect, "other", null, 1),
      new("Fourth", "http://example.com/a?item=1#part", "", null, 2));
    IReadOnlyList<WebSearchMergedHit> hits = WebSearchResults.Merge(you, bing);
    Assert.Equal(ExpectedMerged, hits.Select(hit => hit.Hit.Title));
    Assert.Equal(new[] { new WebSearchProvenance("You.com", 1), new WebSearchProvenance("Bing", 1) }, hits[0].Sources);
    Assert.Equal("https://example.com/a?item=1#part", hits[0].Hit.Url);
    Assert.Equal("https://EXAMPLE.com:443/a?item=1&utm_source=bing#part", WebSearchResults.Destination(redirect));
  }

  [Fact]
  public void InterleavesOriginalRanksEvenWhenOneSourceSkippedAnInvalidHit() {
    WebSearchSourceResult you = Completed(new WebSearchHit("Y2", "https://example.com/2", "", null, 2));
    WebSearchSourceResult bing = Completed(new("B1", "https://example.org/1", "", null, 1), new("B2", "https://example.org/2", "", null, 2));
    Assert.Equal(ExpectedRanked, WebSearchResults.Merge(you, bing).Select(hit => hit.Hit.Title));
  }

  [Fact]
  public void BingParsesOnlyOrganicCardsAndRecognizesEmptyChallengeAndDrift() {
    const string html = """
      <html><title>Results</title><ul id="b_results">
      <li class="b_ad"><h2><a href="https://ads.example/">Ad</a></h2></li>
      <li class="b_algo"><h2><a href="https://example.com/a">Organic <b>A</b></a></h2><div class="b_caption"><p>First snippet</p></div><time datetime="2026-01-02"></time></li>
      <li class="b_algo"><h2><a href="https://example.com/b">Organic B</a></h2><div class="b_caption"><p>Second snippet</p></div></li>
      </ul></html>
      """;
    WebSearchSourceResult result = WebSearchBing.Parse(html);
    Assert.Equal(WebSearchStatus.Completed, result.Status);
    Assert.Equal(new WebSearchHit("Organic A", "https://example.com/a", "First snippet", "2026-01-02", 1), result.Hits[0]);
    Assert.Equal(2, result.Hits.Count);
    Assert.Equal(WebSearchStatus.Completed, WebSearchBing.Parse("<ul id='b_results'><li class='b_no'>There are no results for x</li></ul>").Status);
    Assert.Equal(WebSearchStatus.Blocked, WebSearchBing.Parse("<div id='b_captcha'></div>").Status);
    Assert.Equal(WebSearchStatus.Failed, WebSearchBing.Parse("<html><body>Unexpected layout</body></html>").Status);
    Assert.Equal(WebSearchStatus.Failed, WebSearchBing.Parse("<ul id='b_results'><li class='b_algo'>changed card</li></ul>").Status);
    Assert.Equal(WebSearchStatus.Failed, WebSearchBing.Parse("<ul id='b_results'><li class='b_algo'><h2><a href='https://www.bing.com/ck/a?u=unknown'>Unknown redirect</a></h2></li></ul>").Status);
  }

  [Fact]
  public void ParsesRecordedBingOrganicCardAndDecodesItsRedirect() {
    // Reduced from a public Bing results page; tracking values omitted.
    const string html = """
      <ul id="b_results"><li class="b_algo" data-id iid=SERP.5323>
      <div class="b_tpcn"><a class="tilk" href="https://www.bing.com/ck/a?u=a1aHR0cHM6Ly9kb3RuZXQubWljcm9zb2Z0LmNvbS9lbi11cy9kb3dubG9hZA">microsoft.com</a></div>
      <h2><a href="https://www.bing.com/ck/a?u=a1aHR0cHM6Ly9kb3RuZXQubWljcm9zb2Z0LmNvbS9lbi11cy9kb3dubG9hZA"><strong>Download</strong> .NET (Linux, macOS, and Windows) | .NET</a></h2>
      <div class="b_caption"><p class="b_lineclamp2"><span class="news_dt">Nov 10, 2026</span>&nbsp;&#0183;&#32;Free downloads for building and running .NET apps.</p></div>
      </li></ul>
      """;
    WebSearchSourceResult bing = WebSearchBing.Parse(html);
    WebSearchMergedHit hit = Assert.Single(WebSearchResults.Merge(Completed(), bing));
    Assert.Equal("https://dotnet.microsoft.com/en-us/download", hit.Hit.Url);
    Assert.Equal("Download .NET (Linux, macOS, and Windows) | .NET", hit.Hit.Title);
    Assert.Contains("Free downloads", hit.Hit.Snippet);
  }

  [Fact]
  public async Task StartsBothSourcesBeforeAwaitingAndReturnsPartialResults() {
    TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var tool = new WebSearchTool(async (query, token) => {
      Assert.Equal("same query", query);
      await release.Task.WaitAsync(token);
      return Completed(new WebSearchHit("You result", "https://example.com/", "lead", null, 1));
    }, (query, _) => {
      Assert.Equal("same query", query);
      started.SetResult();
      return Task.FromResult(new WebSearchSourceResult(WebSearchStatus.Blocked, [], "challenge"));
    });
    Task<ToolOutput> pending = Run(tool, " same query ");
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.False(pending.IsCompleted);
    release.SetResult();
    ToolOutput output = await pending;
    Assert.False(output.IsError);
    Assert.Contains("Partial results", output.Text);
    Assert.Contains("Bing: blocked", output.Text);
    Assert.Contains("https://example.com/", output.Text);
  }

  [Fact]
  public async Task BothFailuresAreNotNoResultsAndCompletedEmptyIsDistinct() {
    var failed = new WebSearchSourceResult(WebSearchStatus.Failed, [], "offline");
    var empty = Completed();
    ToolOutput bothFailed = await Run(new WebSearchTool((_, _) => Task.FromResult(failed), (_, _) => Task.FromResult(failed)), "q");
    Assert.True(bothFailed.IsError);
    Assert.DoesNotContain("No results", bothFailed.Text);
    ToolOutput bothEmpty = await Run(new WebSearchTool((_, _) => Task.FromResult(empty), (_, _) => Task.FromResult(empty)), "q");
    Assert.False(bothEmpty.IsError);
    Assert.Contains("No results", bothEmpty.Text);
  }

  [Fact]
  public async Task CancellationReachesBothSources() {
    int started = 0;
    TaskCompletionSource bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task<WebSearchSourceResult> Wait(string _, CancellationToken token) {
      if (Interlocked.Increment(ref started) == 2)
        bothStarted.SetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, token);
      return Completed();
    }
    using var cancellation = new CancellationTokenSource();
    Task<ToolOutput> pending = Run(new WebSearchTool(Wait, Wait), "q", cancellation.Token);
    await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cancellation.CancelAsync();
    ToolOutput output = await pending.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.True(output.IsError);
    Assert.Contains("cancelled", output.Text);
  }

  private static WebSearchSourceResult Completed(params WebSearchHit[] hits) => new(WebSearchStatus.Completed, hits);
  private static Task<ToolOutput> Run(WebSearchTool tool, string query, CancellationToken cancellation = default) =>
    tool.RunAsync(JsonSerializer.SerializeToElement(new { query }), new ToolContext("", Guid.NewGuid(), "call", false, _ => { }), cancellation);
}
