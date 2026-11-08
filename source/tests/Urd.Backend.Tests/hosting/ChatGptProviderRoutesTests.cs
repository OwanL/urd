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

// Keeps the ChatGPT sign-in callback protocol on real HTTP with serialized provider state.
public sealed class ChatGptProviderRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    PropertyNameCaseInsensitive = false,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task SignInCallbackAndPollingDoNotDiscoverModelsOrStartSessionWork() {
    using var database = new TemporaryDatabase();
    var identity = new FakeOpenAIIdentity();
    var catalog = new ScriptedOpenAIHandler("Unexpected catalog request");
    var responses = new ScriptedOpenAIHandler("Unexpected diagnostic request");
    var transport = new ChatGptProviderTransport(identity, catalog, responses);
    await using var server = new RunningServer(new ServerOptions(Path.GetDirectoryName(database.Path)!, 0),
        createProviders: (store, callbackUri) => new ProviderManager(store, () => {
          identity.CallbackUri = callbackUri();
          return identity.CallbackUri;
        }, transport, FakeOpenAIIdentity.Issuer));
    Uri origin = await server.WaitForReadyAsync();

    using (HttpResponseMessage invalid = await server.Client.GetAsync("/auth/callback?state=bogus&code=invalid")) {
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
      AssertPrivateResponse(invalid);
      Assert.Equal("no-referrer", Assert.Single(invalid.Headers.GetValues("Referrer-Policy")));
    }

    ProviderConnection connection = await ApiAsync<ProviderConnection>(server.Client, HttpMethod.Post,
        "/api/providers/chatgpt", new { name = "Plan account" }, HttpStatusCode.Created);
    Assert.Equal(ProviderRoute.ChatGpt, connection.Route);
    Assert.Equal(AuthenticationMethod.OAuth, connection.Authentication);
    Assert.Equal(0, identity.DiscoveryGets);
    string path = $"/api/providers/{connection.Id:D}";
    ProviderSignIn signIn = await ApiAsync<ProviderSignIn>(server.Client, HttpMethod.Post, path + "/sign-in");
    Assert.Equal(connection.Id, signIn.ConnectionId);
    Assert.Equal(ProviderSignInStatus.Pending, signIn.Status);
    Assert.NotNull(signIn.AuthorizationUrl);
    Assert.Equal(new Uri(origin, "/auth/callback"), identity.CallbackUri);
    Assert.Equal(1, identity.DiscoveryGets);

    Uri callback = new(await identity.AuthorizeAsync(signIn.AuthorizationUrl));
    using (HttpResponseMessage completed = await server.Client.GetAsync(callback)) {
      Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
      AssertPrivateResponse(completed);
      Assert.Equal("no-referrer", Assert.Single(completed.Headers.GetValues("Referrer-Policy")));
      Assert.Equal("text/plain", completed.Content.Headers.ContentType?.MediaType);
      Assert.Contains("Sign-in completed.", await completed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    ProviderSignIn poll = await ApiAsync<ProviderSignIn>(server.Client, HttpMethod.Get,
        $"/api/providers/sign-ins/{signIn.Id:D}");
    Assert.Equal(signIn.Id, poll.Id);
    Assert.Equal(ProviderSignInStatus.Completed, poll.Status);
    ProviderCredentialStatus credentials = await ApiAsync<ProviderCredentialStatus>(server.Client, HttpMethod.Get,
        path + "/credentials");
    Assert.True(credentials.HasCredentials);
    Assert.True(credentials.PlanAccessGranted);
    Assert.Equal("hint@example.invalid", credentials.AccountLabel);
    Assert.Empty(catalog.Requests);
    Assert.Empty(responses.Requests);
    Assert.Equal(1, identity.DiscoveryGets);
    Assert.Empty(StoredSessions.Read(database));
    Assert.Empty(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
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
    if (typeof(T) == typeof(ProviderSignIn))
      Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("status").ValueKind);
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
      Assert.DoesNotContain(property.Name, new[] { "apiKey", "accessToken", "refreshToken", "idToken", "credentials", "registration" });
      AssertCredentialFree(property.Value);
    }
  }
}
