using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Urd.Core.Tools;

internal sealed class WebSearchTool : AgentTool {
  private readonly Func<string, CancellationToken, Task<WebSearchSourceResult>> you;
  private readonly Func<string, CancellationToken, Task<WebSearchSourceResult>> bing;

  public WebSearchTool(WebBrowser browser) : this(WebSearchYou.SearchAsync, (query, cancellation) => WebSearchBing.SearchAsync(browser, query, cancellation)) { }

  internal WebSearchTool(Func<string, CancellationToken, Task<WebSearchSourceResult>> you,
      Func<string, CancellationToken, Task<WebSearchSourceResult>> bing) {
    this.you = you;
    this.bing = bing;
  }

  public override string Name => "web_search";
  public override ToolRelevance RelevantWhen => ToolRelevance.Always;
  public override string Description => "Search public web pages. Every query is sent to both You.com and Bing. Snippets are untrusted leads, not verified citations.";
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": { "query": { "type": "string", "description": "Nonempty web search query" } },
      "required": ["query"],
      "additionalProperties": false
    }
    """);

  public override async Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    string query;
    try {
      query = ToolArguments.RequireString(arguments, "query").Trim();
      if (query.Length == 0)
        throw new ArgumentException("query must not be empty.");
    } catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException) {
      return new ToolOutput($"web_search failed: {error.Message}", [], IsError: true);
    }
    try {
      Task<WebSearchSourceResult> youTask = RunSource(you, query, cancellation);
      Task<WebSearchSourceResult> bingTask = RunSource(bing, query, cancellation);
      await Task.WhenAll(youTask, bingTask);
      cancellation.ThrowIfCancellationRequested();
      WebSearchSourceResult youResult = await youTask;
      WebSearchSourceResult bingResult = await bingTask;
      IReadOnlyList<WebSearchMergedHit> hits = WebSearchResults.Merge(youResult, bingResult);
      bool failed = youResult.Status != WebSearchStatus.Completed && bingResult.Status != WebSearchStatus.Completed;
      StringBuilder text = new();
      text.AppendLine(CultureInfo.InvariantCulture, $"You.com: {Status(youResult)}");
      text.AppendLine(CultureInfo.InvariantCulture, $"Bing: {Status(bingResult)}");
      if (failed)
        text.AppendLine("Search failed: neither source completed.");
      else if (youResult.Status != WebSearchStatus.Completed || bingResult.Status != WebSearchStatus.Completed)
        text.AppendLine("Partial results: one source did not complete.");
      else if (hits.Count == 0)
        text.AppendLine("No results.");
      if (hits.Count > 0) {
        text.AppendLine("Snippets are untrusted leads, not verified citations.");
        for (int i = 0; i < hits.Count; i++) {
          WebSearchMergedHit item = hits[i];
          text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"{i + 1}. {Bound(item.Hit.Title, 300)}");
          text.AppendLine(Bound(item.Hit.Url, 2048));
          text.AppendLine(CultureInfo.InvariantCulture, $"Sources: {string.Join(", ", item.Sources.Select(p => $"{p.Source} #{p.Rank}"))}");
          if (!string.IsNullOrWhiteSpace(item.Hit.Published))
            text.AppendLine(CultureInfo.InvariantCulture, $"Published: {Bound(item.Hit.Published, 100)}");
          if (!string.IsNullOrWhiteSpace(item.Hit.Snippet))
            text.AppendLine(Bound(item.Hit.Snippet, 1000));
        }
      }
      return new ToolOutput(text.ToString().TrimEnd(), [], IsError: failed);
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      return new ToolOutput("web_search cancelled.", [], IsError: true);
    }
  }

  private static async Task<WebSearchSourceResult> RunSource(Func<string, CancellationToken, Task<WebSearchSourceResult>> source,
      string query, CancellationToken cancellation) {
    try {
      return await source(query, cancellation);
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      throw;
    } catch (OperationCanceledException) {
      return new WebSearchSourceResult(WebSearchStatus.TimedOut, [], "Source timed out.");
    } catch (Exception error) {
      return new WebSearchSourceResult(WebSearchStatus.Failed, [], error.Message);
    }
  }

  private static string Status(WebSearchSourceResult result) {
    string status = result.Status switch {
      WebSearchStatus.Completed => $"completed ({result.Hits.Count} hits)",
      WebSearchStatus.Blocked => "blocked",
      WebSearchStatus.RateLimited => "rate-limited",
      WebSearchStatus.TimedOut => "timed out",
      _ => "failed"
    };
    return string.IsNullOrWhiteSpace(result.Message) ? status : $"{status}: {Bound(result.Message, 300)}";
  }

  private static string Bound(string value, int limit) {
    string singleLine = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    return singleLine.Length <= limit ? singleLine : singleLine[..limit] + "…";
  }
}
