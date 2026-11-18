using System.Text.Json;
using Microsoft.Playwright;
using Urd.Backend.Tests.Support;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class WebReadLiveTests {
  [Fact]
  [Trait("Category", "LiveWeb")]
  public async Task AttachmentNavigationAndAbortAreNotEgressRefusals() {
    if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB") != "1")
      return;

    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    await using var browser = new WebBrowser();
    const string url = "https://fixture.example/attachment";
    await browser.WithPageAsync(async (page, cancellation) => {
      await page.RouteAsync(url, route => route.FulfillAsync(new RouteFulfillOptions {
        Status = 200,
        ContentType = "application/pdf",
        Headers = new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=sample.pdf" },
        BodyBytes = WebReadTestPdf.WithTextLayer()
      }));
      WebPageVisit visit = await WebPageVisitor.VisitAsync(page, url, cancellation);
      ToolOutput result = await WebReadOutput.Present(visit, url, cancellation);
      Assert.False(result.IsError, result.Text);
      Assert.Contains("Hello web read", result.Text, StringComparison.Ordinal);
      Assert.Equal("pdf", result.Details!.Value.GetProperty("kind").GetString());
      return true;
    }, deadline.Token);
    await browser.WithPageAsync(async (page, cancellation) => {
      await page.RouteAsync(url, route => route.FulfillAsync(new RouteFulfillOptions {
        Status = 200,
        ContentType = "text/plain",
        BodyBytes = WebReadTestPdf.WithTextLayer()
      }));
      WebPageVisit visit = await WebPageVisitor.VisitAsync(page, url, cancellation);
      ToolOutput result = await WebReadOutput.Present(visit, url, cancellation);
      Assert.False(result.IsError, result.Text);
      Assert.Contains("Hello web read", result.Text, StringComparison.Ordinal);
      return true;
    }, deadline.Token);
    await browser.WithPageAsync(async (page, cancellation) => {
      await page.RouteAsync(url, route => route.FulfillAsync(new RouteFulfillOptions {
        Status = 403,
        ContentType = "application/pdf",
        Headers = new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=refused.pdf" },
        BodyBytes = WebReadTestPdf.WithTextLayer()
      }));
      WebPageVisit visit = await WebPageVisitor.VisitAsync(page, url, cancellation);
      ToolOutput result = await WebReadOutput.Present(visit, url, cancellation);
      Assert.True(result.IsError);
      Assert.Equal("blocked", result.Details!.Value.GetProperty("status").GetString());
      Assert.Equal(403, visit.StatusCode);
      return true;
    }, deadline.Token);
    await browser.WithPageAsync(async (page, cancellation) => {
      await page.RouteAsync(url, route => route.AbortAsync());
      PlaywrightException error = await Assert.ThrowsAsync<PlaywrightException>(() => WebPageVisitor.VisitAsync(page, url, cancellation));
      Assert.Contains("ERR_", error.Message, StringComparison.Ordinal);
      return true;
    }, deadline.Token);
  }

  [Fact]
  [Trait("Category", "LiveWeb")]
  public async Task ChallengeDoesNotWaitForStalledResourcesAndNavigationCancels() {
    if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB") != "1")
      return;

    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var browser = new WebBrowser();
    const string url = "https://fixture.example/challenge";
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    try {
      await browser.WithPageAsync(async (page, cancellation) => {
        await page.RouteAsync("https://fixture.example/image", async route => {
          await release.Task;
          await route.AbortAsync();
        });
        await page.RouteAsync(url, route => route.FulfillAsync(new RouteFulfillOptions {
          ContentType = "text/html",
          Body = "<title>Just a moment...</title><body>Checking your browser<img src='/image'></body>"
        }));
        WebPageVisit visit = await WebPageVisitor.VisitAsync(page, url, cancellation);
        ToolOutput result = await WebReadOutput.Present(visit, url, cancellation);
        Assert.True(result.IsError);
        Assert.Equal("blocked", result.Details!.Value.GetProperty("status").GetString());
        release.TrySetResult();
        return true;
      }, deadline.Token);
    } finally {
      release.TrySetResult();
    }

    using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    try {
      Task<WebPageVisit> visit = browser.WithPageAsync(async (page, cancellation) => {
        await page.RouteAsync(url, async route => {
          entered.TrySetResult();
          await unblock.Task;
          await route.AbortAsync();
        });
        return await WebPageVisitor.VisitAsync(page, url, cancellation);
      }, cancelled.Token);
      await entered.Task.WaitAsync(deadline.Token);
      await cancelled.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => visit.WaitAsync(TimeSpan.FromSeconds(8)));
    } finally {
      unblock.TrySetResult();
    }
  }

  [Fact]
  [Trait("Category", "LiveWeb")]
  public async Task PublicPdfIsReadThroughRestrictedBrowserTransport() {
    if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("URD_TEST_LIVE_WEB") != "1")
      return;

    const string url = "https://www.w3.org/WAI/ER/tests/xhtml/testfiles/resources/pdf/dummy.pdf";
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    await using var browser = new WebBrowser();
    var context = new ToolContext(".", Guid.NewGuid(), "web-read-live", false, _ => { });
    ToolOutput blocked = await new WebReadTool(browser).RunAsync(
      JsonSerializer.SerializeToElement(new { url = "http://127.0.0.1:8080/" }), context, deadline.Token);
    Assert.True(blocked.IsError);
    Assert.True(blocked.Details.HasValue, blocked.Text);
    Assert.Equal("blocked", blocked.Details.Value.GetProperty("status").GetString());
    Assert.Contains("public-web egress restriction", blocked.Text, StringComparison.Ordinal);
    ToolOutput result = await new WebReadTool(browser).RunAsync(JsonSerializer.SerializeToElement(new { url }), context, deadline.Token);
    Assert.False(result.IsError, result.Text);
    Assert.Equal("pdf", result.Details!.Value.GetProperty("kind").GetString());
    Assert.Contains("Dummy PDF file", result.Text, StringComparison.OrdinalIgnoreCase);
  }
}
