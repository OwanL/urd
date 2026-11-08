using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.OpenAIStreams;

namespace Urd.Backend.Tests.Hosting;

// Real local HTTP and SQLite, with provider HTTP intercepted below the real SDK before any socket is opened.
public sealed class ProviderRoutesTests {
  private const string Secret = "synthetic-http-api-key-do-not-return";
  private const string ReplacementSecret = "synthetic-http-replacement-key-do-not-return";
  private const string CallbackCode = "synthetic-callback-code-do-not-return";
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    PropertyNameCaseInsensitive = false,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task SavedConnectionsKeysStaleBindingsDisconnectAndRemovalStayLocalAndSecretFree() {
    using var database = new TemporaryDatabase();
    var handler = new ScriptedOpenAIHandler("Unexpected provider work");
    var llm = new ScriptedChatClient(ScriptedResponses.Complete);
    await using var server = Start(database, handler, llm);
    await server.WaitForReadyAsync();
    Assert.Empty(await GetAsync<ProviderConnection[]>(server.Client, "/api/providers"));

    ProviderConnection official = await AddOpenAIAsync(server.Client);
    Assert.Equal(ProviderRoute.OpenAI, official.Route);
    Assert.Equal(new Uri("https://api.openai.com/v1/"), official.Endpoint);
    Assert.Equal(AuthenticationMethod.ApiKey, official.Authentication);
    ProviderConnection custom = await AddCustomAsync(server.Client);
    Assert.NotEqual(official.Id, custom.Id);
    Assert.Equal(ProviderRoute.Custom, custom.Route);
    Assert.Equal(new Uri("https://fixture.invalid/prefix/v1/"), custom.Endpoint);
    Assert.Equal(LlmApi.OpenAIResponses, custom.Api);
    Assert.Equal(2, (await GetAsync<ProviderConnection[]>(server.Client, "/api/providers")).Length);

    ProviderCredentialStatus status = await SetKeyAsync(server.Client, official);
    Assert.True(status.HasCredentials);
    Assert.False(status.RequiresSignIn);
    Assert.True((await SetKeyAsync(server.Client, custom)).HasCredentials);
    Assert.All(StoredProviders.Read(database), row => Assert.Contains(Secret, row.Credentials!));
    Assert.True((await GetAsync<ProviderCredentialStatus>(server.Client, PathFor(official, "/credentials"))).HasCredentials);

    ProviderConnection renamed = await PostAsync<ProviderConnection>(server.Client, PathFor(custom, "/rename"), new { name = "Renamed" });
    Assert.Equal("Renamed", renamed.Name);
    // Display-name changes do not invalidate a caller's credential destination binding.
    Assert.True((await SetKeyAsync(server.Client, custom)).HasCredentials);
    ProviderConnection updated = await PostAsync<ProviderConnection>(server.Client, PathFor(custom, "/custom-settings"),
        Settings("https://fixture.invalid/replaced/v1/"));
    Assert.False((await GetAsync<ProviderCredentialStatus>(server.Client, PathFor(updated, "/credentials"))).HasCredentials);
    using (HttpResponseMessage stale = await SendAsync(server.Client,
        JsonRequest(HttpMethod.Post, PathFor(custom, "/api-key"), new { connection = custom, apiKey = Secret }), HttpStatusCode.Conflict)) {
      Assert.Equal("connectionChanged", (await BodyAsync(stale)).GetProperty("error").GetString());
    }
    Assert.Null(StoredProviders.Read(database).Single(row => row.Connection.Id == custom.Id).Credentials);
    await AssertStatusAsync(server.Client,
        JsonRequest(HttpMethod.Post, PathFor(custom, "/api-key"), new { connection = official, apiKey = Secret }), HttpStatusCode.BadRequest);
    Assert.True((await SetKeyAsync(server.Client, updated, ReplacementSecret)).HasCredentials);

    ProviderDisconnectResult disconnected = await PostAsync<ProviderDisconnectResult>(server.Client, PathFor(updated, "/disconnect"));
    Assert.True(disconnected.LocalCredentialsCleared);
    Assert.Null(disconnected.RemoteRevocationConfirmed);
    Assert.False((await GetAsync<ProviderCredentialStatus>(server.Client, PathFor(updated, "/credentials"))).HasCredentials);
    Assert.Null(StoredProviders.Read(database).Single(row => row.Connection.Id == custom.Id).Credentials);
    await AssertStatusAsync(server.Client, JsonRequest(HttpMethod.Post, PathFor(updated, "/remove")), HttpStatusCode.NoContent);
    await AssertStatusAsync(server.Client, JsonRequest(HttpMethod.Post, PathFor(official, "/remove")), HttpStatusCode.NoContent);
    await AssertStatusAsync(server.Client, new(HttpMethod.Get, PathFor(updated)), HttpStatusCode.NotFound);
    Assert.Empty(await GetAsync<ProviderConnection[]>(server.Client, "/api/providers"));
    Assert.Empty(StoredProviders.Read(database));
    Assert.Empty(handler.Requests);
    AssertNoSessionWork(database, llm);
  }

