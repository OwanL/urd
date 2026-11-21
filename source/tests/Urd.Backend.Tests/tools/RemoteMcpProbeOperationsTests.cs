using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Urd.Core.Harness.Mcp;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class RemoteMcpProbeOperationsTests {
  [Fact]
  public async Task DiscoversPagesAndPreservesSchemas() {
    await using var peer = new Peer((method, parameters) => {
      if (method != "tools/list")
        return null;
      if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("cursor", out var cursor) && cursor.ValueKind == JsonValueKind.String)
        return new { tools = new[] { new { name = "second", inputSchema = new { type = "object" } } } };
      return new { tools = new[] { new { name = "first", inputSchema = new { type = "object", required = new[] { "query" } } } }, nextCursor = "next" };
    });
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

    var tools = await RemoteMcpProbe.DiscoverToolsAsync(new() { Endpoint = peer.Endpoint }, deadline.Token);

    Assert.Equal(new[] { "first", "second" }, tools.Select(tool => tool.Name));
    Assert.Equal("query", tools[0].InputSchema.GetProperty("required")[0].GetString());
    Assert.Equal("next", peer.Requests.Last(request => request.Method == "tools/list").Parameters.GetProperty("cursor").GetString());
  }

  [Fact]
  public async Task DiscoveryRejectsRepeatedCursor() {
    await using var peer = new Peer((method, _) => method == "tools/list"
      ? new { tools = Array.Empty<object>(), nextCursor = "repeat" } : null);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
      RemoteMcpProbe.DiscoverToolsAsync(new() { Endpoint = peer.Endpoint }, deadline.Token));

    Assert.Equal("Unable to discover MCP tools.", error.Message);
    Assert.Equal(2, peer.Requests.Count(request => request.Method == "tools/list"));
  }

  [Fact]
  public async Task CallForwardsArgumentsAndCombinesOutputAndError() {
    await using var peer = new Peer((method, _) => method == "tools/call"
      ? new { content = new object[] { new { type = "text", text = "hello" }, new { type = "image", data = "aGVsbG8=", mimeType = "image/png" } }, structuredContent = new { count = 3 }, isError = true } : null);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var arguments = JsonDocument.Parse("{\"query\":\"example\",\"limit\":3}");

    var result = await RemoteMcpProbe.CallToolAsync(new() { Endpoint = peer.Endpoint }, "lookup", arguments.RootElement, deadline.Token);

    Assert.True(result.IsError);
    Assert.Equal("hello\n[Unsupported non-text content]\n{\"count\":3}", result.Text);
    var call = Assert.Single(peer.Requests, request => request.Method == "tools/call");
    Assert.Equal("lookup", call.Parameters.GetProperty("name").GetString());
    Assert.Equal("example", call.Parameters.GetProperty("arguments").GetProperty("query").GetString());
    Assert.Equal(3, call.Parameters.GetProperty("arguments").GetProperty("limit").GetInt32());
  }

  [Fact]
  public async Task CallTruncatesLargeOutput() {
    await using var peer = new Peer((method, _) => method == "tools/call"
      ? new { content = new[] { new { type = "text", text = new string('x', 40000) } } } : null);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var arguments = JsonDocument.Parse("{}");

    var result = await RemoteMcpProbe.CallToolAsync(new() { Endpoint = peer.Endpoint }, "lookup", arguments.RootElement, deadline.Token);

    Assert.False(result.IsError);
    Assert.Equal(32768, result.Text.Length);
    Assert.EndsWith("[Output truncated]", result.Text);
  }

  [Theory]
  [InlineData("", "{}")]
  [InlineData("lookup", "[]")]
  public async Task InvalidCallDoesNotContactServer(string name, string json) {
    await using var peer = new Peer((_, _) => null);
    using var arguments = JsonDocument.Parse(json);

    var result = await RemoteMcpProbe.CallToolAsync(new() { Endpoint = peer.Endpoint }, name, arguments.RootElement, CancellationToken.None);

    Assert.True(result.IsError);
    Assert.Equal("Invalid MCP tool call.", result.Text);
    Assert.Empty(peer.Requests);
  }

  [Fact]
  public async Task FailureAfterDispatchReportsUnknownOutcomeWithoutReplayingCall() {
    await using var peer = new Peer((_, _) => null, failCall: true);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var arguments = JsonDocument.Parse("{}");

    var result = await RemoteMcpProbe.CallToolAsync(new() { Endpoint = peer.Endpoint }, "lookup", arguments.RootElement, deadline.Token);

    Assert.True(result.IsError);
    Assert.Equal("MCP tool call failed; its outcome is unknown.", result.Text);
    Assert.Single(peer.Requests, request => request.Method == "tools/call");
  }

  private sealed record Request(string? Method, JsonElement Parameters);

  private sealed class Peer : IAsyncDisposable {
    private readonly HttpListener listener = new();
    private readonly Task serving;
    private readonly Func<string?, JsonElement, object?> respond;
    private readonly bool failCall;
    private volatile bool stopping;

    public Peer(Func<string?, JsonElement, object?> respond, bool failCall = false) {
      this.respond = respond;
      this.failCall = failCall;
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
      string? method = null;
      JsonElement id = default;
      JsonElement parameters = default;
      string protocolVersion = "2025-03-26";
      if (context.Request.HttpMethod == "POST") {
        using var document = await JsonDocument.ParseAsync(context.Request.InputStream);
        var root = document.RootElement;
        method = root.GetProperty("method").GetString();
        if (root.TryGetProperty("id", out var requestId))
          id = requestId.Clone();
        if (root.TryGetProperty("params", out var requestParameters))
          parameters = requestParameters.Clone();
        if (method == "initialize" && parameters.TryGetProperty("protocolVersion", out var version))
          protocolVersion = version.GetString() ?? protocolVersion;
      }
      Requests.Enqueue(new Request(method, parameters));
      if (context.Request.HttpMethod != "POST") {
        context.Response.StatusCode = 405;
      } else if (id.ValueKind == JsonValueKind.Undefined) {
        context.Response.StatusCode = 202;
      } else if (method == "tools/call" && failCall) {
        context.Response.StatusCode = 500;
      } else if (method == "server/discover") {
        await WriteAsync(context, new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } });
      } else {
        object result = respond(method, parameters) ?? (method switch {
          "initialize" => new { protocolVersion, capabilities = new { tools = new { } }, serverInfo = new { name = "test-server", version = "1.0" } },
          _ => throw new InvalidOperationException($"Unexpected MCP method: {method}")
        });
        await WriteAsync(context, new { jsonrpc = "2.0", id, result });
      }
      context.Response.Close();
    }

    private static async Task WriteAsync(HttpListenerContext context, object value) {
      byte[] body = JsonSerializer.SerializeToUtf8Bytes(value);
      context.Response.ContentType = "application/json";
      context.Response.ContentLength64 = body.Length;
      await context.Response.OutputStream.WriteAsync(body);
    }

    public async ValueTask DisposeAsync() {
      stopping = true;
      listener.Stop();
      listener.Close();
      await serving;
    }
  }
}
