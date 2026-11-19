using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Usage;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

public sealed class UsagePricingFlowTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Theory]
  [InlineData(272_000, 544_481)]
  [InlineData(272_001, 1_088_766)]
  public async Task SessionAndDiagnosticQuoteTheSameReportedUsageAtTheStandardRate(int input, int expectedMicroDollars) {
    using var profile = new TemporaryDatabase("urd-usage-pricing-flow");
    var inputDetails = new { cached_tokens = 10, cache_write_tokens = 50,
      cache_write_tokens_details = new { ttl_30m_tokens = 20, ttl_12h_tokens = 30 } };
    var outputDetails = new { reasoning_tokens = 8 }; // Part of the 40 output tokens, not an extra charge.
    string responses = ProviderModelStreams.Responses(model: "GPT-6.2-Sol", usage: new {
      input_tokens = input, input_tokens_details = inputDetails,
      output_tokens = 40, output_tokens_details = outputDetails, total_tokens = input + 40
    });
    string chat = ProviderModelStreams.ChatText("hello", model: "6.2sol", usage: new {
      prompt_tokens = input, prompt_tokens_details = inputDetails,
      completion_tokens = 40, completion_tokens_details = outputDetails, total_tokens = input + 40
    });
    await using var server = Start(RunningServer.OptionsForIsolatedWorkspace(profile), responses, chat);
    await server.WaitForReadyAsync();
    ProviderConnection openAI = await AddAsync(server, "OpenAI fixture", "https://api.openai.com/v1/", LlmApi.OpenAIResponses);
    ProviderConnection copilot = await AddAsync(server, "Copilot fixture", "https://api.githubcopilot.com/", LlmApi.OpenAIChatCompletions);
    Guid session = await RunSessionAsync(server, openAI, "gpt-5.4");
    await DiagnoseAsync(server, copilot, "6.2-sol");

    UsageSummary sessionUsage = await ReadAsync<UsageSummary>(server, $"/api/usage?sessionId={session}");
    UsageRequest request = Assert.Single(sessionUsage.Recent);
    UsageSummary global = await GlobalAsync(server);
    UsageRequest test = Assert.Single(global.Recent, quote => quote.Purpose == "provider test");
    Assert.Equal("GPT-6.2-Sol", request.Model);
    Assert.Equal("6.2sol", test.Model);
    Assert.Equal("openai/gpt-6.2-sol", request.Rates?.Model);
    Assert.Equal(request.Rates, test.Rates);
    Assert.Equal(new UsageTokens(input, 40, 10, 20, 30, 8, input + 40), request.Tokens);
    Assert.Equal(request.Tokens, test.Tokens);
    Assert.False(request.IsEstimate);
    Assert.False(test.IsEstimate);
    decimal usd = expectedMicroDollars / 1_000_000m;
    Assert.Equal(usd, request.CostUsd);
    Assert.Equal(usd, test.CostUsd);
    Assert.Equal(usd, sessionUsage.Totals.CostUsd);
    Assert.Equal(2, global.Totals.Requests);
    Assert.Equal(0, global.Totals.UnknownRequests);
    Assert.Equal(2 * usd, global.Totals.CostUsd);
  }

  [Fact]
  public async Task ColdRequestSettlesWithoutWaitingThenNewQuotesUseTheRefreshedCacheAcrossRestart() {
    using var profile = new TemporaryDatabase("urd-usage-pricing-reopen");
    ServerOptions options = RunningServer.OptionsForIsolatedWorkspace(profile);
    string path = Path.Combine(options.DataRoot, "model-prices.json");
    string wire = ProviderModelStreams.Responses(model: "fixture-chat", usage: new {
      input_tokens = 2, output_tokens = 2, total_tokens = 4, input_tokens_details = new { cached_tokens = 0 }
    });
    var requested = Signal();
    var release = Signal();
    using var feed = new ModelsDevFeed(async (_, ct) => {
      requested.TrySetResult();
      await release.Task.WaitAsync(ct);
      return ModelsDevFeed.ChatCatalog;
    });
    ProviderConnection connection;
    Guid coldRequest;
    await using (var prices = new ModelPriceCatalog(path, feed, TimeProvider.System))
    await using (var server = Start(options, wire, prices: prices)) {
      await server.WaitForReadyAsync();
      connection = await AddAsync(server, "Ollama fixture", "https://ollama.com/v1/", LlmApi.OpenAIResponses);
      await DiagnoseAsync(server, connection, "fixture-chat");
      await requested.Task.WaitAsync(TestTimeout);
      UsageRequest cold = Assert.Single((await GlobalAsync(server)).Recent);
      Assert.NotNull(cold.FinishedAt);
      Assert.Null(cold.CostUsd);
      coldRequest = cold.Id;
      Assert.False(File.Exists(path));

      release.TrySetResult();
      await prices.RefreshAsync().WaitAsync(TestTimeout);
      Assert.Equal(1, feed.Calls);
      Guid session = await RunSessionAsync(server, connection, "Fixture Chat");
      var priced = Assert.Single((await ReadAsync<UsageSummary>(server, $"/api/usage?sessionId={session}")).Recent);
      Assert.Equal(0.000016m, priced.CostUsd); // 2 * $3 input + 2 * $5 output per million, not Ollama's rate.
      Assert.Equal("fixture/chat-model", priced.Rates?.Model);
      Assert.Contains("#openrouter/fixture/chat-model", priced.Rates?.Source);
    }

    using var offline = new ModelsDevFeed(static (_, _) => throw new HttpRequestException("Fixture feed unavailable."));
    await using var reopenedPrices = new ModelPriceCatalog(path, offline, TimeProvider.System);
    await using var reopened = Start(options, wire, prices: reopenedPrices);
    await reopened.WaitForReadyAsync();
    await DiagnoseAsync(reopened, connection, "fixture-chat");
    UsageSummary usage = await GlobalAsync(reopened);
    Assert.Null(Assert.Single(usage.Recent, quote => quote.Id == coldRequest).CostUsd);
    Assert.Equal(0.000016m, Assert.Single(usage.Recent, quote => quote.Purpose == "provider test" && quote.Id != coldRequest).CostUsd);
    Assert.Equal(3, usage.Totals.Requests);
    Assert.Equal(1, usage.Totals.UnknownRequests);
    Assert.Equal(0.000032m, usage.Totals.CostUsd);
    Assert.Equal(0, offline.Calls);
  }

  private static RunningServer Start(ServerOptions options, string responses, string? chat = null, ModelPriceCatalog? prices = null) =>
      new(options, createProviders: (store, callback) => new ProviderManager(store, callback, new ProviderWire(responses, chat), prices: prices));

  private static Task<ProviderConnection> AddAsync(RunningServer server, string name, string endpoint, LlmApi api) =>
      ReadAsync<ProviderConnection>(server, "/api/providers/custom", new CustomProviderSettings(name, api, new(endpoint), AuthenticationMethod.None),
          HttpStatusCode.Created);

  private static Task<ProviderModelTestResult> DiagnoseAsync(RunningServer server, ProviderConnection connection, string model) =>
      ReadAsync<ProviderModelTestResult>(server, $"/api/providers/{connection.Id}/model-tests", new { modelId = model });

  private static async Task<Guid> RunSessionAsync(RunningServer server, ProviderConnection connection, string model) {
    JsonElement created = await ReadAsync<JsonElement>(server, "/api/sessions", new { }, HttpStatusCode.Created);
    Guid session = created.GetProperty("sessionId").GetGuid();
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "hello", TestLlmSelection.Create(connection, model));
    UserMessageReceipt receipt = await ReadAsync<UserMessageReceipt>(server, $"/api/sessions/{session}/message-submissions", submission);
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    SessionSnapshot snapshot = await SettledAsync(receipt.ExecutionId!.Value,
        () => ReadAsync<SessionSnapshot>(server, $"/api/sessions/{session}/snapshot"));
    Assert.True(snapshot.LastSettlement?.Outcome == ExecutionOutcome.Completed, snapshot.LastSettlement?.Error);
    return session;
  }

  private static Task<UsageSummary> GlobalAsync(RunningServer server) {
    DateTimeOffset now = DateTimeOffset.UtcNow;
    string from = Uri.EscapeDataString(now.AddMinutes(-2).ToString("O"));
    string to = Uri.EscapeDataString(now.AddMinutes(2).ToString("O"));
    return ReadAsync<UsageSummary>(server, $"/api/usage?from={from}&to={to}");
  }

  private static async Task<T> ReadAsync<T>(RunningServer server, string path, object? body = null,
      HttpStatusCode status = HttpStatusCode.OK) {
    using HttpResponseMessage response = await RequestAsync(server.Client, body is null ? HttpMethod.Get : HttpMethod.Post,
        path, body is null ? null : JsonContent.Create(body, options: json));
    Assert.Equal(status, response.StatusCode);
    return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>(json));
  }

  private sealed class ProviderWire(string responses, string? chat) : HttpMessageHandler {
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      Assert.Equal(HttpMethod.Post, request.Method);
      Assert.True(request.Content is not null);
      using JsonDocument body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
      Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
      bool isChat = request.RequestUri!.Host == "api.githubcopilot.com";
      if (isChat) {
        Assert.EndsWith("/chat/completions", request.RequestUri.AbsolutePath);
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
      } else {
        Assert.Contains(request.RequestUri.Host, new[] { "api.openai.com", "ollama.com" });
        Assert.EndsWith("/responses", request.RequestUri.AbsolutePath);
      }
      string wire = isChat ? Assert.IsType<string>(chat) : responses;
      return new(HttpStatusCode.OK) {
        RequestMessage = request,
        Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(wire))) { Headers = { ContentType = new("text/event-stream") } }
      };
    }
  }
}