  [Fact]
  public async Task ExplicitCatalogRefreshPersistsWhileReadsAndReopenRemainOffline() {
    using var database = new TemporaryDatabase();
    var handler = new ScriptedOpenAIHandler("""
        {"object":"list","data":[
          {"id":"fixture-z","object":"model","created":1700000000,"owned_by":"fixture"},
          {"id":"fixture-a","object":"model","created":1700000000,"owned_by":"fixture"}
        ]}
        """, "application/json");
    var llm = new ScriptedChatClient(ScriptedResponses.Complete);
    ProviderConnection connection;
    ProviderModelCatalog fetched;
    await using (var server = Start(database, handler, llm)) {
      await server.WaitForReadyAsync();
      connection = await AddCustomAsync(server.Client);
      await SetKeyAsync(server.Client, connection);
      ProviderModelCatalog initial = await GetAsync<ProviderModelCatalog>(server.Client, PathFor(connection, "/models"));
      Assert.Null(initial.RefreshedAt);
      Assert.Empty(initial.Models);
      Assert.Empty(handler.Requests);

      fetched = await PostAsync<ProviderModelCatalog>(server.Client, PathFor(connection, "/models/refresh"));
      Assert.Equal(connection.Id, fetched.ConnectionId);
      Assert.NotNull(fetched.RefreshedAt);
      Assert.Equal([new("fixture-z", "fixture-z"), new ProviderModel("fixture-a", "fixture-a")], fetched.Models);
      var request = Assert.Single(handler.Requests);
      Assert.Equal(HttpMethod.Get, request.Method);
      Assert.Equal("/prefix/v1/models", request.Uri.AbsolutePath);
      Assert.Equal($"Bearer {Secret}", request.Authorization);
      ProviderModelCatalog saved = await GetAsync<ProviderModelCatalog>(server.Client, PathFor(connection, "/models"));
      Assert.Equal(fetched.RefreshedAt, saved.RefreshedAt);
      Assert.Equal(fetched.Models, saved.Models);
      Assert.NotNull(Assert.Single(StoredProviders.Read(database)).Catalog);
      Assert.Single(handler.Requests);
    }

    var reopenedHandler = new ScriptedOpenAIHandler("Unexpected provider work");
    var reopenedLlm = new ScriptedChatClient(ScriptedResponses.Complete);
    await using var reopened = Start(database, reopenedHandler, reopenedLlm);
    await reopened.WaitForReadyAsync();
    Assert.Equal(connection, await GetAsync<ProviderConnection>(reopened.Client, PathFor(connection)));
    Assert.Equal(connection, Assert.Single(await GetAsync<ProviderConnection[]>(reopened.Client, "/api/providers")));
    Assert.True((await GetAsync<ProviderCredentialStatus>(reopened.Client, PathFor(connection, "/credentials"))).HasCredentials);
    ProviderModelCatalog retained = await GetAsync<ProviderModelCatalog>(reopened.Client, PathFor(connection, "/models"));
    Assert.Equal(fetched.RefreshedAt, retained.RefreshedAt);
    Assert.Equal(fetched.Models, retained.Models);
    Assert.Contains(Secret, Assert.Single(StoredProviders.Read(database)).Credentials!);
    Assert.Empty(reopenedHandler.Requests);
    AssertNoSessionWork(database, llm);
    Assert.Equal(0, reopenedLlm.Calls);
  }

