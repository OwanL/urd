using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

public sealed class GitHubCopilotProviderTests {
  private const string ClientId = "fixture-client";
  private const string AccessToken = "synthetic-copilot-access";
  private const string Catalog = """
      {"data":[
        {"id":"fixture-responses","name":"Responses","model_picker_enabled":true,"supported_endpoints":["/responses","/chat/completions"],
          "capabilities":{"limits":{"max_context_window_tokens":32768,"max_output_tokens":8192},"supports":{"tool_calls":false,"reasoning_effort":["low","high"]}},
          "billing":{"token_prices":{"default":{"max_prompt_tokens":16384}}}},
        {"id":"fixture-chat","model_picker_enabled":true,"supported_endpoints":["/chat/completions"]},
        {"id":"fixture-messages","vendor":"Anthropic","model_picker_enabled":true,"supported_endpoints":["/chat/completions","/v1/messages"]},
        {"id":"hidden","model_picker_enabled":false,"supported_endpoints":["/responses"]},
        {"id":"disabled","model_picker_enabled":true,"supported_endpoints":["/responses"],"policy":{"state":"disabled"}},
        {"id":"unsupported","model_picker_enabled":true,"supported_endpoints":["/future"]}
      ]}
      """;

  [Theory]
  [InlineData("fixture-responses", LlmApi.OpenAIResponses, "/responses")]
  [InlineData("fixture-chat", LlmApi.OpenAIChatCompletions, "/chat/completions")]
  [InlineData("fixture-messages", LlmApi.AnthropicMessages, "/v1/messages")]
  public async Task DiscoversSelectableModelsAndStreamsUsingTheirFrozenApi(string model, LlmApi api, string path) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = new ControlledProviderHandler((request, _) => {
      Assert.Equal("api.individual.githubcopilot.com", request.RequestUri!.Host);
      Assert.Equal($"Bearer {AccessToken}", request.Headers.Authorization?.ToString());
      AssertCopilotHeaders(request);
      if (request.Method == HttpMethod.Get) {
        Assert.Equal("/models", request.RequestUri.AbsolutePath);
        return Task.FromResult(Reply(Catalog, "application/json"));
      }
      Assert.Equal(path, request.RequestUri.AbsolutePath);
      Assert.Equal("user", Assert.Single(request.Headers.GetValues("X-Initiator")));
      return Task.FromResult(Reply(api switch {
        LlmApi.OpenAIResponses => ProviderModelStreams.Responses(model: model),
        LlmApi.OpenAIChatCompletions => ProviderModelStreams.ChatText("hello", model),
        LlmApi.AnthropicMessages => AnthropicStream,
        _ => throw new InvalidOperationException($"Unexpected fixture protocol: {api}")
      }, "text/event-stream", request));
    });
    var manager = new ProviderManager(store, () => new Uri("http://localhost/auth/callback"), handler, copilotClientId: ClientId);
    try {
      ProviderConnection connection = await manager.AddGitHubCopilotAsync("GitHub Copilot");
      store.Providers.SetCredentials(connection.Id, 0, new(ClientId, "42", "octocat"),
          new(null, AccessToken, "synthetic-github-access", null, DateTimeOffset.UtcNow.AddHours(1), []));

      ProviderModelCatalog catalog = await manager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      Assert.Collection(catalog.Models,
          item => {
            Assert.Equal("fixture-responses", item.Id);
            Assert.Equal(LlmApi.OpenAIResponses, item.Api);
            Assert.Equal(16384, item.ContextWindow);
            Assert.Equal(8192, item.MaxOutputTokens);
            Assert.Equal(new[] { "low", "high" }, item.ReasoningEfforts);
          },
          item => Assert.Equal(LlmApi.OpenAIChatCompletions, item.Api),
          item => Assert.Equal(LlmApi.AnthropicMessages, item.Api));
      Assert.Equal(catalog.Models.Select(item => (item.Id, item.Api)),
          (await manager.GetModelCatalogAsync(connection.Id)).Models.Select(item => (item.Id, item.Api)));
      Assert.Equal(LlmApi.OpenAIResponses, (await manager.GetAsync(connection.Id)).Api);

      using IChatClient client = manager.CreateChatClient(connection, api);
      List<ChatResponseUpdate> updates = await ReadAsync(client, model).WaitAsync(TestTimeout);
      Assert.Equal("hello", string.Concat(updates.SelectMany(update => update.Contents.OfType<TextContent>()).Select(text => text.Text)));
      Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);

      using IChatClient stale = manager.CreateChatClient(connection,
          api == LlmApi.OpenAIResponses ? LlmApi.OpenAIChatCompletions : LlmApi.OpenAIResponses);
      var changed = await Assert.ThrowsAsync<ProviderOperationException>(() => ReadAsync(stale, model).WaitAsync(TestTimeout));
      Assert.Equal(ProviderFailure.ConnectionChanged, changed.Failure);
      Assert.Equal(2, handler.Requests.Count);

      ProviderModelTestResult diagnostic = await manager.TestModelAsync(connection.Id, model).WaitAsync(TestTimeout);
      Assert.Equal("hello", diagnostic.Text);
      Assert.False(diagnostic.Truncated);
      if (api == LlmApi.AnthropicMessages) {
        Assert.Equal(3, diagnostic.Usage?.InputTokens);
        Assert.Equal(2, diagnostic.Usage?.OutputTokens);
      }
      Assert.Equal(3, handler.Requests.Count);

