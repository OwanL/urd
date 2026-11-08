using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;
using static Urd.Backend.Tests.Support.OpenAIStreams;

namespace Urd.Backend.Tests.Hosting;

// One local HTTP journey composes real OAuth, provider persistence and SDK diagnostics without a session.
public sealed class ChatGptProviderRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    PropertyNameCaseInsensitive = false,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task ChatGptSignInCatalogAndDiagnosticDisconnectWithoutCreatingASession() {
    using var database = new TemporaryDatabase();
    var identity = new FakeOpenAIIdentity { ExpectedRevocationToken = "synthetic-refresh-1" };
    var catalogHandler = new ScriptedOpenAIHandler("""
        {"models":[
          {"slug":"fixture-model","display_name":"Plan model","visibility":"list"},
          {"slug":"hidden-model","visibility":"hide"}
        ],"data":[{"id":"not-the-plan-catalog"}]}
        """, "application/json");
    var responseHandler = new ScriptedOpenAIHandler(ResponseStream());
    var transport = new ChatGptProviderTransport(identity, catalogHandler, responseHandler);
    ProviderConnection connection;
    StoredProviderRow disconnected;
    ProviderRegistration registration;

    await using (var server = new RunningServer(new ServerOptions(Path.GetDirectoryName(database.Path)!, 0),
        createProviders: (store, callbackUri) => new ProviderManager(store, () => {
          identity.CallbackUri = callbackUri();
          return identity.CallbackUri;
        }, transport, FakeOpenAIIdentity.Issuer))) {
      Uri origin = await server.WaitForReadyAsync();
      Assert.Empty(await ApiAsync<ProviderConnection[]>(server.Client, HttpMethod.Get, "/api/providers"));
      connection = await ApiAsync<ProviderConnection>(server.Client, HttpMethod.Post, "/api/providers/chatgpt",
          new { name = "My ChatGPT plan" }, HttpStatusCode.Created);
      Assert.NotEqual(Guid.Empty, connection.Id);
      Assert.Equal("My ChatGPT plan", connection.Name);
      Assert.Equal(ProviderRoute.ChatGpt, connection.Route);
      Assert.Equal(LlmApi.OpenAIResponses, connection.Api);
      Assert.Equal(AuthenticationMethod.OAuth, connection.Authentication);
      Assert.Equal(new Uri("https://api.openai.com/v1/"), connection.Endpoint);
      string path = $"/api/providers/{connection.Id:D}";
      AssertDisconnected(await ApiAsync<ProviderCredentialStatus>(server.Client, HttpMethod.Get, path + "/credentials"),
          connection.Id, accountLabel: null);
      Assert.Empty(catalogHandler.Requests);
      Assert.Empty(responseHandler.Requests);
      Assert.Equal(0, identity.DiscoveryGets);

      ProviderSignIn begin = await ApiAsync<ProviderSignIn>(server.Client, HttpMethod.Post, path + "/sign-in");
      Assert.Equal(connection.Id, begin.ConnectionId);
      Assert.Equal(ProviderSignInStatus.Pending, begin.Status);
      Assert.NotNull(begin.AuthorizationUrl);
      Assert.Null(begin.Error);
      Assert.Equal(new Uri(origin, "/auth/callback"), identity.CallbackUri);
      string callback = await identity.AuthorizeAsync(begin.AuthorizationUrl);
      var callbackUri = new Uri(callback);
      Assert.Equal(identity.CallbackUri.GetLeftPart(UriPartial.Path), callbackUri.GetLeftPart(UriPartial.Path));
      // The issuer redirect lands on the real local route, not an API-marked or in-memory callback.
      Assert.False(server.Client.DefaultRequestHeaders.Contains("X-Urd-Client"));
      using (HttpResponseMessage completed = await server.Client.GetAsync(callbackUri)) {
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        AssertPrivateResponse(completed);
        Assert.Equal("no-referrer", Assert.Single(completed.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("text/plain", completed.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Sign-in completed. You can close this page.", await completed.Content.ReadAsStringAsync());
      }

      ProviderSignIn poll = await ApiAsync<ProviderSignIn>(server.Client, HttpMethod.Get,
          $"/api/providers/sign-ins/{begin.Id:D}");
      Assert.Equal(begin.Id, poll.Id);
      Assert.Equal(connection.Id, poll.ConnectionId);
      Assert.Equal(ProviderSignInStatus.Completed, poll.Status);
      Assert.Null(poll.AuthorizationUrl);
      Assert.Null(poll.Error);
      ProviderCredentialStatus connected = await ApiAsync<ProviderCredentialStatus>(server.Client, HttpMethod.Get,
          path + "/credentials");
      Assert.Equal(connection.Id, connected.ConnectionId);
      Assert.True(connected.HasCredentials);
      Assert.False(connected.RequiresSignIn);
      Assert.False(connected.IsDisconnecting);
      Assert.True(connected.PlanAccessGranted);
      Assert.NotNull(connected.ExpiresAt);
      Assert.Equal("hint@example.invalid", connected.AccountLabel);
      Assert.Equal(connection, await ApiAsync<ProviderConnection>(server.Client, HttpMethod.Get, path));
      Assert.Equal(connection, Assert.Single(await ApiAsync<ProviderConnection[]>(server.Client, HttpMethod.Get,
          "/api/providers")));
      StoredProviderRow signedIn = Assert.Single(StoredProviders.Read(database));
      registration = Assert.IsType<ProviderRegistration>(JsonSerializer.Deserialize<ProviderRegistration>(signedIn.Registration!));
      Assert.Equal(FakeOpenAIIdentity.ClientId, registration.ClientId);
      Assert.Equal(FakeOpenAIIdentity.Subject, registration.Subject);
      Assert.NotNull(signedIn.Credentials);

      ProviderModelCatalog empty = await ApiAsync<ProviderModelCatalog>(server.Client, HttpMethod.Get, path + "/models");
      Assert.Null(empty.RefreshedAt);
      Assert.Empty(empty.Models);
      Assert.Empty(catalogHandler.Requests);
      ProviderModelCatalog catalog = await ApiAsync<ProviderModelCatalog>(server.Client, HttpMethod.Post,
          path + "/models/refresh");
      Assert.Equal(connection.Id, catalog.ConnectionId);
      Assert.NotNull(catalog.RefreshedAt);
      Assert.Equal(new ProviderModel("fixture-model", "Plan model"), Assert.Single(catalog.Models));
      ProviderModelCatalog cached = await ApiAsync<ProviderModelCatalog>(server.Client, HttpMethod.Get, path + "/models");
      Assert.Equal(catalog.RefreshedAt, cached.RefreshedAt);
      Assert.Equal(catalog.Models, cached.Models);
      ScriptedOpenAIRequest catalogRequest = Assert.Single(catalogHandler.Requests);
      Assert.Equal(HttpMethod.Get, catalogRequest.Method);
      Assert.Equal(new Uri("https://api.openai.com/v1/models"), catalogRequest.Uri);
      Assert.Equal("Bearer synthetic-access-1", catalogRequest.Authorization);

      ProviderModelTestResult result = await ApiAsync<ProviderModelTestResult>(server.Client, HttpMethod.Post,
          path + "/model-tests", new { modelId = Assert.Single(catalog.Models).Id });
      Assert.Equal(connection.Id, result.ConnectionId);
      Assert.Equal("fixture-model", result.ModelId);
      Assert.Equal("hello", result.Text);
      Assert.False(result.Truncated);
      ScriptedOpenAIRequest modelRequest = Assert.Single(responseHandler.Requests);
      Assert.Equal(HttpMethod.Post, modelRequest.Method);
      Assert.Equal(new Uri("https://api.openai.com/v1/responses"), modelRequest.Uri);
      Assert.Equal("Bearer synthetic-access-1", modelRequest.Authorization);
      using (JsonDocument request = JsonDocument.Parse(modelRequest.Body)) {
        Assert.Equal("fixture-model", request.RootElement.GetProperty("model").GetString());
        Assert.True(request.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(request.RootElement.GetProperty("store").GetBoolean());
      }
      Assert.Equal(1, responseHandler.StreamDisposals);
      Assert.Empty(StoredSessions.Read(database));

      ProviderDisconnectResult disconnect = await ApiAsync<ProviderDisconnectResult>(server.Client, HttpMethod.Post,
          path + "/disconnect");
      Assert.Equal(connection.Id, disconnect.ConnectionId);
      Assert.True(disconnect.LocalCredentialsCleared);
      Assert.True(disconnect.RemoteRevocationConfirmed);
      Assert.Equal(1, identity.CodePosts);
      Assert.Equal(0, identity.RefreshPosts);
      Assert.Equal(1, identity.RevocationPosts);
      AssertDisconnected(await ApiAsync<ProviderCredentialStatus>(server.Client, HttpMethod.Get, path + "/credentials"),
          connection.Id, connected.AccountLabel);
      Assert.Equal(connection, await ApiAsync<ProviderConnection>(server.Client, HttpMethod.Get, path));
      ProviderModelCatalog cleared = await ApiAsync<ProviderModelCatalog>(server.Client, HttpMethod.Get, path + "/models");
      Assert.Null(cleared.RefreshedAt);
      Assert.Empty(cleared.Models);
      disconnected = Assert.Single(StoredProviders.Read(database));
      Assert.Equal(connection, disconnected.Connection);
      Assert.Equal(signedIn.Registration, disconnected.Registration);
      Assert.Null(disconnected.Credentials);
      Assert.Null(disconnected.Catalog);
      Assert.False(disconnected.Disconnecting);
    }

    Assert.True(identity.Disposed);
    using (UrdStore reopened = UrdStore.Open(database.Path)) {
      Assert.Equal(connection, Assert.Single(reopened.Providers.List()));
      ProviderState retained = reopened.Providers.Get(connection.Id);
      Assert.Equal(registration, retained.Registration);
      Assert.Null(retained.Credentials);
      Assert.Null(retained.Catalog);
      Assert.False(retained.Disconnecting);
    }
    var reopenedIdentity = new FakeOpenAIIdentity();
    var reopenedCatalog = new ScriptedOpenAIHandler("{\"models\":[]}", "application/json");
    var reopenedResponses = new ScriptedOpenAIHandler(ResponseStream());
    await using (var restarted = new RunningServer(new ServerOptions(Path.GetDirectoryName(database.Path)!, 0),
        createProviders: (store, callbackUri) => new ProviderManager(store, callbackUri,
            new ChatGptProviderTransport(reopenedIdentity, reopenedCatalog, reopenedResponses), FakeOpenAIIdentity.Issuer))) {
      await restarted.WaitForReadyAsync();
      Assert.Equal(connection, Assert.Single(await ApiAsync<ProviderConnection[]>(restarted.Client, HttpMethod.Get,
          "/api/providers")));
      string path = $"/api/providers/{connection.Id:D}";
      AssertDisconnected(await ApiAsync<ProviderCredentialStatus>(restarted.Client, HttpMethod.Get, path + "/credentials"),
          connection.Id, registration.Email);
      ProviderModelCatalog cleared = await ApiAsync<ProviderModelCatalog>(restarted.Client, HttpMethod.Get, path + "/models");
      Assert.Null(cleared.RefreshedAt);
      Assert.Empty(cleared.Models);
      Assert.Equal(0, reopenedIdentity.DiscoveryGets);
      Assert.Empty(reopenedCatalog.Requests);
      Assert.Empty(reopenedResponses.Requests);
    }
    Assert.True(reopenedIdentity.Disposed);
    Assert.Equal(disconnected, Assert.Single(StoredProviders.Read(database)));
    Assert.Single(catalogHandler.Requests);
    Assert.Single(responseHandler.Requests);
    Assert.Empty(StoredSessions.Read(database));
    Assert.Empty(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
    Assert.Empty(StoredExecutions.ReadRequests(database));
  }

  private static void AssertDisconnected(ProviderCredentialStatus status, Guid connectionId, string? accountLabel) {
    Assert.Equal(connectionId, status.ConnectionId);
    Assert.False(status.HasCredentials);
    Assert.False(status.IsDisconnecting);
    Assert.False(status.PlanAccessGranted);
    Assert.Null(status.ExpiresAt);
    Assert.Equal(accountLabel, status.AccountLabel);
  }

  private static async Task<T> ApiAsync<T>(HttpClient client, HttpMethod method, string path, object? body = null,
      HttpStatusCode expected = HttpStatusCode.OK) {
    using var request = new HttpRequestMessage(method, path);
    request.Headers.Add("X-Urd-Client", "browser");
    if (method == HttpMethod.Post)
      request.Content = JsonContent.Create(body ?? new { }, options: json);
    using HttpResponseMessage response = await client.SendAsync(request);
    Assert.Equal(expected, response.StatusCode);
    AssertPrivateResponse(response);
    Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    string content = await response.Content.ReadAsStringAsync();
    foreach (string secret in new[] { "synthetic-access", "synthetic-refresh", "synthetic-code" })
      Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
    using JsonDocument document = JsonDocument.Parse(content);
    AssertCredentialFree(document.RootElement);
    return Assert.IsType<T>(JsonSerializer.Deserialize<T>(content, json));
  }

  private static void AssertCredentialFree(JsonElement value) {
    if (value.ValueKind == JsonValueKind.Array) {
      foreach (JsonElement item in value.EnumerateArray())
        AssertCredentialFree(item);
      return;
    }
    if (value.ValueKind != JsonValueKind.Object)
      return;
    foreach (JsonProperty property in value.EnumerateObject()) {
      Assert.DoesNotContain(property.Name, new[] {
        "apiKey", "accessToken", "refreshToken", "idToken", "credentials", "registration", "clientSecret", "codeVerifier", "nonce",
        "api_key", "access_token", "refresh_token", "id_token", "client_secret", "code_verifier", "clientId", "subject"
      });
      AssertCredentialFree(property.Value);
    }
  }
}
