using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Urd.Core.Harness.Mcp;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class RemoteMcpProbeTests {
  [Theory]
  [InlineData(RemoteMcpAuthentication.Automatic, null, null, null, null)]
  [InlineData(RemoteMcpAuthentication.Bearer, "bearer-secret", null, "Authorization", "Bearer bearer-secret")]
  [InlineData(RemoteMcpAuthentication.CustomHeader, "header-secret", "X-Api-Key", "X-Api-Key", "header-secret")]
  public async Task ListsToolsAndSendsConfiguredAuthentication(RemoteMcpAuthentication authentication, string? secret, string? headerName, string? expectedHeader, string? expectedValue) {
    await using var peer = new McpPeer();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var result = await RemoteMcpProbe.ProbeAsync(new RemoteMcpProbeOptions {
      Endpoint = peer.Endpoint,
      Authentication = authentication,
      Secret = secret,
      HeaderName = headerName
    }, deadline.Token);

    Assert.True(result.Success, result.Error);
    Assert.Equal("Test MCP server", result.DisplayName);
    var tool = Assert.Single(result.Tools);
    Assert.Equal("lookup", tool.Name);
    Assert.Equal("Look up", tool.Title);
    Assert.Equal("Find an item", tool.Description);
    Assert.Contains(peer.Requests, request => request.Method == "initialize");
    Assert.Contains(peer.Requests, request => request.Method == "tools/list");
    if (expectedHeader is not null) {
      Assert.All(peer.Requests.Where(request => request.Method is "initialize" or "tools/list"),
        request => Assert.Equal(expectedValue, request.Headers[expectedHeader]));
    }
  }

  [Fact]
  public async Task InvalidEndpointDoesNotExposeCredentialsInError() {
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var result = await RemoteMcpProbe.ProbeAsync(new RemoteMcpProbeOptions {
      Endpoint = new Uri("ftp://user:password@example.invalid/mcp?token=query-secret"),
      Authentication = RemoteMcpAuthentication.Bearer,
      Secret = "bearer-secret"
    }, deadline.Token);

    Assert.False(result.Success);
    Assert.False(string.IsNullOrWhiteSpace(result.Error));
    Assert.DoesNotContain("password", result.Error, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("query-secret", result.Error, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("bearer-secret", result.Error, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task DoesNotFollowRedirectWithCredentials() {
    await using var destination = new McpPeer();
    await using var redirect = new McpPeer(destination.Endpoint);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var result = await RemoteMcpProbe.ProbeAsync(new RemoteMcpProbeOptions {
      Endpoint = redirect.Endpoint,
      Authentication = RemoteMcpAuthentication.CustomHeader,
      HeaderName = "X-Api-Key",
      Secret = "redirect-secret"
    }, deadline.Token);

    Assert.False(result.Success);
    Assert.NotEmpty(redirect.Requests);
    Assert.Empty(destination.Requests);
    Assert.DoesNotContain("redirect-secret", result.Error ?? "", StringComparison.Ordinal);
  }

  private sealed record Request(string? Method, NameValueCollection Headers);

  private sealed class McpPeer : IAsyncDisposable {
    private readonly HttpListener listener = new();
    private readonly Task serving;
    private readonly Uri? redirect;

    public McpPeer(Uri? redirect = null) {
      this.redirect = redirect;
      var socket = new TcpListener(IPAddress.Loopback, 0);
      socket.Start();
      int port = ((IPEndPoint)socket.LocalEndpoint).Port;
      socket.Stop();
      Endpoint = new Uri($"http://127.0.0.1:{port}/mcp/");
      listener.Prefixes.Add(Endpoint.AbsoluteUri);
      listener.Start();
      serving = ServeAsync();
    }

    public Uri Endpoint { get; }
    public ConcurrentQueue<Request> Requests { get; } = new();

    private async Task ServeAsync() {
      while (listener.IsListening) {
        HttpListenerContext context;
        try {
          context = await listener.GetContextAsync();
        } catch (HttpListenerException) when (!listener.IsListening) {
          break;
        } catch (ObjectDisposedException) when (!listener.IsListening) {
          break;
        }
        await RespondAsync(context);
      }
    }

    private async Task RespondAsync(HttpListenerContext context) {
      string? method = null;
      JsonElement id = default;
      string protocolVersion = "2025-03-26";
      if (context.Request.HttpMethod == "POST") {
        using JsonDocument document = await JsonDocument.ParseAsync(context.Request.InputStream);
        JsonElement root = document.RootElement;
        method = root.GetProperty("method").GetString();
        if (root.TryGetProperty("id", out JsonElement requestId))
          id = requestId.Clone();
        if (method == "initialize" && root.GetProperty("params").TryGetProperty("protocolVersion", out JsonElement version))
          protocolVersion = version.GetString() ?? protocolVersion;
      }
      Requests.Enqueue(new Request(method, context.Request.Headers));
      if (redirect is not null) {
        context.Response.StatusCode = 302;
        context.Response.RedirectLocation = redirect.AbsoluteUri;
      } else if (context.Request.HttpMethod != "POST") {
        context.Response.StatusCode = 405;
      } else if (id.ValueKind == JsonValueKind.Undefined) {
        context.Response.StatusCode = 202;
      } else if (method == "server/discover") {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } });
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
      } else {
        object result = method switch {
          "initialize" => new { protocolVersion, capabilities = new { tools = new { } }, serverInfo = new { name = "test-server", title = "Test MCP server", version = "1.0" } },
          "tools/list" => new { tools = new[] { new { name = "lookup", title = "Look up", description = "Find an item", inputSchema = new { type = "object" } } } },
          _ => throw new InvalidOperationException($"Unexpected MCP method: {method}")
        };
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, result });
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
      }
      context.Response.Close();
    }

    public async ValueTask DisposeAsync() {
      listener.Stop();
      listener.Close();
      await serving;
    }
  }
}
