using System.Net;
using System.Net.Sockets;
using System.Text;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class PlaywrightNetworkTests {
  [Fact]
  public async Task NavigationNominatesOnlyTheExactLoopbackOrigin() {
    await using var network = new PlaywrightNetwork(() => 0);
    using var peer = new LoopbackPeer();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

    Assert.True(network.TryAllowNavigation(new Uri($"http://localhost:{peer.Port}/page")));
    Assert.True(network.TryAllowNavigation(new Uri("https://8.8.8.8/")));
    Assert.False(network.TryAllowNavigation(new Uri("http://10.0.0.1/")));
    Assert.False(network.TryAllowNavigation(new Uri("http://127.0.0.2/")));
    Assert.False(network.TryAllowNavigation(new Uri("http://user:password@localhost/")));

    string alias = await SendAsync(network, $"GET http://127.0.0.1:{peer.Port}/ HTTP/1.1\r\nHost: localhost:{peer.Port}\r\n\r\n", deadline.Token);
    Assert.StartsWith("HTTP/1.1 403", alias);
    Assert.False(peer.Pending);

    string otherScheme = await SendAsync(network, $"CONNECT localhost:{peer.Port} HTTP/1.1\r\nHost: localhost:{peer.Port}\r\n\r\n", deadline.Token);
    Assert.StartsWith("HTTP/1.1 403", otherScheme);
    Assert.False(peer.Pending);
  }

  [Fact]
  public async Task BlockedPortIsCheckedAgainAfterNomination() {
    int blockedPort = 0;
    await using var network = new PlaywrightNetwork(() => Volatile.Read(ref blockedPort));
    using var peer = new LoopbackPeer();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Assert.True(network.TryAllowNavigation(new Uri($"http://127.0.0.1:{peer.Port}/")));
    Assert.True(network.TryAllowNavigation(new Uri($"https://127.0.0.1:{peer.Port}/")));
    Volatile.Write(ref blockedPort, peer.Port);
    Assert.False(network.TryAllowNavigation(new Uri($"http://127.0.0.1:{peer.Port}/")));

    string response = await SendAsync(network, $"GET http://127.0.0.1:{peer.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{peer.Port}\r\n\r\n", deadline.Token);
    Assert.StartsWith("HTTP/1.1 403", response);
    string tunnel = await SendAsync(network, $"CONNECT 127.0.0.1:{peer.Port} HTTP/1.1\r\n\r\n", deadline.Token);
    Assert.StartsWith("HTTP/1.1 403", tunnel);
    Assert.False(peer.Pending);
  }

  [Fact]
  public async Task ForwardsBodyWithDestinationHostAndDoesNotForwardPipeline() {
    await using var network = new PlaywrightNetwork(() => 0);
    using var peer = new LoopbackPeer();
    using var secondPeer = new LoopbackPeer();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Assert.True(network.TryAllowNavigation(new Uri($"http://127.0.0.1:{peer.Port}/")));
    Assert.True(network.TryAllowNavigation(new Uri($"http://127.0.0.1:{secondPeer.Port}/")));

    using TcpClient client = await ConnectAsync(network, deadline.Token);
    NetworkStream stream = client.GetStream();
    string request = $"POST http://127.0.0.1:{peer.Port}/submit?q=1 HTTP/1.1\r\nHost: attacker.invalid\r\nContent-Length: 4\r\n\r\ndata"
        + $"GET http://127.0.0.1:{secondPeer.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{secondPeer.Port}\r\n\r\n";
    await WriteAsync(stream, request, deadline.Token);
    using TcpClient upstream = await peer.AcceptAsync(deadline.Token);
    NetworkStream upstreamStream = upstream.GetStream();
    string headers = await ReadHeadersAsync(upstreamStream, deadline.Token);
    Assert.StartsWith("POST /submit?q=1 HTTP/1.1\r\n", headers);
    Assert.Contains($"Host: 127.0.0.1:{peer.Port}\r\n", headers, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("attacker.invalid", headers, StringComparison.OrdinalIgnoreCase);
    byte[] body = new byte[4];
    await upstreamStream.ReadExactlyAsync(body, deadline.Token);
    Assert.Equal("data", Encoding.ASCII.GetString(body));
    await WriteAsync(upstreamStream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", deadline.Token);
    upstream.Client.Shutdown(SocketShutdown.Send);
    Assert.StartsWith("HTTP/1.1 200", await ReadHeadersAsync(stream, deadline.Token));
    byte[] responseBody = new byte[2];
    await stream.ReadExactlyAsync(responseBody, deadline.Token);
    Assert.Equal("ok", Encoding.ASCII.GetString(responseBody));
    byte[] extra = new byte[1];
    Assert.Equal(0, await stream.ReadAsync(extra, deadline.Token));
    Assert.Equal(0, await upstreamStream.ReadAsync(extra, deadline.Token));
    Assert.False(secondPeer.Pending);
  }

  [Fact]
  public async Task RelaysOnlyOneHttpResponseWhenUpstreamStaysOpen() {
    await using var network = new PlaywrightNetwork(() => 0);
    using var peer = new LoopbackPeer();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Assert.True(network.TryAllowNavigation(new Uri($"http://127.0.0.1:{peer.Port}/")));

    using TcpClient client = await ConnectAsync(network, deadline.Token);
    NetworkStream stream = client.GetStream();
    await WriteAsync(stream, $"GET http://127.0.0.1:{peer.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{peer.Port}\r\n\r\n", deadline.Token);
    using TcpClient upstream = await peer.AcceptAsync(deadline.Token);
    NetworkStream upstreamStream = upstream.GetStream();
    await ReadHeadersAsync(upstreamStream, deadline.Token);
    await WriteAsync(upstreamStream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nokHTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\nbad", deadline.Token);

    Assert.StartsWith("HTTP/1.1 200", await ReadHeadersAsync(stream, deadline.Token));
    byte[] body = new byte[2];
    await stream.ReadExactlyAsync(body, deadline.Token);
    Assert.Equal("ok", Encoding.ASCII.GetString(body));
    Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
  }

  [Fact]
  public async Task ReframesChunkedResponseAndDropsTrailers() {
    await using var network = new PlaywrightNetwork(() => 0);
    using var peer = new LoopbackPeer();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Assert.True(network.TryAllowNavigation(new Uri($"http://127.0.0.1:{peer.Port}/")));

    using TcpClient client = await ConnectAsync(network, deadline.Token);
    NetworkStream stream = client.GetStream();
    await WriteAsync(stream, $"GET http://127.0.0.1:{peer.Port}/ HTTP/1.1\r\n\r\n", deadline.Token);
    using TcpClient upstream = await peer.AcceptAsync(deadline.Token);
    NetworkStream upstreamStream = upstream.GetStream();
    await ReadHeadersAsync(upstreamStream, deadline.Token);
    await WriteAsync(upstreamStream, "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nTrailer: X-Check\r\n\r\n"
        + "2;hint=one\r\nok\r\n0\r\nX-Check: done\r\n\r\n", deadline.Token);

    string headers = await ReadHeadersAsync(stream, deadline.Token);
    Assert.Contains("Transfer-Encoding: chunked\r\n", headers, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Trailer:", headers, StringComparison.OrdinalIgnoreCase);
    const string expected = "2\r\nok\r\n0\r\n\r\n";
    byte[] body = new byte[expected.Length];
    await stream.ReadExactlyAsync(body, deadline.Token);
    Assert.Equal(expected, Encoding.ASCII.GetString(body));
    Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
  }

  [Fact]
  public async Task RejectsConnectWithoutExplicitPort() {
    await using var network = new PlaywrightNetwork(() => 0);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Assert.True(network.TryAllowNavigation(new Uri("https://127.0.0.1/")));
    Assert.StartsWith("HTTP/1.1 400", await SendAsync(network, "CONNECT 127.0.0.1 HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n", deadline.Token));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task RejectsIncompleteOrOversizedHeaders(bool oversized) {
    await using var network = new PlaywrightNetwork(() => 0);
    using var peer = new LoopbackPeer();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Assert.True(network.TryAllowNavigation(new Uri($"http://127.0.0.1:{peer.Port}/")));
    using TcpClient client = await ConnectAsync(network, deadline.Token);
    NetworkStream stream = client.GetStream();
    string prefix = $"GET http://127.0.0.1:{peer.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{peer.Port}\r\nX-Test: ";
    string request = prefix + (oversized ? new string('x', 32 * 1024 - prefix.Length) : "incomplete");
    await WriteAsync(stream, request, deadline.Token);
    client.Client.Shutdown(SocketShutdown.Send);
    Assert.StartsWith("HTTP/1.1 400", await ReadHeadersAsync(stream, deadline.Token));
    Assert.False(peer.Pending);
  }

  [Theory]
  [InlineData("Content-Length: 1\r\nContent-Length: 2\r\n")]
  [InlineData("Content-Length: -1\r\n")]
  [InlineData("Content-Length: 67108865\r\n")]
  [InlineData("Transfer-Encoding: chunked\r\n")]
  [InlineData("Expect: 100-continue\r\n")]
  [InlineData("Connection: Upgrade\r\nUpgrade: websocket\r\n")]
  public async Task RejectsAmbiguousOrUnsupportedFraming(string extraHeaders) {
    await using var network = new PlaywrightNetwork(() => 0);
    using var peer = new LoopbackPeer();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Assert.True(network.TryAllowNavigation(new Uri($"http://127.0.0.1:{peer.Port}/")));
    string request = $"POST http://127.0.0.1:{peer.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{peer.Port}\r\n{extraHeaders}\r\n";
    Assert.StartsWith("HTTP/1.1 400", await SendAsync(network, request, deadline.Token));
    Assert.False(peer.Pending);
  }

  [Fact]
  public async Task RejectsHttpsAbsoluteFormWithoutDialing() {
    await using var network = new PlaywrightNetwork(() => 0);
    using var peer = new LoopbackPeer();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Assert.True(network.TryAllowNavigation(new Uri($"https://127.0.0.1:{peer.Port}/")));
    string request = $"GET https://127.0.0.1:{peer.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{peer.Port}\r\n\r\n";
    Assert.StartsWith("HTTP/1.1 400", await SendAsync(network, request, deadline.Token));
    Assert.False(peer.Pending);
  }

  [Fact]
  public async Task ConnectRelaysOpaqueBytesInBothDirections() {
    await using var network = new PlaywrightNetwork(() => 0);
    using var peer = new LoopbackPeer();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Assert.True(network.TryAllowNavigation(new Uri($"https://127.0.0.1:{peer.Port}/")));
    using TcpClient client = await ConnectAsync(network, deadline.Token);
    NetworkStream stream = client.GetStream();
    byte[] clientRecord = [0x16, 0x03, 0x03, 0x00, 0x02, 0x01, 0x00];
    byte[] serverRecord = [0x17, 0x03, 0x03, 0x00, 0x01, 0xff];
    byte[] connect = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{peer.Port} HTTP/1.1\r\nHost: 127.0.0.1:{peer.Port}\r\n\r\n");
    byte[] initial = [.. connect, .. clientRecord];
    await stream.WriteAsync(initial, deadline.Token);
    Assert.StartsWith("HTTP/1.1 200", await ReadHeadersAsync(stream, deadline.Token));
    using TcpClient upstream = await peer.AcceptAsync(deadline.Token);
    NetworkStream upstreamStream = upstream.GetStream();
    byte[] received = new byte[clientRecord.Length];
    await upstreamStream.ReadExactlyAsync(received, deadline.Token);
    Assert.Equal(clientRecord, received);
    await upstreamStream.WriteAsync(serverRecord, deadline.Token);
    received = new byte[serverRecord.Length];
    await stream.ReadExactlyAsync(received, deadline.Token);
    Assert.Equal(serverRecord, received);
  }

  private static async Task<TcpClient> ConnectAsync(PlaywrightNetwork network, CancellationToken cancellation) {
    var address = new Uri(network.Address);
    var client = new TcpClient();
    try {
      await client.ConnectAsync(IPAddress.Loopback, address.Port, cancellation);
      return client;
    } catch {
      client.Dispose();
      throw;
    }
  }

  private static async Task<string> SendAsync(PlaywrightNetwork network, string request, CancellationToken cancellation) {
    using TcpClient client = await ConnectAsync(network, cancellation);
    NetworkStream stream = client.GetStream();
    await WriteAsync(stream, request, cancellation);
    return await ReadHeadersAsync(stream, cancellation);
  }

  private static async Task WriteAsync(NetworkStream stream, string text, CancellationToken cancellation) =>
      await stream.WriteAsync(Encoding.ASCII.GetBytes(text), cancellation);

  private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellation) {
    var bytes = new List<byte>();
    byte[] buffer = new byte[1];
    while (bytes.Count < 32 * 1024) {
      if (await stream.ReadAsync(buffer, cancellation) == 0)
        throw new IOException("Connection closed before the headers completed.");
      bytes.Add(buffer[0]);
      int count = bytes.Count;
      if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n' && bytes[count - 2] == '\r' && bytes[count - 1] == '\n')
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
    throw new IOException("Headers exceeded the test limit.");
  }

  private sealed class LoopbackPeer : IDisposable {
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);

    public LoopbackPeer() => listener.Start();
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool Pending => listener.Pending();
    public ValueTask<TcpClient> AcceptAsync(CancellationToken cancellation) => listener.AcceptTcpClientAsync(cancellation);
    public void Dispose() => listener.Stop();
  }
}
