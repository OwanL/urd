using System.Net;
using System.Text;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class WebSearchYouTests {
  [Fact]
  public async Task MapsRankSnippetsAndPublishDatesIntoHits() {
    WebSearchSourceResult result = await SearchAsync("""
      {"result":{"content":[{"type":"text","text":"unused"}],"structuredContent":{"results":{"web":[
        {"url":"https://example.com/a","title":"Example A","description":"Desc A","snippets":["Snippet A1","Snippet A2"],"page_age":"2026-11-16T04:15:22"},
        {"url":"https://example.com/b","title":"Example B","description":"Desc B"}
      ]},"metadata":{"query":"q"}}},"jsonrpc":"2.0","id":2}
      """);

    Assert.Equal(WebSearchStatus.Completed, result.Status);
    Assert.Equal(2, result.Hits.Count);
    Assert.Equal(new WebSearchHit("Example A", "https://example.com/a", "Snippet A1", "2026-11-16T04:15:22", 1), result.Hits[0]);
    // Without excerpts the description carries the result.
    Assert.Equal(new WebSearchHit("Example B", "https://example.com/b", "Desc B", null, 2), result.Hits[1]);
  }

  [Fact]
  public async Task CallsYouSearchOnTheFreeProfile() {
    FakeMcpHandler handler = new();
    WebSearchSourceResult result = await SearchAsync(handler, "mcp specification", EmptyResults);

    Assert.Equal(WebSearchStatus.Completed, result.Status);
    Assert.Contains(handler.Requests, request => request.Uri.ToString() == WebSearchYou.FreeEndpoint);
    string discover = Assert.Single(handler.Requests, request => request.Body.Contains("\"method\":\"server/discover\"")).Body;
    Assert.Contains("\"name\":\"urd\"", discover);
    string call = Assert.Single(handler.Requests, request => request.Body.Contains("\"method\":\"tools/call\"")).Body;
    Assert.Contains($"\"name\":\"{WebSearchYou.SearchToolName}\"", call);
    Assert.Contains("\"query\":\"mcp specification\"", call);
    Assert.Contains("\"count\":5", call);
    Assert.Contains("\"extraction\":\"none\"", call);
  }

  [Fact]
  public async Task ReportsToolErrorsAsFailedWithTheServerMessage() {
    WebSearchSourceResult result = await SearchAsync("""
      {"result":{"content":[{"type":"text","text":"Input validation error: Invalid arguments for tool you-search: query: Query is required"}],"isError":true},"jsonrpc":"2.0","id":2}
      """);

    Assert.Equal(WebSearchStatus.Failed, result.Status);
    Assert.Empty(result.Hits);
    Assert.Contains("Query is required", result.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FallsBackToTextContentWhenStructuredContentIsMissing() {
    WebSearchSourceResult result = await SearchAsync(
      "{\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"results\\\":{\\\"web\\\":[{\\\"url\\\":\\\"https://example.com/c\\\",\\\"title\\\":\\\"Example C\\\"}]}}\"}],\"isError\":false},\"jsonrpc\":\"2.0\",\"id\":2}");

    Assert.Equal(WebSearchStatus.Completed, result.Status);
    WebSearchHit hit = Assert.Single(result.Hits);
    Assert.Equal("https://example.com/c", hit.Url);
    Assert.Equal("", hit.Snippet);
  }

  [Theory]
  [InlineData("{\"result\":{\"content\":[],\"structuredContent\":{\"results\":{\"web\":\"not an array\"}},\"isError\":false},\"jsonrpc\":\"2.0\",\"id\":2}", "invalid web results")]
  [InlineData("{\"result\":{\"content\":[],\"structuredContent\":{\"results\":{\"web\":[{\"url\":7}]}},\"isError\":false},\"jsonrpc\":\"2.0\",\"id\":2}", "unrecognized web results")]
  [InlineData("{\"result\":{\"content\":[],\"isError\":false},\"jsonrpc\":\"2.0\",\"id\":2}", "structured results")]
  [InlineData("{\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"not json\"}],\"isError\":false},\"jsonrpc\":\"2.0\",\"id\":2}", "structured results")]
  [InlineData("{\"result\":{\"content\":[],\"structuredContent\":{\"metadata\":{}},\"isError\":false},\"jsonrpc\":\"2.0\",\"id\":2}", "missing results")]
  public async Task UntextualOrMissingWebPayloadsFailRatherThanGuess(string reply, string expectedText) {
    WebSearchSourceResult result = await SearchAsync(reply);

    Assert.Equal(WebSearchStatus.Failed, result.Status);
    Assert.Contains(expectedText, result.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingWebArrayCompletesWithoutHits() {
    WebSearchSourceResult result = await SearchAsync(
      "{\"result\":{\"content\":[],\"structuredContent\":{\"results\":{}},\"isError\":false},\"jsonrpc\":\"2.0\",\"id\":2}");

    Assert.Equal(WebSearchStatus.Completed, result.Status);
    Assert.Empty(result.Hits);
  }

  [Theory]
  [InlineData(HttpStatusCode.TooManyRequests, nameof(WebSearchStatus.RateLimited), "rate limit")]
  [InlineData(HttpStatusCode.Unauthorized, nameof(WebSearchStatus.Blocked), "refused")]
  [InlineData(HttpStatusCode.Forbidden, nameof(WebSearchStatus.Blocked), "refused")]
  public async Task MapsHttpStatusCodesToSearchStatuses(HttpStatusCode status, string expectedStatus, string expectedText) {
    WebSearchSourceResult result = await SearchAsync("web search", _ => new HttpResponseMessage(status) { Content = new ByteArrayContent([]) });

    Assert.Equal(Enum.Parse<WebSearchStatus>(expectedStatus), result.Status);
    Assert.Contains(expectedText, result.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task ProtocolErrorsFailWithTheServerMessage() {
    WebSearchSourceResult result = await SearchAsync("web search", _ => FakeMcpHandler.JsonResponse(
      "{\"error\":{\"code\":-32602,\"message\":\"Tool you-search not found\"},\"jsonrpc\":\"2.0\",\"id\":2}"));

    Assert.Equal(WebSearchStatus.Failed, result.Status);
    Assert.Contains("not found", result.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ExhaustingTheSearchTimeoutTimesOut() {
    FakeMcpHandler handler = new() { StallToolCalls = true };
    WebSearchSourceResult result = await SearchAsync(handler, "web search", TimeSpan.FromMilliseconds(500), _ => new HttpResponseMessage(HttpStatusCode.OK));

    Assert.Equal(WebSearchStatus.TimedOut, result.Status);
  }

  [Fact]
  public async Task CallerCancellationInterruptsAndRethrows() {
    CancellationToken cancelled = new(canceled: true);
    FakeMcpHandler handler = new();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SearchAsync(handler, "web search", EmptyResults, cancelled));
  }

  private const string EmptyResults =
    "{\"result\":{\"content\":[],\"structuredContent\":{\"results\":{\"web\":[]},\"metadata\":{}},\"isError\":false},\"jsonrpc\":\"2.0\",\"id\":2}";

  private static Task<WebSearchSourceResult> SearchAsync(string callReply) {
    FakeMcpHandler handler = new();
    return SearchAsync(handler, "web search", TimeSpan.FromSeconds(10), _ => FakeMcpHandler.JsonResponse(callReply));
  }

  private static Task<WebSearchSourceResult> SearchAsync(FakeMcpHandler handler, string query, string callReply) =>
    SearchAsync(handler, query, TimeSpan.FromSeconds(10), _ => FakeMcpHandler.JsonResponse(callReply));

  private static Task<WebSearchSourceResult> SearchAsync(string query, Func<FakeMcpHandler, HttpResponseMessage> respondToCall) {
    FakeMcpHandler handler = new();
    return SearchAsync(handler, query, TimeSpan.FromSeconds(10), respondToCall);
  }

  private static async Task<WebSearchSourceResult> SearchAsync(FakeMcpHandler handler, string query, TimeSpan timeout, Func<FakeMcpHandler, HttpResponseMessage> respondToCall) {
    handler.RespondToCall = () => respondToCall(handler);
    TimeSpan previousTimeout = WebSearchYou.SearchTimeout;
    Func<HttpClient> previousProvider = WebSearchYou.HttpClientProvider;
    try {
      WebSearchYou.SearchTimeout = timeout;
      WebSearchYou.HttpClientProvider = () => new HttpClient(handler);
      return await WebSearchYou.SearchAsync(query, CancellationToken.None);
    } finally {
      WebSearchYou.SearchTimeout = previousTimeout;
      WebSearchYou.HttpClientProvider = previousProvider;
    }
  }

  private static async Task<WebSearchSourceResult> SearchAsync(FakeMcpHandler handler, string query, string callReply, CancellationToken cancellation) {
    handler.RespondToCall = () => FakeMcpHandler.JsonResponse(callReply);
    TimeSpan previousTimeout = WebSearchYou.SearchTimeout;
    Func<HttpClient> previousProvider = WebSearchYou.HttpClientProvider;
    try {
      WebSearchYou.SearchTimeout = TimeSpan.FromSeconds(10);
      WebSearchYou.HttpClientProvider = () => new HttpClient(handler);
      return await WebSearchYou.SearchAsync(query, cancellation);
    } finally {
      WebSearchYou.SearchTimeout = previousTimeout;
      WebSearchYou.HttpClientProvider = previousProvider;
    }
  }

  // A socket-free streamable-HTTP fixture: records POSTs and answers each MCP method with a canned body.
  private sealed class FakeMcpHandler : HttpMessageHandler {
    private const string DiscoverReply =
      "{\"result\":{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{\"tools\":{\"listChanged\":false}},\"instructions\":\"search\"," +
      "\"resultType\":\"complete\",\"ttlMs\":0,\"cacheScope\":\"private\",\"_meta\":{\"io.modelcontextprotocol/serverInfo\":{\"name\":\"You.com\",\"version\":\"4.1.0\"}}}," +
      "\"jsonrpc\":\"2.0\",\"id\":1}";

    internal List<(Uri Uri, string Body)> Requests { get; } = [];
    internal Func<HttpResponseMessage> RespondToCall { get; set; } = () => new HttpResponseMessage(HttpStatusCode.NotImplemented);
    internal bool StallToolCalls { get; init; }

    internal static HttpResponseMessage JsonResponse(string body) =>
      new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
      Requests.Add((request.RequestUri!, body));
      if (body.Contains("\"method\":\"server/discover\""))
        return JsonResponse(DiscoverReply);
      if (body.Contains("\"method\":\"tools/call\"")) {
        if (StallToolCalls) {
          // Hangs until the search deadline cancels the request.
          await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
          return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        return RespondToCall();
      }
      return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("") };
    }
  }
}
