using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Preferences;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

// Exercises provider CRUD and explicit catalog refresh over the real local HTTP routes.
public sealed class ProviderRoutesTests {
  private const string Secret = "synthetic-provider-secret-do-not-return";
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    PropertyNameCaseInsensitive = false,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task ProviderCrudRefreshIsExplicitAndResponsesNeverExposeCredentials() {
    using var database = new TemporaryDatabase();
    var handler = new ScriptedOpenAIHandler("""
        {"object":"list","data":[{"id":"fixture-model","object":"model","created":1700000000,"owned_by":"fixture"}]}
        """, "application/json");
    await using var server = new RunningServer(RunningServer.OptionsFor(database),
        createProviders: (store, callbackUri) => new ProviderManager(store, callbackUri, handler));
    await server.WaitForReadyAsync();

    Assert.Empty(await GetAsync<ProviderConnection[]>(server.Client, "/api/providers"));
    ProviderConnection connection = await PostAsync<ProviderConnection>(server.Client, "/api/providers/custom",
        new CustomProviderSettings("Fixture", LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/prefix/v1/"),
            AuthenticationMethod.ApiKey), HttpStatusCode.Created);
    Assert.Equal(ProviderRoute.Custom, connection.Route);
    Assert.Equal(connection, await GetAsync<ProviderConnection>(server.Client, PathFor(connection)));
    Assert.Equal(connection, Assert.Single(await GetAsync<ProviderConnection[]>(server.Client, "/api/providers")));
    Assert.Empty(await GetAsync<ProviderConnection[]>(server.Client, "/api/providers/available"));
    Assert.Empty(handler.Requests);

    ProviderCredentialStatus credentials = await PostAsync<ProviderCredentialStatus>(server.Client,
        PathFor(connection, "/api-key"), new { connection, apiKey = Secret });
    Assert.True(credentials.HasCredentials);
    Assert.Equal(connection,
        Assert.Single(await GetAsync<ProviderConnection[]>(server.Client, "/api/providers/available")));
    Assert.Contains(Secret, Assert.Single(StoredProviders.Read(database)).Credentials!);

    ProviderModelCatalog empty = await GetAsync<ProviderModelCatalog>(server.Client, PathFor(connection, "/models"));
    Assert.Null(empty.RefreshedAt);
    Assert.Empty(empty.Models);
    Assert.Empty(handler.Requests);

    string settingsPath = PathFor(connection, "/model-settings");
    Assert.Empty(await GetAsync<ModelSettings[]>(server.Client, settingsPath));
    var settings = new ModelSettings(connection.Id, "fixture-model", ModelFamily.OpenAI, true);
    Assert.Equal(settings, await PostAsync<ModelSettings>(server.Client, settingsPath, settings));
    Assert.Equal(settings, Assert.Single(await GetAsync<ModelSettings[]>(server.Client, settingsPath)));
    using HttpResponseMessage wrongIdentity = await SendAsync(server.Client, HttpMethod.Post, settingsPath,
        settings with { ConnectionId = Guid.NewGuid() }, HttpStatusCode.BadRequest);
    using HttpResponseMessage missingCapability = await SendAsync(server.Client, HttpMethod.Post, settingsPath,
        new { connectionId = connection.Id, modelId = "missing-capability", family = "other" }, HttpStatusCode.BadRequest);
    Assert.Empty(handler.Requests);

    ProviderModelCatalog refreshed = await PostAsync<ProviderModelCatalog>(server.Client,
        PathFor(connection, "/models/refresh"));
    Assert.NotNull(refreshed.RefreshedAt);
    Assert.Equal(new ProviderModel("fixture-model", "fixture-model", Family: ModelFamily.Other),
        Assert.Single(refreshed.Models));
    ScriptedOpenAIRequest request = Assert.Single(handler.Requests);
    Assert.Equal(HttpMethod.Get, request.Method);
    Assert.Equal("/prefix/v1/models", request.Uri.AbsolutePath);
    Assert.Equal($"Bearer {Secret}", request.Authorization);
    Assert.Equal(refreshed.Models, (await GetAsync<ProviderModelCatalog>(server.Client, PathFor(connection, "/models"))).Models);
    Assert.Single(handler.Requests);
    Assert.Equal(settings, Assert.Single(await GetAsync<ModelSettings[]>(server.Client, settingsPath)));

    ProviderConnection renamed = await PostAsync<ProviderConnection>(server.Client,
        PathFor(connection, "/rename"), new { name = "Renamed fixture" });
    Assert.Equal("Renamed fixture", renamed.Name);
    Assert.Equal(renamed, await GetAsync<ProviderConnection>(server.Client, PathFor(connection)));
    using (HttpResponseMessage removed = await SendAsync(server.Client, HttpMethod.Post,
        PathFor(connection, "/remove"), expected: HttpStatusCode.NoContent)) {
      Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
      Assert.Empty(await removed.Content.ReadAsByteArrayAsync());
    }
    Assert.Empty(await GetAsync<ProviderConnection[]>(server.Client, "/api/providers"));
    using (HttpResponseMessage missing = await SendAsync(server.Client, HttpMethod.Get, PathFor(connection),
        expected: HttpStatusCode.NotFound)) {
      Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
      Assert.Empty(await missing.Content.ReadAsByteArrayAsync());
    }
    Assert.Empty(StoredProviders.Read(database));
    Assert.Empty(StoredSessions.Read(database));
    Assert.Empty(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
  }

  [Fact]
  public async Task CopilotConnectionUsesBundledVsCodeOAuthConfiguration() {
    using var database = new TemporaryDatabase();
    var handler = new ScriptedOpenAIHandler($$"""
        {"device_code":"{{Secret}}","user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device",
        "expires_in":600,"interval":60}
        """, "application/json");
    await using var server = new RunningServer(RunningServer.OptionsFor(database),
        createProviders: (store, callbackUri) => new ProviderManager(store, callbackUri, handler));
    await server.WaitForReadyAsync();

    ProviderConnection connection = await PostAsync<ProviderConnection>(server.Client, "/api/providers/github-copilot",
        new { name = "GitHub Copilot" }, HttpStatusCode.Created);
    Assert.Equal(ProviderRoute.GitHubCopilot, connection.Route);
    Assert.Equal(AuthenticationMethod.OAuth, connection.Authentication);
    using var listed = await SendAsync(server.Client, HttpMethod.Get, PathFor(connection));
    JsonElement binding = await listed.Content.ReadFromJsonAsync<JsonElement>(json);
    Assert.Equal("gitHubCopilot", binding.GetProperty("route").GetString());
    Assert.Equal("GitHub Copilot", binding.GetProperty("name").GetString());

    ProviderSignIn signIn = await PostAsync<ProviderSignIn>(server.Client, PathFor(connection, "/sign-in"));
    Assert.Equal(ProviderSignInStatus.Pending, signIn.Status);
    Assert.Equal("ABCD-EFGH", signIn.UserCode);
    ScriptedOpenAIRequest request = Assert.Single(handler.Requests);
    Assert.Equal($"client_id={GitHubCopilotAuthentication.DefaultClientId}&scope=read%3Auser", request.Body);
    await PostAsync<ProviderSignIn>(server.Client, $"/api/providers/sign-ins/{signIn.Id:D}/cancel");
  }

  [Fact]
  public async Task CopilotSignInExposesOnlyTheUserCodeAndClearsItWhenCancelled() {
    using var database = new TemporaryDatabase();
    var handler = new ScriptedOpenAIHandler($$"""
        {"device_code":"{{Secret}}","user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device",
        "expires_in":600,"interval":60}
        """, "application/json");
    await using var server = new RunningServer(RunningServer.OptionsFor(database),
        createProviders: (store, callbackUri) => new ProviderManager(store, callbackUri, handler,
            copilotClientId: "synthetic-client-id"));
    await server.WaitForReadyAsync();
    ProviderConnection connection = await PostAsync<ProviderConnection>(server.Client, "/api/providers/github-copilot",
        new { name = "GitHub Copilot" }, HttpStatusCode.Created);

    using var started = await SendAsync(server.Client, HttpMethod.Post, PathFor(connection, "/sign-in"));
    JsonElement signIn = await started.Content.ReadFromJsonAsync<JsonElement>(json);
    Assert.Equal("pending", signIn.GetProperty("status").GetString());
    Assert.Equal("ABCD-EFGH", signIn.GetProperty("userCode").GetString());
    Assert.Equal("https://github.com/login/device", signIn.GetProperty("authorizationUrl").GetString());
    Assert.False(signIn.TryGetProperty("deviceCode", out _));
    string signInPath = $"/api/providers/sign-ins/{signIn.GetProperty("id").GetGuid():D}";
    ProviderSignIn cancelled = await PostAsync<ProviderSignIn>(server.Client, $"{signInPath}/cancel");
    Assert.Equal(ProviderSignInStatus.Cancelled, cancelled.Status);
    Assert.Null(cancelled.UserCode);
    Assert.Null(cancelled.AuthorizationUrl);
    Assert.Equal("https://github.com/login/device/code", Assert.Single(handler.Requests).Uri.AbsoluteUri);
  }

  [Fact]
  public async Task OAuthCallbackClosesPageOnlyAfterSuccessfulSignIn() {
    using var database = new TemporaryDatabase();
    using var identity = new FakeOpenAIIdentity();
    await using var server = new RunningServer(RunningServer.OptionsFor(database),
        onReady: origin => identity.CallbackUri = new Uri(origin, "/auth/callback"),
        createProviders: (store, callbackUri) =>
            new ProviderManager(store, callbackUri, identity, FakeOpenAIIdentity.Issuer));
    await server.WaitForReadyAsync();
    ProviderConnection connection = await PostAsync<ProviderConnection>(server.Client, "/api/providers/chatgpt",
        new { name = "ChatGPT" }, HttpStatusCode.Created);

    ProviderSignIn success = await PostAsync<ProviderSignIn>(server.Client,
        PathFor(connection, "/sign-in"));
    string successCallback = await identity.AuthorizeAsync(success.AuthorizationUrl!);
    using (HttpResponseMessage completed = await SendAsync(server.Client, HttpMethod.Get,
        new Uri(successCallback).PathAndQuery)) {
      Assert.Equal("text/html", completed.Content.Headers.ContentType?.MediaType);
      Assert.Contains("window.close()", await completed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    identity.Fault = "wrong-key";
    ProviderSignIn failed = await PostAsync<ProviderSignIn>(server.Client,
        PathFor(connection, "/sign-in"));
    string failedCallback = await identity.AuthorizeAsync(failed.AuthorizationUrl!);
    using (HttpResponseMessage rejected = await SendAsync(server.Client, HttpMethod.Get,
        new Uri(failedCallback).PathAndQuery)) {
      string content = await rejected.Content.ReadAsStringAsync();
      Assert.DoesNotContain("window.close()", content, StringComparison.Ordinal);
    }
  }

  [Fact]
  public async Task BucketSuggestionsRequireExplicitUnchangedConfirmation() {
    using var database = new TemporaryDatabase();
    var handler = new ScriptedOpenAIHandler("""
        {"object":"list","data":[
          {"id":"gpt-9-luna","object":"model","created":1700000000,"owned_by":"fixture"},
          {"id":"gpt-10-luna","object":"model","created":1700000000,"owned_by":"fixture"},
          {"id":"gpt-2.10-terra","object":"model","created":1700000000,"owned_by":"fixture"},
          {"id":"gpt-2.9-terra","object":"model","created":1700000000,"owned_by":"fixture"},
          {"id":"unrelated-sol","object":"model","created":1700000000,"owned_by":"fixture"}]}
        """, "application/json");
    await using var server = new RunningServer(RunningServer.OptionsFor(database),
        createProviders: (store, callbackUri) => new ProviderManager(store, callbackUri, handler));
    await server.WaitForReadyAsync();
    ProviderConnection connection = await PostAsync<ProviderConnection>(server.Client, "/api/providers/custom",
        new CustomProviderSettings("Fixture", LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/v1/"),
            AuthenticationMethod.None), HttpStatusCode.Created);
    string path = PathFor(connection, "/bucket-suggestions");
    Assert.Empty((await GetAsync<BucketSuggestions>(server.Client, path)).Assignments);
    Assert.Empty(handler.Requests);
    await PostAsync<ProviderModelCatalog>(server.Client, PathFor(connection, "/models/refresh"));
    BucketSuggestions proposal = await GetAsync<BucketSuggestions>(server.Client, path);
    Assert.Equal(new[] { new BucketAssignment(ModelBucket.Small, "gpt-10-luna"),
        new BucketAssignment(ModelBucket.Medium, "gpt-2.10-terra") }, proposal.Assignments);
    UserPreferences before = await GetAsync<UserPreferences>(server.Client, "/api/preferences");
    Assert.Empty(before.ModelChoices.Small);
    Assert.Empty(before.ModelChoices.Medium);
    Assert.Single(handler.Requests);

    await PostAsync<UserPreferences>(server.Client, "/api/preferences/model-bucket",
        new { bucket = ModelBucket.Small, models = new[] { new ConfiguredModel(connection.Id, "manual") } });
    using var rejected = await SendAsync(server.Client, HttpMethod.Post, path, proposal, HttpStatusCode.Conflict);
    UserPreferences stale = await GetAsync<UserPreferences>(server.Client, "/api/preferences");
    Assert.Equal("manual", Assert.Single(stale.ModelChoices.Small).ModelId);
    Assert.Empty(stale.ModelChoices.Medium);
    BucketSuggestions revised = await GetAsync<BucketSuggestions>(server.Client, path);
    Assert.Equal(ModelBucket.Medium, Assert.Single(revised.Assignments).Bucket);
    using var confirmed = await SendAsync(server.Client, HttpMethod.Post, path, revised, HttpStatusCode.NoContent);
    UserPreferences saved = await GetAsync<UserPreferences>(server.Client, "/api/preferences");
    Assert.Equal("manual", Assert.Single(saved.ModelChoices.Small).ModelId);
    Assert.Equal("gpt-2.10-terra", Assert.Single(saved.ModelChoices.Medium).ModelId);
    using var replay = await SendAsync(server.Client, HttpMethod.Post, path, revised, HttpStatusCode.Conflict);
    using var malformed = await SendAsync(server.Client, HttpMethod.Post, path, new { connection }, HttpStatusCode.BadRequest);
  }

  private static string PathFor(ProviderConnection connection, string suffix = "") => $"/api/providers/{connection.Id:D}{suffix}";

  private static async Task<T> GetAsync<T>(HttpClient client, string path) {
    using HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, path);
    return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>(json));
  }

  private static async Task<T> PostAsync<T>(HttpClient client, string path, object? body = null,
      HttpStatusCode expected = HttpStatusCode.OK) {
    using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, path, body, expected);
    T result = Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>(json));
    if (expected == HttpStatusCode.Created) {
      ProviderConnection connection = Assert.IsType<ProviderConnection>(result);
      Assert.Equal(PathFor(connection), response.Headers.Location?.OriginalString);
    }
    return result;
  }

  private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path,
      object? body = null, HttpStatusCode expected = HttpStatusCode.OK) {
    using var request = new HttpRequestMessage(method, path);
    request.Headers.Add("X-Urd-Client", "browser");
    if (method == HttpMethod.Post)
      request.Content = JsonContent.Create(body ?? new { }, options: json);
    HttpResponseMessage response = await client.SendAsync(request);
    Assert.Equal(expected, response.StatusCode);
    AssertPrivateResponse(response);
    string content = await response.Content.ReadAsStringAsync();
    Assert.DoesNotContain(Secret, content, StringComparison.Ordinal);
    Assert.DoesNotContain(Secret, response.Headers.ToString(), StringComparison.Ordinal);
    return response;
  }
}
