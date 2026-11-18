using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Drives one browser page through the restricted transport and captures the document exactly once.
internal static class WebPageVisitor {
  public static async Task<WebPageVisit> VisitAsync(IPage page, string url, CancellationToken cancellation) {
    WebNavigationCapture capture = await WebNavigationCapture.StartAsync(page, cancellation).ConfigureAwait(false);
    IResponse? response = await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded })
      .WaitAsync(cancellation).ConfigureAwait(false);
    if (capture.Captured.IsCompleted)
      return await capture.Captured.ConfigureAwait(false);
    string contentType = response?.Headers.GetValueOrDefault("content-type", "") ?? "";
    if (response is null)
      return new WebPageVisit(page.Url, null, contentType, "", null, null, null, true);
    if (response.Status == 451 && response.Headers.ContainsKey("x-urd-web-blocked"))
      return new WebPageVisit(page.Url, response.Status, contentType, "", null, null,
        "The destination was refused by the public-web egress restriction.", true);
    switch (WebPageVisit.Transport(contentType)) {
      case WebPageVisit.WebFetchKind.HtmlDocument: {
        bool settled = await SettleAsync(page, cancellation).ConfigureAwait(false);
        string html = await page.ContentAsync().WaitAsync(cancellation).ConfigureAwait(false);
        return new WebPageVisit(page.Url, response.Status, contentType, await TitleAsync(page, cancellation).ConfigureAwait(false),
          html, null, null, settled);
      }
      default:
        byte[] body = await response.BodyAsync().WaitAsync(cancellation).ConfigureAwait(false);
        return new WebPageVisit(page.Url, response.Status, contentType, "", null, body, null, true);
    }
  }

  // Completion means the document rendered, not global network idle: wait for a short bounded
  // interval of unchanged page text, and say so when the budget ran out while content kept changing.
  private static async Task<bool> SettleAsync(IPage page, CancellationToken cancellation) {
    int previous = -1;
    for (int attempt = 0; attempt < WebReadLimits.SettleAttempts; attempt++) {
      int current = await page.EvaluateAsync<int>("(document.body ? document.body.innerText : '').length")
        .WaitAsync(cancellation).ConfigureAwait(false);
      if (current == previous)
        return true;
      previous = current;
      await Task.Delay(WebReadLimits.SettlePollMs, cancellation).ConfigureAwait(false);
    }
    return false;
  }

  private static async Task<string> TitleAsync(IPage page, CancellationToken cancellation) {
    try {
      return await page.TitleAsync().WaitAsync(cancellation).ConfigureAwait(false);
    } catch (PlaywrightException) {
      return "";
    }
  }
}
