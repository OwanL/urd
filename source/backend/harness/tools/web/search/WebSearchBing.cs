using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

internal static class WebSearchBing {
  internal static async Task<WebSearchSourceResult> SearchAsync(WebBrowser browser, string query, CancellationToken cancellation) {
    try {
      return await browser.WithPageAsync(async (page, browserCancellation) => {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(browserCancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try {
          string url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query) + "&count=5";
          IResponse? response = await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15000 }).WaitAsync(deadline.Token);
          if (response?.Status == 429)
            return new WebSearchSourceResult(WebSearchStatus.RateLimited, [], "Bing rate limited the search.");
          if (response?.Status is 401 or 403)
            return new WebSearchSourceResult(WebSearchStatus.Blocked, [], "Bing blocked the search.");
          if (response is null || response.Status >= 400)
            return new WebSearchSourceResult(WebSearchStatus.Failed, [], $"Bing navigation failed ({response?.Status.ToString(CultureInfo.InvariantCulture) ?? "no response"}).");
          string html = await page.ContentAsync().WaitAsync(deadline.Token);
          return Parse(html);
        } catch (OperationCanceledException) when (!browserCancellation.IsCancellationRequested) {
          return new WebSearchSourceResult(WebSearchStatus.TimedOut, [], "Bing search timed out.");
        } catch (TimeoutException) {
          return new WebSearchSourceResult(WebSearchStatus.TimedOut, [], "Bing search timed out.");
        }
      }, cancellation);
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      throw;
    } catch (TimeoutException error) {
      return new WebSearchSourceResult(WebSearchStatus.TimedOut, [], error.Message);
    } catch (Exception error) when (error is PlaywrightException or InvalidOperationException or IOException) {
      return new WebSearchSourceResult(WebSearchStatus.Failed, [], error.Message);
    }
  }

  internal static WebSearchSourceResult Parse(string html) {
    IDocument document = new HtmlParser().ParseDocument(html);
    string title = document.Title ?? "";
    if (document.QuerySelector("#b_captcha, #captcha, form[action*='challenge'], .b_captcha") is not null ||
        title.Contains("verify you are", StringComparison.OrdinalIgnoreCase) || title.Contains("access denied", StringComparison.OrdinalIgnoreCase))
      return new WebSearchSourceResult(WebSearchStatus.Blocked, [], "Bing presented an access challenge.");
    List<WebSearchHit> hits = [];
    int rank = 0;
    foreach (IElement card in document.QuerySelectorAll("#b_results > .b_algo")) {
      if (++rank > 5)
        break;
      IElement? anchor = card.QuerySelector("h2 a[href]");
      string? url = anchor?.GetAttribute("href");
      if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(anchor?.TextContent) ||
          !Uri.TryCreate(WebSearchResults.Destination(url), UriKind.Absolute, out Uri? destination) ||
          destination.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(destination.UserInfo) ||
          WebSearchResults.IsBingRedirect(destination) || destination.AbsoluteUri.Length > 2048)
        continue;
      string snippet = card.QuerySelector(".b_caption p")?.TextContent.Trim() ?? "";
      string? published = card.QuerySelector("time")?.GetAttribute("datetime");
      hits.Add(new WebSearchHit(anchor.TextContent.Trim(), url, snippet, published, rank));
    }
    if (hits.Count > 0)
      return new WebSearchSourceResult(WebSearchStatus.Completed, hits);
    string noResults = document.QuerySelector("#b_results .b_no")?.TextContent ?? "";
    if (noResults.Contains("there are no results for", StringComparison.OrdinalIgnoreCase) ||
        noResults.Contains("no results found for", StringComparison.OrdinalIgnoreCase))
      return new WebSearchSourceResult(WebSearchStatus.Completed, []);
    return new WebSearchSourceResult(WebSearchStatus.Failed, [], "Bing returned an unrecognized results page.");
  }
}