  [Fact]
  public async Task ExplicitResponseTestUsesTheRealSdkWithoutCreatingAChatOrFetchingACatalog() {
    using var database = new TemporaryDatabase();
    var handler = new ScriptedOpenAIHandler(ResponseStream(text: "Hello.", usage: new { input_tokens = 6, output_tokens = 2, total_tokens = 8 }));
    var llm = new ScriptedChatClient(ScriptedResponses.Complete);
    await using var server = Start(database, handler, llm);
    await server.WaitForReadyAsync();
    ProviderConnection connection = await AddOpenAIAsync(server.Client);
    await SetKeyAsync(server.Client, connection);
    Guid[] sessionIds = StoredSessions.Read(database).Select(row => row.Id).ToArray();
    Guid[] creationIds = StoredSessions.ReadCreation(database).Select(row => row.Id).ToArray();
    var saved = StoredProviders.Read(database).ToArray();

    ProviderModelTestResult result = await PostAsync<ProviderModelTestResult>(server.Client, PathFor(connection, "/model-tests"),
        new { modelId = "fixture-model" });

    Assert.Equal(connection.Id, result.ConnectionId);
    Assert.Equal("fixture-model", result.ModelId);
    Assert.Equal("Hello.", result.Text);
    Assert.False(result.Truncated);
    Assert.Equal(new ProviderUsage(6, 2, 8, null), result.Usage);
    var request = Assert.Single(handler.Requests);
    Assert.Equal(HttpMethod.Post, request.Method);
    Assert.Equal("https://api.openai.com/v1/responses", request.Uri.AbsoluteUri);
    Assert.Equal($"Bearer {Secret}", request.Authorization);
    Assert.Equal(1, handler.StreamDisposals);
    Assert.Null((await GetAsync<ProviderModelCatalog>(server.Client, PathFor(connection, "/models"))).RefreshedAt);
    Assert.Single(handler.Requests);
    Assert.Equal(saved, StoredProviders.Read(database));
    Assert.Equal(sessionIds, StoredSessions.Read(database).Select(row => row.Id));
    Assert.Equal(creationIds, StoredSessions.ReadCreation(database).Select(row => row.Id));
    AssertNoSessionWork(database, llm);
  }

  [Fact]
  public async Task ProviderFailureReturnsOnlyTheSafeClassificationWithoutRetryOrSessionWork() {
    using var database = new TemporaryDatabase();
    var handler = new ScriptedOpenAIHandler(
        $"{{\"error\":{{\"message\":\"{Secret}\",\"type\":\"server_error\"}}}}", "application/json", HttpStatusCode.ServiceUnavailable);
    var llm = new ScriptedChatClient(ScriptedResponses.Complete);
    await using var server = Start(database, handler, llm);
    await server.WaitForReadyAsync();
    ProviderConnection connection = await AddOpenAIAsync(server.Client);
    await SetKeyAsync(server.Client, connection);
    using HttpResponseMessage response = await SendAsync(server.Client,
        JsonRequest(HttpMethod.Post, PathFor(connection, "/model-tests"), new { modelId = "fixture-model" }), HttpStatusCode.BadGateway);
    JsonElement body = await BodyAsync(response);
    Assert.Equal("providerUnavailable", body.GetProperty("error").GetString());
    Assert.Single(body.EnumerateObject());
    Assert.Single(handler.Requests);
    AssertNoSessionWork(database, llm);
  }

