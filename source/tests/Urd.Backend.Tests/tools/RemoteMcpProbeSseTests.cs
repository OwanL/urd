using System.Net;
using System.Net.Sockets;
using System.Text;
using Urd.Core.Harness.Mcp;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class RemoteMcpProbeSseTests {
  [Theory]
  [InlineData(RemoteMcpAuthentication.Automatic, null, null)]
  [InlineData(RemoteMcpAuthentication.Bearer, "bearer-secret", null)]
  [InlineData(RemoteMcpAuthentication.CustomHeader, "header-secret", "X-Api-Key")]
  public async Task LegacySseCannotSendRequestsToAnotherOrigin(RemoteMcpAuthentication authentication, string? secret, string? headerName) {
    await using var destination = new Listener();
    await using var server = new Listener();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var peer = ServeSseAsync(server, destination.Endpoint, deadline.Token);
    var destinationRequest = destination.GetContextAsync();

    var result = await RemoteMcpProbe.ProbeAsync(new RemoteMcpProbeOptions {
      Endpoint = server.Endpoint,
      Authentication = authentication,
      Secret = secret,
      HeaderName = headerName
    }, deadline.Token);

    Assert.False(result.Success);
    Assert.True(await peer);
    Assert.False(destinationRequest.IsCompleted, "The cross-origin destination received a request.");
  }

  [Fact]
  public async Task LegacySseCanSendRequestsToSameOrigin() {
    await using var server = new Listener();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var peer = ServeSseAsync(server, server.Endpoint, deadline.Token);
    var probe = RemoteMcpProbe.ProbeAsync(new RemoteMcpProbeOptions { Endpoint = server.Endpoint }, deadline.Token);

    Assert.True(await peer);
    var request = await server.GetContextAsync().WaitAsync(deadline.Token);
    Assert.Equal("POST", request.Request.HttpMethod);
    request.Response.StatusCode = 500;
    request.Response.Close();
    Assert.False((await probe).Success);
  }

  private static async Task<bool> ServeSseAsync(Listener server, Uri destination, CancellationToken cancellationToken) {
    while (true) {
      var context = await server.GetContextAsync().WaitAsync(cancellationToken);
      if (context.Request.HttpMethod != "GET") {
        context.Response.StatusCode = 405;
        context.Response.Close();
        continue;
      }

      context.Response.ContentType = "text/event-stream";
      byte[] body = Encoding.UTF8.GetBytes($"event: endpoint\ndata: {destination.AbsoluteUri}\n\n");
      await context.Response.OutputStream.WriteAsync(body, cancellationToken);
      await context.Response.OutputStream.FlushAsync(cancellationToken);
      return true;
    }
  }

  private sealed class Listener : IAsyncDisposable {
    private readonly HttpListener listener = new();

    public Listener() {
      var socket = new TcpListener(IPAddress.Loopback, 0);
      socket.Start();
      int port = ((IPEndPoint)socket.LocalEndpoint).Port;
      socket.Stop();
      Endpoint = new Uri($"http://127.0.0.1:{port}/mcp/");
      listener.Prefixes.Add(Endpoint.AbsoluteUri);
      listener.Start();
    }

    public Uri Endpoint { get; }
    public Task<HttpListenerContext> GetContextAsync() => listener.GetContextAsync();

    public ValueTask DisposeAsync() {
      listener.Close();
      return ValueTask.CompletedTask;
    }
  }
}
