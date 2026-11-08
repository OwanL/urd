using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Server;
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
    await using var server = new RunningServer(new ServerOptions(Path.GetDirectoryName(database.Path)!, 0),
        createProviders: (store, callbackUri) => new ProviderManager(store, callbackUri, handler));
    await server.WaitForReadyAsync();

    Assert.Empty(await GetAsync<ProviderConnection[]>(server.Client, "/api/providers"));
    ProviderConnection connection = await PostAsync<ProviderConnection>(server.Client, "/api/providers/custom",
        new CustomProviderSettings("Fixture", LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/prefix/v1/"),
            AuthenticationMethod.ApiKey), HttpStatusCode.Created);
    Assert.Equal(ProviderRoute.Custom, connection.Route);
    Assert.Equal(connection, await GetAsync<ProviderConnection>(server.Client, PathFor(connection)));
    Assert.Equal(connection, Assert.Single(await GetAsync<ProviderConnection[]>(server.Client, "/api/providers")));
    Assert.Empty(handler.Requests);

    ProviderCredentialStatus credentials = await PostAsync<ProviderCredentialStatus>(server.Client,
        PathFor(connection, "/api-key"), new { connection, apiKey = Secret });
    Assert.True(credentials.HasCredentials);
    Assert.Contains(Secret, Assert.Single(StoredProviders.Read(database)).Credentials!);

    ProviderModelCatalog empty = await GetAsync<ProviderModelCatalog>(server.Client, PathFor(connection, "/models"));
    Assert.Null(empty.RefreshedAt);
    Assert.Empty(empty.Models);
    Assert.Empty(handler.Requests);

    ProviderModelCatalog refreshed = await PostAsync<ProviderModelCatalog>(server.Client,
        PathFor(connection, "/models/refresh"));
    Assert.NotNull(refreshed.RefreshedAt);
    Assert.Equal(new ProviderModel("fixture-model", "fixture-model"), Assert.Single(refreshed.Models));
    ScriptedOpenAIRequest request = Assert.Single(handler.Requests);
    Assert.Equal(HttpMethod.Get, request.Method);
    Assert.Equal("/prefix/v1/models", request.Uri.AbsolutePath);
    Assert.Equal($"Bearer {Secret}", request.Authorization);
    Assert.Equal(refreshed.Models, (await GetAsync<ProviderModelCatalog>(server.Client, PathFor(connection, "/models"))).Models);
    Assert.Single(handler.Requests);

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
