using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol;

namespace Urd.Core.Tools;

// Searches the web through You.com's keyless free MCP profile: you-search over streamable HTTP.
internal static class WebSearchYou {
  internal const string FreeEndpoint = "https://api.you.com/mcp?profile=free";
  internal const string SearchToolName = "you-search";
  private const string ServerName = "youdotcom";

  // Seams for offline tests: each search runs against a fresh HttpClient; the timeout bounds the whole session.
  internal static Func<HttpClient> HttpClientProvider { get; set; } = CreateHttpClient;
  internal static TimeSpan SearchTimeout { get; set; } = TimeSpan.FromSeconds(15);

  internal static async Task<WebSearchSourceResult> SearchAsync(string query, CancellationToken cancellation) {
    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    deadline.CancelAfter(SearchTimeout);
    HttpClient client = HttpClientProvider();
    try {
      // The transport must not own the client because this method disposes it.
      await using HttpClientTransport transport = new(TransportOptions(), client, loggerFactory: null, ownsHttpClient: false);
      await using McpClient search = await McpClient.CreateAsync(transport, CreateOptions(), loggerFactory: null, deadline.Token).ConfigureAwait(false);
      CallToolResult result = await search.CallToolAsync(SearchToolName, SearchArguments(query), null, null, deadline.Token).ConfigureAwait(false);
      return ToResult(result);
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      throw;
    } catch (OperationCanceledException) {
      return new WebSearchSourceResult(WebSearchStatus.TimedOut, [], "you.com search timed out.");
    } catch (HttpRequestException error) {
      return ErrorResult(error);
    } catch (McpException error) {
      return new WebSearchSourceResult(WebSearchStatus.Failed, [], error.Message);
    } finally {
      client.Dispose();
    }
  }

  // Maps a tools/call result into the shared search model.
  internal static WebSearchSourceResult ToResult(CallToolResult result) {
    if (result.IsError == true)
      return new WebSearchSourceResult(WebSearchStatus.Failed, [], FirstText(result) ?? "you-search reported an error without details.");
    JsonElement? payload = result.StructuredContent;
    if (payload is null && FirstText(result) is { Length: > 0 } text)
      payload = ParsePayload(text);
    if (payload is not JsonElement structured || structured.ValueKind is not JsonValueKind.Object)
      return new WebSearchSourceResult(WebSearchStatus.Failed, [], "you-search returned no structured results.");
    return ToResult(structured);
  }

  // Reads the outputSchema payload { results: { web: [...] }, metadata }.
  internal static WebSearchSourceResult ToResult(JsonElement payload) {
    if (!payload.TryGetProperty("results", out JsonElement results) || results.ValueKind is not JsonValueKind.Object)
      return new WebSearchSourceResult(WebSearchStatus.Failed, [], "you-search response is missing results.");
    if (!results.TryGetProperty("web", out JsonElement web))
      return new WebSearchSourceResult(WebSearchStatus.Completed, []);

    if (web.ValueKind is not JsonValueKind.Array)
      return new WebSearchSourceResult(WebSearchStatus.Failed, [], "you-search returned an invalid web results array.");
    List<WebSearchHit> hits = [];
    int rank = 0;
    foreach (JsonElement entry in web.EnumerateArray()) {
      if (++rank > 5)
        break;
      WebSearchHit? hit = ToHit(rank, entry);
      if (hit is not null)
        hits.Add(hit);
    }
    if (rank > 0 && hits.Count == 0)
      return new WebSearchSourceResult(WebSearchStatus.Failed, [], "you-search returned unrecognized web results.");
    return new WebSearchSourceResult(WebSearchStatus.Completed, new ReadOnlyCollection<WebSearchHit>(hits));
  }

  private static WebSearchHit? ToHit(int rank, JsonElement entry) {
    if (entry.ValueKind is not JsonValueKind.Object || !entry.TryGetProperty("url", out JsonElement url) || url.ValueKind != JsonValueKind.String ||
        url.GetString() is not { Length: > 0 } target || !Uri.TryCreate(WebSearchResults.Destination(target), UriKind.Absolute, out Uri? address) ||
        address.Scheme is not ("http" or "https") || address.AbsoluteUri.Length > 2048 || !string.IsNullOrEmpty(address.UserInfo) ||
        WebSearchResults.IsBingRedirect(address) || string.IsNullOrWhiteSpace(Text(entry, "title")))
      return null;
    return new WebSearchHit(
      Text(entry, "title"),
      target,
      Snippet(entry),
      Text(entry, "page_age") is { Length: > 0 } published ? published : null,
      rank);
  }

  // Query-relevant excerpts outrank the description, which is the page's meta description.
  private static string Snippet(JsonElement entry) {
    if (entry.TryGetProperty("snippets", out JsonElement snippets) && snippets.ValueKind is JsonValueKind.Array) {
      foreach (JsonElement excerpt in snippets.EnumerateArray()) {
        if (excerpt.ValueKind is JsonValueKind.String && excerpt.GetString() is { Length: > 0 } text)
          return text;
      }
    }
    return entry.TryGetProperty("description", out JsonElement description) && description.ValueKind is JsonValueKind.String ? description.GetString() ?? "" : "";
  }

  private static string Text(JsonElement entry, string name) =>
    entry.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.String ? value.GetString() ?? "" : "";

  private static string? FirstText(CallToolResult result) =>
    result.Content.OfType<TextContentBlock>().Select(static block => block.Text).FirstOrDefault();

  private static WebSearchSourceResult ErrorResult(HttpRequestException error) =>
    error.StatusCode switch {
      HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new WebSearchSourceResult(WebSearchStatus.Blocked, [], $"you.com refused the search ({(int)error.StatusCode})."),
      HttpStatusCode.TooManyRequests => new WebSearchSourceResult(WebSearchStatus.RateLimited, [], "you.com free search rate limit reached."),
      _ => new WebSearchSourceResult(WebSearchStatus.Failed, [], error.Message),
    };

  private static McpClientOptions CreateOptions() => new() {
    ClientInfo = new Implementation { Name = "urd", Version = "1.0" },
    InitializationTimeout = SearchTimeout,
  };

  private static Dictionary<string, object?> SearchArguments(string query) => new() {
    ["query"] = query,
    ["count"] = 5,
    // Snippets only: skips page crawling and keeps responses light.
    ["extraction"] = "none",
  };

  private static HttpClientTransportOptions TransportOptions() => new() {
    Endpoint = new Uri(FreeEndpoint),
    Name = ServerName,
    TransportMode = HttpTransportMode.StreamableHttp,
    ConnectionTimeout = SearchTimeout,
  };

  private static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false });

  private static JsonElement? ParsePayload(string? text) {
    if (string.IsNullOrWhiteSpace(text))
      return null;
    try {
      using JsonDocument document = JsonDocument.Parse(text);
      return document.RootElement.Clone();
    } catch (JsonException) {
      return null;
    }
  }
}
