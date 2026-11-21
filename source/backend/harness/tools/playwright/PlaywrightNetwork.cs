using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Urd.Core.Tools;

internal sealed class PlaywrightNetwork : IAsyncDisposable {
  private const int ConnectionLimit = 64;
  private const int LocalOriginLimit = 256;
  private const int CopyBufferBytes = 16 * 1024;
  private readonly Func<int> blockedHostPort;
  private readonly bool allowPublic;
  private readonly TcpListener listener = new(IPAddress.Loopback, 0);
  private readonly CancellationTokenSource stopping = new();
  private readonly Lock gate = new();
  private readonly HashSet<string> localOrigins = new(StringComparer.OrdinalIgnoreCase);
  private readonly Task workers;
  private readonly int port;
  private Task? disposal;

  // The callback supplies the current host port and must be safe to read on proxy workers.
  public PlaywrightNetwork(Func<int> blockedHostPort, bool allowPublic = true) {
    ArgumentNullException.ThrowIfNull(blockedHostPort);
    this.blockedHostPort = blockedHostPort;
    this.allowPublic = allowPublic;
    listener.Start(backlog: ConnectionLimit);
    port = ((IPEndPoint)listener.LocalEndpoint).Port;
    Address = new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port).Uri.AbsoluteUri;
    workers = Task.WhenAll(Enumerable.Range(0, ConnectionLimit).Select(_ => AcceptAsync(stopping.Token)));
  }

  public string Address { get; }

  // A nomination grants one exact local origin for this proxy's lifetime. Public names are
  // only candidates here; the proxy checks all resolved addresses on every new connection.
  public bool TryAllowNavigation(Uri url) {
    PlaywrightNetworkDestination? destination = PlaywrightNetworkDestination.FromNavigation(url);
    if (destination is null || IsBlockedPort(destination.Url.Port))
      return false;
    lock (gate) {
      if (disposal is not null)
        return false;
      if (destination.LoopbackAddress is null)
        return allowPublic && WebBrowserAddress.CouldBePublic(destination.Url);
      if (localOrigins.Contains(destination.Origin))
        return true;
      return localOrigins.Count < LocalOriginLimit && localOrigins.Add(destination.Origin);
    }
  }

  public ValueTask DisposeAsync() {
    lock (gate)
      return new ValueTask(disposal ??= StopAsync());
  }

  private async Task StopAsync() {
    try {
      await stopping.CancelAsync();
      await workers;
    } finally {
      listener.Stop();
      stopping.Dispose();
    }
  }

  private async Task AcceptAsync(CancellationToken cancellation) {
    while (!cancellation.IsCancellationRequested) {
      try {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellation);
        client.NoDelay = true;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lifetime.CancelAfter(TimeSpan.FromMinutes(5));
        await HandleAsync(client.GetStream(), lifetime.Token);
      } catch (Exception failure) when (IsConnectionFailure(failure)) {
        // Browser preconnects, timeouts and disconnected peers are scoped to one socket.
      }
    }
  }

  private async Task HandleAsync(Stream browser, CancellationToken cancellation) {
    using var headerDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    headerDeadline.CancelAfter(TimeSpan.FromSeconds(10));
    PlaywrightNetworkRequest? request = await PlaywrightNetworkRequest.ReadAsync(browser, headerDeadline.Token);
    bool tunnel = request?.Method == "CONNECT";
    PlaywrightNetworkDestination? destination = request is null ? null : PlaywrightNetworkDestination.FromRequest(request.Target, tunnel);
    if (request is null || destination is null) {
      await browser.WriteAsync("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancellation);
      return;
    }

    TcpClient? upstream = await OpenAsync(browser, destination, cancellation);
    if (upstream is null)
      return;
    using (upstream) {
      if (tunnel)
        await browser.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), cancellation);
      await RelayAsync(browser, upstream, request, destination.Url, tunnel, cancellation);
    }
  }

  private async Task<TcpClient?> OpenAsync(Stream browser, PlaywrightNetworkDestination destination, CancellationToken cancellation) {
    try {
      if (!IsAllowed(destination))
        throw new WebBrowserDestinationException();
      using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
      deadline.CancelAfter(TimeSpan.FromSeconds(10));
      TcpClient upstream = await DialAsync(destination, deadline.Token);
      try {
        RequireAllowedPort(destination.Url.Port);
        return upstream;
      } catch {
        upstream.Dispose();
        throw;
      }
    } catch (WebBrowserDestinationException) {
      await browser.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancellation);
    } catch (Exception error) when (error is SocketException or HttpRequestException or NetworkInformationException
        || (error is OperationCanceledException && !cancellation.IsCancellationRequested)) {
      await browser.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancellation);
    }
    return null;
  }

  internal bool AllowsNavigation(Uri url) => PlaywrightNetworkDestination.FromNavigation(url) is { } destination && IsAllowed(destination);

  private bool IsAllowed(PlaywrightNetworkDestination destination) {
    if (IsBlockedPort(destination.Url.Port))
      return false;
    if (destination.LoopbackAddress is null)
      return allowPublic && WebBrowserAddress.CouldBePublic(destination.Url);
    lock (gate)
      return localOrigins.Contains(destination.Origin);
  }

  private bool IsBlockedPort(int destinationPort) => destinationPort == port || destinationPort == blockedHostPort();

  private void RequireAllowedPort(int destinationPort) {
    if (IsBlockedPort(destinationPort))
      throw new WebBrowserDestinationException();
  }

  private async Task<TcpClient> DialAsync(PlaywrightNetworkDestination destination, CancellationToken cancellation) {
    IPAddress[] addresses = destination.LoopbackAddress is { } loopback
        ? FixedLoopbackAddresses(destination.Url, loopback) : await ResolvePublicAsync(destination.Url, cancellation);
    SocketException? lastFailure = null;
    foreach (IPAddress resolved in addresses) {
      RequireAllowedPort(destination.Url.Port);
      IPAddress address = NormalizeAddress(resolved);
      TcpClient? client = null;
      try {
        client = new TcpClient(address.AddressFamily) { NoDelay = true };
        await client.ConnectAsync(address, destination.Url.Port, cancellation);
        return client;
      } catch (SocketException failure) {
        client?.Dispose();
        lastFailure = failure;
      } catch {
        client?.Dispose();
        throw;
      }
    }
    throw new HttpRequestException("Could not connect to the destination.", lastFailure);
  }

  private static IPAddress[] FixedLoopbackAddresses(Uri url, IPAddress address) =>
      url.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? [address, IPAddress.IPv6Loopback] : [address];

  private static async Task<IPAddress[]> ResolvePublicAsync(Uri url, CancellationToken cancellation) {
    IPAddress[] addresses = IPAddress.TryParse(url.DnsSafeHost, out IPAddress? literal) ? [literal]
        : await Dns.GetHostAddressesAsync(url.IdnHost.TrimEnd('.') + ".", cancellation);
    HashSet<IPAddress> localAddresses = NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Select(entry => NormalizeAddress(entry.Address)).ToHashSet();
    // Reject the whole answer on any denied address. ConnectAsync receives only checked IPs, never the hostname.
    WebBrowserAddress.RequirePublicAddresses(addresses, localAddresses);
    return addresses;
  }

  private static IPAddress NormalizeAddress(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

  private static async Task RelayAsync(Stream browser, TcpClient upstream, PlaywrightNetworkRequest request, Uri url,
      bool tunnel, CancellationToken cancellation) {
    using var relay = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    NetworkStream server = upstream.GetStream();
    Task receiving = tunnel ? server.CopyToAsync(browser, CopyBufferBytes, relay.Token)
        : PlaywrightNetworkResponse.ForwardAsync(server, browser, request.Method, relay.Token);
    Task sending = tunnel ? SendTunnelAsync(browser, upstream, request, relay.Token)
        : request.ForwardAsync(browser, server, url, relay.Token);
    try {
      if (await Task.WhenAny(receiving, sending) == sending)
        await sending;
      await receiving;
    } finally {
      await relay.CancelAsync();
      await IgnoreDisconnectAsync(sending);
      await IgnoreDisconnectAsync(receiving);
    }
  }

  private static async Task SendTunnelAsync(Stream browser, TcpClient upstream, PlaywrightNetworkRequest request,
      CancellationToken cancellation) {
    NetworkStream server = upstream.GetStream();
    await request.ForwardTunnelPrefixAsync(server, cancellation);
    await browser.CopyToAsync(server, CopyBufferBytes, cancellation);
    upstream.Client.Shutdown(SocketShutdown.Send);
  }

  private static async Task IgnoreDisconnectAsync(Task transfer) {
    try {
      await transfer;
    } catch (Exception error) when (IsConnectionFailure(error)) {
      // Both pumps are observed, including the one canceled after the peer closes.
    }
  }

  private static bool IsConnectionFailure(Exception failure) => failure is IOException or SocketException or OperationCanceledException;
}
