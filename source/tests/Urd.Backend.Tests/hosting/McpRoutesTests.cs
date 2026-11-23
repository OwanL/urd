using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Server;
using Microsoft.AspNetCore.WebUtilities;
using Urd.Backend.Tests.Support;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;
using static Urd.Backend.Tests.Support.ScriptedResponses;

namespace Urd.Backend.Tests.Hosting;

public sealed class McpRoutesTests {
  private const string Secret = "synthetic-mcp-secret-never-persist-plaintext";

  [Fact]
  public async Task ConnectPersistsReconnectsAndRemovesWithoutExposingCredential() {
    using var database = new TemporaryDatabase("urd-mcp-routes");
    await using var peer = new McpPeer();
    Guid serverId;
    await using (var server = new RunningServer(RunningServer.OptionsFor(database))) {
      await server.WaitForReadyAsync();
      Assert.Empty((await SendAsync(server.Client, HttpMethod.Get, "/api/mcp-servers")).EnumerateArray());
      JsonElement started = await SendAsync(server.Client, HttpMethod.Post, "/api/mcp-servers", new {
        url = peer.Endpoint.AbsoluteUri, authentication = "bearerToken", headerName = (string?)null, credential = Secret
      });
      await ConnectedAsync(server.Client, started.GetProperty("id").GetGuid());
      JsonElement saved = Assert.Single((await SendAsync(server.Client, HttpMethod.Get, "/api/mcp-servers")).EnumerateArray());
      serverId = saved.GetProperty("id").GetGuid();
      Assert.Equal("Test MCP server", saved.GetProperty("name").GetString());
      Assert.Equal(peer.Endpoint.AbsoluteUri, saved.GetProperty("url").GetString());
      Assert.Equal("bearerToken", saved.GetProperty("authentication").GetString());
      Assert.Equal("lookup", Assert.Single(saved.GetProperty("tools").EnumerateArray()).GetString());
    }

    string raw = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(database.Path));
    Assert.DoesNotContain(Secret, raw, StringComparison.Ordinal);
    await using (var restarted = new RunningServer(RunningServer.OptionsFor(database))) {
      await restarted.WaitForReadyAsync();
      JsonElement saved = Assert.Single((await SendAsync(restarted.Client, HttpMethod.Get, "/api/mcp-servers")).EnumerateArray());
      Assert.Equal(serverId, saved.GetProperty("id").GetGuid());
      JsonElement reconnect = await SendAsync(restarted.Client, HttpMethod.Post, $"/api/mcp-servers/{serverId}/reconnect", new { });
      await ConnectedAsync(restarted.Client, reconnect.GetProperty("id").GetGuid());
      await SendAsync(restarted.Client, HttpMethod.Delete, $"/api/mcp-servers/{serverId}");
      Assert.Empty((await SendAsync(restarted.Client, HttpMethod.Get, "/api/mcp-servers")).EnumerateArray());
    }
    Assert.True(peer.Requests.Count(request => request.Method == "initialize") >= 2);
    Assert.True(peer.Requests.Count(request => request.Method == "tools/list") >= 2);
    Assert.All(peer.Requests.Where(request => request.Method is "initialize" or "tools/list"),
        request => Assert.Equal($"Bearer {Secret}", request.Authorization));
  }

  [Fact]
  public async Task AutomaticAuthenticationCompletesOAuthWithoutExposingTokens() {
    using var database = new TemporaryDatabase("urd-mcp-oauth");
    await using var peer = new McpPeer(oauth: true);
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    JsonElement started = await SendAsync(server.Client, HttpMethod.Post, "/api/mcp-servers", new {
      url = peer.Endpoint.AbsoluteUri, authentication = "automatic", headerName = (string?)null, credential = (string?)null
    });
    Guid operationId = started.GetProperty("id").GetGuid();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    JsonElement operation;
    do {
      operation = await SendAsync(server.Client, HttpMethod.Get, $"/api/mcp-operations/{operationId}");
      Assert.NotEqual("failed", operation.GetProperty("state").GetString());
      if (operation.GetProperty("state").GetString() == "signIn") break;
      await Task.Delay(25, deadline.Token);
    } while (true);
    var authorization = new Uri(operation.GetProperty("authorizationUrl").GetString()!);
    var query = QueryHelpers.ParseQuery(authorization.Query);
    string state = Assert.Single(query["state"])!;
    string redirect = Assert.Single(query["redirect_uri"])!;
    string challenge = Assert.Single(query["code_challenge"])!;
    Assert.Equal("S256", Assert.Single(query["code_challenge_method"]));
    Assert.Equal(peer.AuthorizationEndpoint.GetLeftPart(UriPartial.Path), authorization.GetLeftPart(UriPartial.Path));
    using (var wrong = await server.Client.GetAsync(QueryHelpers.AddQueryString(redirect, new Dictionary<string, string?> {
      ["state"] = state + "wrong", ["code"] = "test-code"
    }))) {
      Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
      Assert.Equal("no-store", wrong.Headers.CacheControl?.ToString());
    }
    Assert.DoesNotContain("/token", peer.Paths);
    Assert.Equal("signIn", (await SendAsync(server.Client, HttpMethod.Get, $"/api/mcp-operations/{operationId}"))
        .GetProperty("state").GetString());
    using (var correct = await server.Client.GetAsync(QueryHelpers.AddQueryString(redirect, new Dictionary<string, string?> {
      ["state"] = state, ["code"] = "test-code"
    }))) {
      Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
      Assert.DoesNotContain(Secret, await correct.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
    await ConnectedAsync(server.Client, operationId);
    JsonElement saved = Assert.Single((await SendAsync(server.Client, HttpMethod.Get, "/api/mcp-servers")).EnumerateArray());
    Assert.Equal("lookup", Assert.Single(saved.GetProperty("tools").EnumerateArray()).GetString());
    Assert.Contains(peer.Requests, request => request.Method == "tools/list" && request.Authorization == $"Bearer {Secret}");
    Assert.NotNull(peer.Verifier);
    Assert.Equal(challenge, WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(peer.Verifier))));
    Assert.Contains("/.well-known/oauth-protected-resource/mcp", peer.Paths);
    Assert.Contains("/.well-known/oauth-authorization-server", peer.Paths);
    Assert.Contains("/register", peer.Paths);
    Assert.Contains("/token", peer.Paths);
    await server.DisposeAsync();
    Assert.DoesNotContain(Secret, Encoding.Latin1.GetString(await File.ReadAllBytesAsync(database.Path)), StringComparison.Ordinal);
  }

  [Fact]
  public async Task RotatedRefreshTokenSurvivesFailedToolsList() {
    using var database = new TemporaryDatabase("urd-mcp-oauth-rotation");
    await using var peer = new McpPeer(oauth: true, rotateTokens: true);
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    JsonElement started = await SendAsync(server.Client, HttpMethod.Post, "/api/mcp-servers", new {
      url = peer.Endpoint.AbsoluteUri, authentication = "automatic", headerName = (string?)null, credential = (string?)null
    });
    Guid operationId = started.GetProperty("id").GetGuid();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    JsonElement operation;
    while (true) {
      operation = await SendAsync(server.Client, HttpMethod.Get, $"/api/mcp-operations/{operationId}");
      string? state = operation.GetProperty("state").GetString();
      Assert.NotEqual("failed", state);
      if (state == "signIn") break;
      await Task.Delay(25, deadline.Token);
    }
    var query = QueryHelpers.ParseQuery(new Uri(operation.GetProperty("authorizationUrl").GetString()!).Query);
    using (var callback = await server.Client.GetAsync(QueryHelpers.AddQueryString(Assert.Single(query["redirect_uri"])!,
        new Dictionary<string, string?> { ["state"] = Assert.Single(query["state"]), ["code"] = "test-code" })))
      Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
    await ConnectedAsync(server.Client, operationId);
    Guid serverId = Assert.Single((await SendAsync(server.Client, HttpMethod.Get, "/api/mcp-servers")).EnumerateArray())
        .GetProperty("id").GetGuid();

    peer.RejectAccessToken = true;
    peer.FailToolsList = true;
    JsonElement reconnect = await SendAsync(server.Client, HttpMethod.Post, $"/api/mcp-servers/{serverId}/reconnect", new { });
    await WaitForReconnectAsync(server.Client, reconnect.GetProperty("id").GetGuid(), "failed");
    Assert.Equal(new[] { "refresh-0" }, peer.RefreshTokens.ToArray());
    Assert.Contains(peer.Requests, request => request.Method == "tools/list" && request.Authorization == "Bearer access-1");

    peer.FailToolsList = false;
    peer.RejectAccessToken = true;
    reconnect = await SendAsync(server.Client, HttpMethod.Post, $"/api/mcp-servers/{serverId}/reconnect", new { });
    await WaitForReconnectAsync(server.Client, reconnect.GetProperty("id").GetGuid(), "connected");
    Assert.Equal(new[] { "refresh-0", "refresh-1" }, peer.RefreshTokens.ToArray());
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ExistingSessionUsesConnectedToolsAndStopsAdvertisingThemAfterRemoval(bool oauth) {
    using var database = new TemporaryDatabase("urd-mcp-chat");
    await using var peer = new McpPeer(oauth: oauth, rotateTokens: oauth);
    var requests = new ConcurrentQueue<ScriptedRequest>();
    int calls = 0;
    var model = new ScriptedChatClient((request, token) => {
      requests.Enqueue(request);
      if (Interlocked.Increment(ref calls) != 2)
        return Complete(token);
      var tool = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(request.Options!.Tools!,
          candidate => candidate.Name.StartsWith("mcp_lookup_", StringComparison.Ordinal)));
      return ToolBatch(token, new FunctionCallContent("remote-call", tool.Name,
          new Dictionary<string, object?> { ["query"] = "northern lights", ["background"] = true }));
    });
    await using var server = new RunningServer(RunningServer.OptionsForIsolatedWorkspace(database),
        createClient: () => new PrepassTolerantChatClient(model));
    await server.WaitForReadyAsync();
    Guid sessionId;
    using (HttpResponseMessage created = await RequestAsync(server.Client, HttpMethod.Post, "/api/sessions", JsonBody("{}"))) {
      Assert.Equal(HttpStatusCode.Created, created.StatusCode);
      using JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
      sessionId = body.RootElement.GetProperty("sessionId").GetGuid();
    }
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) {
      Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    long revision = 0;
    async Task SendMessageAsync(string text) {
      var submission = new UserMessageSubmission(Guid.NewGuid(), revision, text, TestLlmSelection.Default);
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Post,
          $"/api/sessions/{sessionId}/message-submissions", JsonContent.Create(submission, options: json));
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      UserMessageReceipt receipt = Assert.IsType<UserMessageReceipt>(await response.Content.ReadFromJsonAsync<UserMessageReceipt>(json));
      SessionSnapshot settled = await SettledAsync(receipt.ExecutionId!.Value, async () => {
        using HttpResponseMessage snapshot = await RequestAsync(server.Client, HttpMethod.Get, $"/api/sessions/{sessionId}/snapshot");
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        return Assert.IsType<SessionSnapshot>(await snapshot.Content.ReadFromJsonAsync<SessionSnapshot>(json));
      });
      revision = settled.HistoryRevision;
    }

    await SendMessageAsync("before connecting");
    JsonElement started = await SendAsync(server.Client, HttpMethod.Post, "/api/mcp-servers", new {
      url = peer.Endpoint.AbsoluteUri, authentication = oauth ? "automatic" : "bearerToken",
      headerName = (string?)null, credential = oauth ? null : Secret
    });
    Guid operationId = started.GetProperty("id").GetGuid();
    if (oauth) {
      using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
      while (true) {
        JsonElement operation = await SendAsync(server.Client, HttpMethod.Get, $"/api/mcp-operations/{operationId}");
        string? state = operation.GetProperty("state").GetString();
        Assert.NotEqual("failed", state);
        if (state == "signIn") {
          var authorization = new Uri(operation.GetProperty("authorizationUrl").GetString()!);
          var parameters = QueryHelpers.ParseQuery(authorization.Query);
          using var response = await server.Client.GetAsync(QueryHelpers.AddQueryString(Assert.Single(parameters["redirect_uri"])!,
              new Dictionary<string, string?> { ["state"] = Assert.Single(parameters["state"]), ["code"] = "test-code" }));
          Assert.Equal(HttpStatusCode.OK, response.StatusCode);
          break;
        }
        await Task.Delay(25, deadline.Token);
      }
    }
    await ConnectedAsync(server.Client, operationId);
    peer.RejectAccessToken = oauth;
    Guid serverId = Assert.Single((await SendAsync(server.Client, HttpMethod.Get, "/api/mcp-servers")).EnumerateArray())
        .GetProperty("id").GetGuid();
    await SendMessageAsync("look this up");
    await SendAsync(server.Client, HttpMethod.Delete, $"/api/mcp-servers/{serverId}");
    await SendMessageAsync("after removal");

    ScriptedRequest[] captured = requests.ToArray();
    Assert.Equal(4, captured.Length);
    Assert.DoesNotContain(captured[0].Options!.Tools!, tool => tool.Name.StartsWith("mcp_lookup_", StringComparison.Ordinal));
    var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(captured[1].Options!.Tools!,
        tool => tool.Name.StartsWith("mcp_lookup_", StringComparison.Ordinal)));
    Assert.Equal("string", declaration.JsonSchema.GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
    Assert.Equal("Remote lookup mode", declaration.JsonSchema.GetProperty("properties").GetProperty("background").GetProperty("description").GetString());
    Assert.Contains(declaration.JsonSchema.GetProperty("required").EnumerateArray(), item => item.GetString() == "query");
    Assert.Contains(captured[2].Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
        result => result.CallId == "remote-call" && JsonSerializer.Serialize(result.Result).Contains("aurora result", StringComparison.Ordinal));
    Assert.DoesNotContain(captured[3].Options!.Tools!, tool => tool.Name.StartsWith("mcp_lookup_", StringComparison.Ordinal));
    Request call = Assert.Single(peer.Requests, request => request.Method == "tools/call");
    Assert.Equal(oauth ? "Bearer access-1" : $"Bearer {Secret}", call.Authorization);
    if (oauth)
      Assert.Equal(new[] { "refresh-0" }, peer.RefreshTokens.ToArray());
    Assert.Equal("lookup", call.Parameters.GetProperty("name").GetString());
    Assert.Equal("northern lights", call.Parameters.GetProperty("arguments").GetProperty("query").GetString());
    Assert.True(call.Parameters.GetProperty("arguments").GetProperty("background").GetBoolean());
  }

  [Fact]
  public async Task LanPauseAndRemovalBlockCallsFromAnAlreadyDiscoveredCatalog() {
    using var database = new TemporaryDatabase("urd-mcp-agent-admission");
    using var store = UrdStore.Open(database.Path);
    await using var peer = new McpPeer();
    Guid id = Guid.NewGuid();
    Assert.True(store.McpServers.Save(new(id, "Test MCP server", peer.Endpoint.AbsoluteUri, "automatic", ["lookup"], null, null, null), false));
    var connections = new McpConnections(store, () => new Uri("http://127.0.0.1:4444/mcp/auth/callback"), Path.GetDirectoryName(database.Path)!);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var arguments = JsonDocument.Parse("{\"query\":\"weather\"}");
    var tool = Assert.Single(await connections.DiscoverAsync(deadline.Token));
    using (connections.PauseAgentToolsForLan()) {
      Assert.Empty(await connections.DiscoverAsync(deadline.Token));
      Assert.True((await connections.CallAsync(tool.ServerId, tool.Tool.Name, arguments.RootElement, deadline.Token)).IsError);
    }
    Assert.False((await connections.CallAsync(tool.ServerId, tool.Tool.Name, arguments.RootElement, deadline.Token)).IsError);
    Assert.True(connections.Remove(id));
    Assert.True((await connections.CallAsync(tool.ServerId, tool.Tool.Name, arguments.RootElement, deadline.Token)).IsError);
    Assert.Single(peer.Requests, request => request.Method == "tools/call");
  }

  private static async Task WaitForReconnectAsync(HttpClient client, Guid operationId, string expected) {
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (true) {
      JsonElement operation = await SendAsync(client, HttpMethod.Get, $"/api/mcp-operations/{operationId}");
      string? state = operation.GetProperty("state").GetString();
      Assert.NotEqual("signIn", state);
      if (state is "connected" or "failed") {
        Assert.Equal(expected, state);
        return;
      }
      await Task.Delay(25, deadline.Token);
    }
  }

  [Fact]
  public async Task ShutdownCancelsPendingOAuth() {
    using var database = new TemporaryDatabase("urd-mcp-oauth-shutdown");
    await using var peer = new McpPeer(oauth: true);
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    JsonElement started = await SendAsync(server.Client, HttpMethod.Post, "/api/mcp-servers", new {
      url = peer.Endpoint.AbsoluteUri, authentication = "automatic", headerName = (string?)null, credential = (string?)null
    });
    Guid id = started.GetProperty("id").GetGuid();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (true) {
      JsonElement operation = await SendAsync(server.Client, HttpMethod.Get, $"/api/mcp-operations/{id}");
      string? state = operation.GetProperty("state").GetString();
      Assert.NotEqual("failed", state);
      if (state == "signIn") break;
      await Task.Delay(25, deadline.Token);
    }
    server.Cancel();
    await server.WaitForCompletionAsync();
    Assert.DoesNotContain("/token", peer.Paths);
  }

  private static async Task ConnectedAsync(HttpClient client, Guid operationId) {
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (true) {
      JsonElement operation = await SendAsync(client, HttpMethod.Get, $"/api/mcp-operations/{operationId}");
      string? state = operation.GetProperty("state").GetString();
      if (state == "connected") return;
      Assert.NotEqual("failed", state);
      await Task.Delay(25, deadline.Token);
    }
  }

  private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null) {
    using var request = new HttpRequestMessage(method, path);
    request.Headers.Add("X-Urd-Client", "browser");
    if (body is not null) request.Content = JsonContent.Create(body);
    using HttpResponseMessage response = await client.SendAsync(request);
    string content = await response.Content.ReadAsStringAsync();
    Assert.DoesNotContain(Secret, content, StringComparison.Ordinal);
    Assert.DoesNotContain(Secret, response.Headers.ToString(), StringComparison.Ordinal);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertPrivateResponse(response);
    using JsonDocument document = JsonDocument.Parse(content);
    return document.RootElement.Clone();
  }

  private sealed record Request(string? Method, string? Authorization, JsonElement Parameters);

  private sealed class McpPeer : IAsyncDisposable {
    private readonly HttpListener listener = new();
    private readonly Task serving;
    private readonly bool oauth;
    private readonly bool rotateTokens;
    private int tokenGeneration;
    private volatile bool stopping;
    internal bool RejectAccessToken { get; set; }
    internal bool FailToolsList { get; set; }
    internal ConcurrentQueue<string> RefreshTokens { get; } = new();
    internal ConcurrentQueue<Request> Requests { get; } = new();
    internal ConcurrentQueue<string> Paths { get; } = new();
    internal Uri Endpoint { get; }
    internal Uri AuthorizationEndpoint => new(Endpoint, "/authorize");
    internal string? Verifier { get; private set; }

    internal McpPeer(bool oauth = false, bool rotateTokens = false) {
      this.oauth = oauth;
      this.rotateTokens = rotateTokens;
      var socket = new TcpListener(IPAddress.Loopback, 0);
      socket.Start();
      int port = ((IPEndPoint)socket.LocalEndpoint).Port;
      socket.Stop();
      Endpoint = new Uri($"http://127.0.0.1:{port}/mcp/");
      listener.Prefixes.Add(Endpoint.GetLeftPart(UriPartial.Authority) + "/");
      listener.Start();
      serving = ServeAsync();
    }

    private async Task ServeAsync() {
      while (!stopping) {
        HttpListenerContext context;
        try {
          context = await listener.GetContextAsync();
        } catch (HttpListenerException) when (stopping) {
          break;
        } catch (ObjectDisposedException) when (stopping) {
          break;
        }
        await RespondAsync(context);
      }
    }

    private async Task RespondAsync(HttpListenerContext context) {
      string path = context.Request.Url!.AbsolutePath;
      Paths.Enqueue(path);
      string origin = Endpoint.GetLeftPart(UriPartial.Authority);
      if (oauth && path != "/mcp/") {
        object? response = path switch {
          "/.well-known/oauth-protected-resource/mcp/" or "/.well-known/oauth-protected-resource/mcp" or "/.well-known/oauth-protected-resource" =>
            new { resource = Endpoint.AbsoluteUri, authorization_servers = new[] { origin } },
          "/.well-known/oauth-authorization-server" or "/.well-known/openid-configuration" => new {
            issuer = origin, authorization_endpoint = origin + "/authorize", token_endpoint = origin + "/token",
            registration_endpoint = origin + "/register", response_types_supported = new[] { "code" },
            code_challenge_methods_supported = new[] { "S256" }, token_endpoint_auth_methods_supported = new[] { "none" }
          },
          "/register" => new { client_id = "test-client", token_endpoint_auth_method = "none" },
          "/token" => null,
          _ => null
        };
        if (path == "/token") {
          using var reader = new StreamReader(context.Request.InputStream);
          var form = QueryHelpers.ParseQuery(await reader.ReadToEndAsync());
          if (rotateTokens && form["grant_type"] == "refresh_token") {
            string refresh = Assert.Single(form["refresh_token"])!;
            RefreshTokens.Enqueue(refresh);
            if (refresh != $"refresh-{tokenGeneration}") {
              context.Response.StatusCode = 400;
              context.Response.Close();
              return;
            }
            tokenGeneration++;
            RejectAccessToken = false;
          } else {
            Verifier = Assert.Single(form["code_verifier"]);
            Assert.Equal("test-code", Assert.Single(form["code"]));
          }
          response = rotateTokens
              ? new { access_token = $"access-{tokenGeneration}", token_type = "Bearer", refresh_token = $"refresh-{tokenGeneration}" }
              : new { access_token = Secret, token_type = "Bearer" };
        }
        if (response is null) context.Response.StatusCode = 404;
        else {
          byte[] json = JsonSerializer.SerializeToUtf8Bytes(response);
          context.Response.ContentType = "application/json";
          context.Response.ContentLength64 = json.Length;
          await context.Response.OutputStream.WriteAsync(json);
        }
        context.Response.Close();
        return;
      }
      string accessToken = rotateTokens ? $"access-{tokenGeneration}" : Secret;
      if (oauth && (RejectAccessToken || context.Request.Headers["Authorization"] != $"Bearer {accessToken}")) {
        context.Response.StatusCode = 401;
        context.Response.Headers.Add("WWW-Authenticate", $"Bearer resource_metadata=\"{origin}/.well-known/oauth-protected-resource/mcp\"");
        context.Response.Close();
        return;
      }
      string? method = null;
      JsonElement id = default;
      JsonElement parameters = default;
      string protocolVersion = "2025-03-26";
      if (context.Request.HttpMethod == "POST") {
        using JsonDocument document = await JsonDocument.ParseAsync(context.Request.InputStream);
        JsonElement root = document.RootElement;
        method = root.GetProperty("method").GetString();
        if (root.TryGetProperty("params", out JsonElement requestParameters)) parameters = requestParameters.Clone();
        if (root.TryGetProperty("id", out JsonElement requestId)) id = requestId.Clone();
        if (method == "initialize" && root.GetProperty("params").TryGetProperty("protocolVersion", out JsonElement version))
          protocolVersion = version.GetString() ?? protocolVersion;
      }
      Requests.Enqueue(new Request(method, context.Request.Headers["Authorization"], parameters));
      if (context.Request.HttpMethod != "POST") {
        context.Response.StatusCode = 405;
      } else if (id.ValueKind == JsonValueKind.Undefined) {
        context.Response.StatusCode = 202;
      } else {
        object response = method switch {
          "server/discover" => new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } },
          "initialize" => new { jsonrpc = "2.0", id, result = new { protocolVersion, capabilities = new { tools = new { } }, serverInfo = new { name = "test-server", title = "Test MCP server", version = "1.0" } } },
          "tools/list" when FailToolsList => new { jsonrpc = "2.0", id, error = new { code = -32603, message = "Probe failed" } },
          "tools/list" => new { jsonrpc = "2.0", id, result = new { tools = new[] { new {
            name = "lookup", inputSchema = new { type = "object", properties = new {
              query = new { type = "string" }, background = new { type = "boolean", description = "Remote lookup mode" }
            }, required = new[] { "query" } }
          } } } },
          "tools/call" => new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "text", text = "aurora result" } } } },
          _ => throw new InvalidOperationException($"Unexpected MCP method: {method}")
        };
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(response);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
      }
      context.Response.Close();
    }

    public async ValueTask DisposeAsync() {
      // Stop can abort a pending accept before IsListening changes.
      stopping = true;
      listener.Stop();
      listener.Close();
      await serving;
    }
  }
}
