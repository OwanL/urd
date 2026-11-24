using Microsoft.Playwright;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class WebPageVisitorLiveTests {
  [Fact]
  [Trait("Category", "LiveWeb")]
  public async Task BurstOfReadsWaitsForAvailablePages() {
    if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB") != "1")
      return;

    await using var browser = new WebBrowser();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<int>[] reads = Enumerable.Range(0, 12).Select(index => browser.WithPageAsync(async (_, cancellation) => {
      await release.Task.WaitAsync(cancellation);
      return index;
    }, deadline.Token)).ToArray();
    release.SetResult();
    Assert.Equal(Enumerable.Range(0, reads.Length), await Task.WhenAll(reads));
  }

  [Fact]
  [Trait("Category", "LiveWeb")]
  public async Task HtmlNavigationCapturesTheRenderedPage() {
    if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB") != "1")
      return;

    const string url = "https://fixture.example/page";
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    await using var browser = new WebBrowser();
    WebPageVisit visit = await browser.WithPageAsync(async (page, cancellation) => {
      await page.RouteAsync(url, route => route.FulfillAsync(new RouteFulfillOptions {
        ContentType = "text/html",
        Body = "<!doctype html><html><head><title>Fixture page</title></head><body><main>Navigation fixture</main></body></html>"
      }));
      return await WebPageVisitor.VisitAsync(page, url, cancellation);
    }, deadline.Token);

    Assert.Equal(url, visit.FinalUrl);
    Assert.Equal(200, visit.StatusCode);
    Assert.Equal("Fixture page", visit.Title);
    Assert.NotNull(visit.Html);
    Assert.Contains("Navigation fixture", visit.Html, StringComparison.Ordinal);
    Assert.True(visit.TextSettled);
  }
}