      ProviderDisconnectResult disconnected = await manager.DisconnectAsync(connection.Id).WaitAsync(TestTimeout);
      Assert.True(disconnected.LocalCredentialsCleared);
      Assert.Null(disconnected.RemoteRevocationConfirmed);
      Assert.Empty((await manager.GetModelCatalogAsync(connection.Id)).Models);
      Assert.Equal(3, handler.Requests.Count);
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  [Fact]
  public async Task ExpiredServiceTokenIsExchangedBeforeCatalogAccess() {
    const string githubToken = "synthetic-github-access";
    const string refreshedToken = "tid=fixture;proxy-ep=proxy.business.githubcopilot.com;example";
    long expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid connectionId = Guid.Empty;
    var handler = new ControlledProviderHandler((request, _) => {
      if (request.RequestUri == new Uri("https://api.github.com/copilot_internal/v2/token")) {
        Assert.Equal($"Bearer {githubToken}", request.Headers.Authorization?.ToString());
        AssertCopilotIdentity(request);
        return Task.FromResult(Reply($$"""{"token":"{{refreshedToken}}","expires_at":{{expiresAt}}}""", "application/json"));
      }
      Assert.Equal(new Uri("https://api.business.githubcopilot.com/models"), request.RequestUri);
      Assert.Null(store.Providers.Get(connectionId).Catalog);
      Assert.Equal($"Bearer {refreshedToken}", request.Headers.Authorization?.ToString());
      AssertCopilotHeaders(request);
      return Task.FromResult(Reply(Catalog, "application/json"));
    });
    var manager = new ProviderManager(store, () => new Uri("http://localhost/auth/callback"), handler,
        copilotClientId: ClientId);
    try {
      ProviderConnection connection = await manager.AddGitHubCopilotAsync("GitHub Copilot");
      connectionId = connection.Id;
      ProviderState state = store.Providers.SetCredentials(connection.Id, 0, new(ClientId, "42", "octocat"),
          new(null, "expired-service-token", githubToken, null, DateTimeOffset.UtcNow.AddMinutes(-1), []));
      Assert.True(store.Providers.SaveCatalog(connection.Id, state.Generation,
          new(connection.Id, DateTimeOffset.UtcNow, [new("stale", "Stale", Api: LlmApi.OpenAIResponses)])));

      ProviderModelCatalog catalog = await manager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);

      Assert.Equal(3, catalog.Models.Count);
      Assert.Equal(2, handler.Requests.Count);
      ProviderCredentials saved = store.Providers.Get(connection.Id).Credentials!;
      Assert.Equal(refreshedToken, saved.AccessToken);
      Assert.Equal(githubToken, saved.RefreshToken);
      Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expiresAt), saved.ExpiresAt);
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  [Fact]
  public async Task DirectGrantCredentialsFromTheOldRouteRequireSignInWithoutRequests() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = new ControlledProviderHandler((_, _) => throw new InvalidOperationException("Unexpected outbound request"));
    var manager = new ProviderManager(store, () => new Uri("http://localhost/auth/callback"), handler,
        copilotClientId: ClientId);
    try {
      ProviderConnection connection = await manager.AddGitHubCopilotAsync("GitHub Copilot");
      store.Providers.SetCredentials(connection.Id, 0, new(ClientId, "42", "octocat"),
          new(null, "legacy-github-grant", null, null, null, [],
              CopilotEndpoint: new Uri("https://api.business.githubcopilot.com/")));
      ProviderState saved = store.Providers.Get(connection.Id);
      Assert.Equal(ProviderRoute.GitHubCopilot, saved.Connection.Route);
      Assert.NotNull(saved.Credentials);
      Assert.False(GitHubCopilotAuthentication.HasGrant(saved.Credentials));
      Assert.True((await manager.GetCredentialStatusAsync(connection.Id)).RequiresSignIn);

      ProviderOperationException failure = await Assert.ThrowsAsync<ProviderOperationException>(() =>
          manager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout));
      Assert.Equal(ProviderFailure.AuthenticationRequired, failure.Failure);
      Assert.True((await manager.GetCredentialStatusAsync(connection.Id)).RequiresSignIn);
      Assert.Empty(handler.Requests);
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  private static void AssertCopilotHeaders(HttpRequestMessage request) {
    AssertCopilotIdentity(request);
    Assert.Equal(GitHubCopilotModels.ApiVersion, Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
  }

  private static void AssertCopilotIdentity(HttpRequestMessage request) {
    Assert.Equal(GitHubCopilotRequest.UserAgent, request.Headers.UserAgent.ToString());
    Assert.Equal("vscode/1.107.0", Assert.Single(request.Headers.GetValues("Editor-Version")));
    Assert.Equal("copilot-chat/0.35.0", Assert.Single(request.Headers.GetValues("Editor-Plugin-Version")));
    Assert.Equal("vscode-chat", Assert.Single(request.Headers.GetValues("Copilot-Integration-Id")));
  }

  private static HttpResponseMessage Reply(string body, string mediaType, HttpRequestMessage? request = null) => new(HttpStatusCode.OK) {
    RequestMessage = request,
    Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))) { Headers = { ContentType = new(mediaType) } }
  };

  private static async Task<List<ChatResponseUpdate>> ReadAsync(IChatClient client, string model) {
    var updates = new List<ChatResponseUpdate>();
    await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
        [new(ChatRole.User, "Say hello")], new() { ModelId = model }))
      updates.Add(update);
    return updates;
  }

  private const string AnthropicStream = """
      event: message_start
      data: {"type":"message_start","message":{"id":"msg_fixture","type":"message","role":"assistant","model":"fixture-messages","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":3,"output_tokens":1}}}

      event: content_block_start
      data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

      event: content_block_delta
      data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}

      event: content_block_stop
      data: {"type":"content_block_stop","index":0}

      event: message_delta
      data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":2}}

      event: message_stop
      data: {"type":"message_stop"}
      """ + "\n\n";
}