  [Fact]
  public async Task ProviderReadsAndCommandsRequireLocalAdmissionAndPrivateResponses() {
    using var database = new TemporaryDatabase();
    var handler = new ScriptedOpenAIHandler("Unexpected provider work");
    await using var server = Start(database, handler);
    Uri origin = await server.WaitForReadyAsync();
    foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Post }) {
      string path = method == HttpMethod.Get ? "/api/providers" : "/api/providers/openai";
      await AssertStatusAsync(server.Client, JsonRequest(method, path, new { name = "Not admitted" }), HttpStatusCode.Forbidden, marker: false);
      foreach (string suppliedOrigin in new[] { "null", "http://foreign.example" }) {
        var request = JsonRequest(method, path, new { name = "Not admitted" });
        request.Headers.Add("Origin", suppliedOrigin);
        await AssertStatusAsync(server.Client, request, HttpStatusCode.Forbidden);
      }
      var wrongHost = JsonRequest(method, path, new { name = "Not admitted" });
      wrongHost.Headers.Host = $"foreign.example:{origin.Port}";
      await AssertStatusAsync(server.Client, wrongHost, HttpStatusCode.Forbidden);
    }
    var sameOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/providers");
    sameOrigin.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
    await AssertStatusAsync(server.Client, sameOrigin, HttpStatusCode.OK);
    Assert.Empty(StoredProviders.Read(database));
    Assert.Empty(handler.Requests);
  }

  [Fact]
  public async Task BodyAndJsonValidationRejectRequestsBeforeMutationOrProviderWork() {
    using var database = new TemporaryDatabase();
    var handler = new ScriptedOpenAIHandler("Unexpected provider work");
    await using var server = Start(database, handler);
    await server.WaitForReadyAsync();
    ProviderConnection connection = await AddCustomAsync(server.Client);
    foreach (string path in new[] { "/api/providers", PathFor(connection), PathFor(connection, "/credentials"), PathFor(connection, "/models") }) {
      foreach (bool chunked in new[] { false, true }) {
        var request = new HttpRequestMessage(HttpMethod.Get, path) { Content = new StringContent("body") };
        if (chunked) request.Headers.TransferEncodingChunked = true;
        await AssertStatusAsync(server.Client, request, HttpStatusCode.BadRequest);
      }
    }
    foreach (string invalid in new[] { "", "{", "null", "[]", "{}", "{\"name\":null}" })
      await AssertStatusAsync(server.Client, RawPost("/api/providers/openai", invalid), HttpStatusCode.BadRequest);
    await AssertStatusAsync(server.Client, RawPost("/api/providers/openai", "{\"name\":\"Wrong media\"}", "text/plain"), HttpStatusCode.UnsupportedMediaType);
    var noMediaType = RawPost("/api/providers/openai", "{\"name\":\"Wrong media\"}");
    noMediaType.Content!.Headers.ContentType = null;
    await AssertStatusAsync(server.Client, noMediaType, HttpStatusCode.UnsupportedMediaType);
    var badCharset = RawPost("/api/providers/openai", "{\"name\":\"Wrong encoding\"}");
    badCharset.Content!.Headers.ContentType!.CharSet = "not-a-real-encoding";
    await AssertStatusAsync(server.Client, badCharset, HttpStatusCode.UnsupportedMediaType);
    foreach (string suffix in new[] { "/remove", "/disconnect", "/models/refresh", "/sign-in" }) {
      foreach (string body in new[] { "", "null", "[]", "{\"unexpected\":true}" })
        await AssertStatusAsync(server.Client, RawPost(PathFor(connection, suffix), body), HttpStatusCode.BadRequest);
    }
    await AssertStatusAsync(server.Client, JsonRequest(HttpMethod.Post, PathFor(connection, "/model-tests")), HttpStatusCode.BadRequest);
    await AssertStatusAsync(server.Client, JsonRequest(HttpMethod.Post, PathFor(connection, "/custom-settings"),
        Settings() with { Authentication = AuthenticationMethod.OAuth }), HttpStatusCode.BadRequest);
    Assert.Equal(connection, Assert.Single(StoredProviders.Read(database)).Connection);
    Assert.Null(Assert.Single(StoredProviders.Read(database)).Credentials);
    Assert.Empty(handler.Requests);
  }

  [Fact]
  public async Task CallbackWithoutValidStateRejectsWithoutMarkerNetworkOrCredentialOutput() {
    using var database = new TemporaryDatabase();
    var handler = new ScriptedOpenAIHandler("Unexpected provider work");
    await using var server = Start(database, handler);
    await server.WaitForReadyAsync();
    ProviderConnection connection = await AddOpenAIAsync(server.Client);
    await SetKeyAsync(server.Client, connection);
    foreach (string query in new[] { "", "?state=", $"?code={CallbackCode}", $"?state=bogus&code={CallbackCode}", "?state=bogus&state=other" }) {
      using HttpResponseMessage response = await SendAsync(server.Client,
          new(HttpMethod.Get, "/auth/callback" + query), HttpStatusCode.BadRequest, marker: false);
      Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }
    await AssertStatusAsync(server.Client, new(HttpMethod.Get, "/auth/callback?state=bogus") {
      Content = new StringContent("body")
    }, HttpStatusCode.BadRequest, marker: false);
    Assert.True((await GetAsync<ProviderCredentialStatus>(server.Client, PathFor(connection, "/credentials"))).HasCredentials);
    Assert.Contains(Secret, Assert.Single(StoredProviders.Read(database)).Credentials!);
    Assert.Empty(StoredProviders.ReadHostIds(database));
    Assert.Empty(handler.Requests);
  }

  private static RunningServer Start(TemporaryDatabase database, ScriptedOpenAIHandler handler, ScriptedChatClient? llm = null) =>
      new(new ServerOptions(Path.GetDirectoryName(database.Path)!, 0), createClient: llm is null ? null : () => llm,
          createProviders: (store, callbackUri) => new ProviderManager(store, callbackUri, handler));

  private static string PathFor(ProviderConnection connection, string suffix = "") => $"/api/providers/{connection.Id:D}{suffix}";

  private static CustomProviderSettings Settings(string endpoint = "https://fixture.invalid/prefix/v1/") =>
      new("Custom fixture", LlmApi.OpenAIResponses, new Uri(endpoint), AuthenticationMethod.ApiKey);

  private static Task<ProviderConnection> AddCustomAsync(HttpClient client) =>
      PostAsync<ProviderConnection>(client, "/api/providers/custom", Settings(), HttpStatusCode.Created);

  private static Task<ProviderConnection> AddOpenAIAsync(HttpClient client) =>
      PostAsync<ProviderConnection>(client, "/api/providers/openai", new { name = "OpenAI fixture" }, HttpStatusCode.Created);

  private static Task<ProviderCredentialStatus> SetKeyAsync(HttpClient client, ProviderConnection connection, string key = Secret) =>
      PostAsync<ProviderCredentialStatus>(client, PathFor(connection, "/api-key"), new { connection, apiKey = key });

  private static async Task<T> GetAsync<T>(HttpClient client, string path) {
    using HttpResponseMessage response = await SendAsync(client, new(HttpMethod.Get, path), HttpStatusCode.OK);
    return (await BodyAsync(response)).Deserialize<T>(json)!;
  }

  private static async Task<T> PostAsync<T>(HttpClient client, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) {
    using HttpResponseMessage response = await SendAsync(client, JsonRequest(HttpMethod.Post, path, body), expected);
    Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    T result = (await BodyAsync(response)).Deserialize<T>(json)!;
    if (expected == HttpStatusCode.Created) {
      var connection = Assert.IsType<ProviderConnection>(result);
      Assert.Equal(PathFor(connection), response.Headers.Location?.OriginalString);
    }
    return result;
  }

  private static HttpRequestMessage JsonRequest(HttpMethod method, string path, object? body = null) => new(method, path) {
    Content = method == HttpMethod.Post ? JsonContent.Create(body ?? new { }, options: json) : null
  };

  private static HttpRequestMessage RawPost(string path, string body, string contentType = "application/json") =>
      new(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };

  private static async Task AssertStatusAsync(HttpClient client, HttpRequestMessage request, HttpStatusCode expected, bool marker = true) {
    using HttpResponseMessage response = await SendAsync(client, request, expected, marker);
  }

  private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, HttpStatusCode expected,
      bool marker = true) {
    using (request) {
      if (marker) request.Headers.Add("X-Urd-Client", "browser");
      HttpResponseMessage response = await client.SendAsync(request);
      Assert.Equal(expected, response.StatusCode);
      Assert.True(response.Headers.CacheControl?.NoStore);
      Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
      Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
      Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
      string body = await response.Content.ReadAsStringAsync();
      foreach (string secret in new[] { Secret, ReplacementSecret, CallbackCode }) {
        Assert.DoesNotContain(secret, body);
        Assert.DoesNotContain(secret, response.Headers.ToString());
      }
      if (response.Content.Headers.ContentType?.MediaType == "application/json" && !string.IsNullOrWhiteSpace(body)) {
        using JsonDocument document = JsonDocument.Parse(body);
        AssertNoCredentialProperties(document.RootElement);
      }
      return response;
    }
  }

  private static void AssertNoCredentialProperties(JsonElement value) {
    if (value.ValueKind == JsonValueKind.Object) {
      foreach (JsonProperty property in value.EnumerateObject()) {
        Assert.False(property.Name is "apiKey" or "accessToken" or "refreshToken" or "idToken",
            "HTTP responses must not expose credential properties.");
        AssertNoCredentialProperties(property.Value);
      }
    } else if (value.ValueKind == JsonValueKind.Array) {
      foreach (JsonElement item in value.EnumerateArray()) AssertNoCredentialProperties(item);
    }
  }

  private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) {
    using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return document.RootElement.Clone();
  }

  private static void AssertNoSessionWork(TemporaryDatabase database, ScriptedChatClient llm) {
    Assert.Empty(StoredSessions.Read(database));
    Assert.Empty(StoredSessions.ReadCreation(database));
    Assert.Empty(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
    Assert.Equal(0, llm.Calls);
  }
}
