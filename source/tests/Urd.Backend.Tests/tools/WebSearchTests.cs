using System.Text;
using System.Text.Json;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class WebSearchTests {
  private static readonly string[] ExpectedMerged = ["First", "Second", "Fourth", "Third"];
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

  private static WebSearchSourceResult Completed(params WebSearchHit[] hits) => new(WebSearchStatus.Completed, hits);
  private static Task<ToolOutput> Run(WebSearchTool tool, string query, CancellationToken cancellation = default) =>
    tool.RunAsync(JsonSerializer.SerializeToElement(new { query }), new ToolContext("", Guid.NewGuid(), "call", false, _ => { }), cancellation);
}
