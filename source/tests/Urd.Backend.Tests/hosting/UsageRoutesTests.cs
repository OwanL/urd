using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Usage;
using Urd.Backend.Tests.Support;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class UsageRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task ReadsSessionAndGlobalUsageWithBoundedAndTotalsOnlyPayloads() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      for (int index = 0; index < 70; index++) {
        DateTimeOffset started = At(1).AddSeconds(index);
        store.Usage.Begin(Request(sessionId, started, $"model-{index:D2}") with {
          CostUsd = 0.000000001m,
          FinishedAt = started.AddSeconds(1)
        });
      }
      DateTimeOffset otherStarted = At(1).AddMinutes(1);
      store.Usage.Begin(Request(Guid.NewGuid(), otherStarted) with {
        CostUsd = 2m,
        FinishedAt = otherStarted.AddSeconds(1)
      });
    }

    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();

    using (HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/usage?sessionId={sessionId}")) {
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      AssertPrivateResponse(response);
      using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      Assert.Equal(70, body.RootElement.GetProperty("totals").GetProperty("requests").GetInt64());
      JsonElement breakdown = body.RootElement.GetProperty("breakdown");
      Assert.Equal(64, breakdown.GetArrayLength());
      JsonElement other = Assert.Single(breakdown.EnumerateArray(), group =>
          group.GetProperty("isOther").GetBoolean());
      Assert.Equal(7, other.GetProperty("totals").GetProperty("requests").GetInt64());
      JsonElement history = body.RootElement.GetProperty("history");
      Assert.Equal(64, history.GetArrayLength());
      Assert.All(history.EnumerateArray(), point => {
        JsonElement pointBreakdown = point.GetProperty("breakdown");
        Assert.Equal(64, pointBreakdown.GetArrayLength());
        Assert.Equal(point.GetProperty("totals").GetProperty("requests").GetInt64(),
            pointBreakdown.EnumerateArray().Sum(group => group.GetProperty("totals")
                .GetProperty("requests").GetInt64()));
      });
      Assert.Equal(20, body.RootElement.GetProperty("recent").GetArrayLength());
    }

    string from = Uri.EscapeDataString(At(1).ToString("O"));
    string to = Uri.EscapeDataString(At(1).AddDays(1).ToString("O"));
    using (HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/usage?from={from}&to={to}")) {
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      Assert.Equal(71, body.RootElement.GetProperty("totals").GetProperty("requests").GetInt64());
    }

    using (HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/usage?sessionId={sessionId}&detail=false")) {
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      Assert.Equal(70, body.RootElement.GetProperty("totals").GetProperty("requests").GetInt64());
      Assert.Empty(body.RootElement.GetProperty("breakdown").EnumerateArray());
      Assert.Empty(body.RootElement.GetProperty("history").EnumerateArray());
      Assert.Empty(body.RootElement.GetProperty("recent").EnumerateArray());
    }

    using (HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/usage?sessionId={Guid.NewGuid()}")) {
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      Assert.Equal(0, body.RootElement.GetProperty("totals").GetProperty("requests").GetInt64());
    }

    string[] invalidQueries = [
      "/api/usage",
      $"/api/usage?sessionId={Guid.Empty}",
      $"/api/usage?sessionId=not-a-guid",
      $"/api/usage?from={from}",
      $"/api/usage?from={Uri.EscapeDataString(At(2).ToString("O"))}&to={from}",
      $"/api/usage?from={from}&to={Uri.EscapeDataString(At(1).AddDays(32).ToString("O"))}"
    ];
    foreach (string query in invalidQueries) {
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get, query);
      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
  }

  [Fact]
  public async Task ReadsPromptCacheForRequiredSession() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Usage.Begin(Request(sessionId, At(1)) with {
        Purpose = "conversation",
        ExecutionId = Guid.NewGuid(),
        FinishedAt = At(1).AddSeconds(1),
        Tokens = new UsageTokens(100, 10, 40)
      });
    }

    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();

    using (HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/usage/prompt-cache?sessionId={sessionId}")) {
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      AssertPrivateResponse(response);
      using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("lastTurn").ValueKind);
      JsonElement session = body.RootElement.GetProperty("session");
      Assert.Equal(100, session.GetProperty("inputTokens").GetInt64());
      Assert.Equal(40, session.GetProperty("cachedInputTokens").GetInt64());
      Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("warning").ValueKind);
      Assert.Equal(1, session.GetProperty("reportedRequests").GetInt64());
      Assert.Equal(1, session.GetProperty("totalRequests").GetInt64());
    }

    using (HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/usage/prompt-cache?sessionId={Guid.NewGuid()}")) {
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      AssertPrivateResponse(response);
      using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("lastTurn").ValueKind);
      Assert.Equal(0, body.RootElement.GetProperty("session").GetProperty("totalRequests").GetInt64());
    }

    string[] invalidQueries = [
      "/api/usage/prompt-cache",
      "/api/usage/prompt-cache?sessionId=",
      "/api/usage/prompt-cache?sessionId=not-a-guid",
      $"/api/usage/prompt-cache?sessionId={Guid.Empty}"
    ];
    foreach (string query in invalidQueries) {
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get, query);
      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
  }

  [Fact]
  public async Task WarnsFromLiveComparableRequestsWithoutReclassifyingSessionTotals() {
    using var database = new TemporaryDatabase("urd-prompt-cache-route");
    using var handler = new ScriptedOpenAIHandler(ProviderModelStreams.ChatText("Fixture reply.", usage: new {
      prompt_tokens = 5000, prompt_tokens_details = new { cached_tokens = 100 },
      completion_tokens = 5, total_tokens = 5005
    }));
    await using var server = new RunningServer(RunningServer.OptionsForIsolatedWorkspace(database),
        createProviders: (store, callback) => new ProviderManager(store, callback, handler));
    await server.WaitForReadyAsync();
    ProviderConnection connection = await SendAsync<ProviderConnection>(server, "/api/providers/custom",
        new CustomProviderSettings("Cache fixture", LlmApi.OpenAIChatCompletions, new("http://127.0.0.1:11434/v1/"),
            AuthenticationMethod.None), HttpStatusCode.Created);
    Guid session = (await SendAsync<JsonElement>(server, "/api/sessions", new { }, HttpStatusCode.Created))
        .GetProperty("sessionId").GetGuid();
    long revision = 0;
    for (int turn = 1; turn <= 3; turn++) {
      UserMessageReceipt receipt = await SendAsync<UserMessageReceipt>(server, $"/api/sessions/{session}/message-submissions",
          new UserMessageSubmission(Guid.NewGuid(), revision, $"Synthetic prefix continuation {turn}.",
              TestLlmSelection.Create(connection, "fixture-model")));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      SessionSnapshot settled = await SettledAsync(receipt.ExecutionId!.Value,
          () => SendAsync<SessionSnapshot>(server, $"/api/sessions/{session}/snapshot"));
      Assert.True(settled.LastSettlement?.Outcome == ExecutionOutcome.Completed, settled.LastSettlement?.Error);
      revision = settled.HistoryRevision;

      PromptCacheSummary summary = await SendAsync<PromptCacheSummary>(server, $"/api/usage/prompt-cache?sessionId={session}");
      Assert.Equal(new PromptCacheTotals(5000 * turn, 100 * turn, turn, turn), summary.Session);
      Assert.Equal(new PromptCacheTotals(5000, 100, 1, 1), summary.LastTurn);
      if (turn < 3) Assert.Null(summary.Warning);
      else Assert.Equal(new PromptCacheTotals(10_000, 200, 2, 2), summary.Warning);
    }
  }

  private static async Task<T> SendAsync<T>(RunningServer server, string path, object? body = null,
      HttpStatusCode expected = HttpStatusCode.OK) {
    using HttpResponseMessage response = await RequestAsync(server.Client, body is null ? HttpMethod.Get : HttpMethod.Post,
        path, body is null ? null : JsonContent.Create(body, options: json));
    Assert.Equal(expected, response.StatusCode);
    return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>(json));
  }

  private static UsageRequest Request(Guid? session, DateTimeOffset started, string model = "model") =>
      new(Guid.NewGuid(), session, "provider", model, "chat", started, null, null, false, null,
          null, null, Guid.NewGuid());

  private static DateTimeOffset At(int day) => new(2026, 10, day, 0, 0, 0, TimeSpan.Zero);
}
