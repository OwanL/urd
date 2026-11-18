using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Urd.Core.Tools;

// The browser never gets a raw CONNECT tunnel. TLS is re-originated here so every request,
// including redirects, has the same address, method and decompressed-byte limits.
internal sealed class WebBrowserProxy : IAsyncDisposable {
  internal const int MaximumBodyBytes = 10 * 1024 * 1024;
  private const int MaximumTotalBytes = 32 * 1024 * 1024;
  private const int MaximumRequests = 128;
  private readonly CancellationTokenSource stopping = new();
  private readonly TcpListener listener = new(IPAddress.Loopback, 0);
  private readonly HttpClient client;
  private readonly SslServerAuthenticationOptions tls;
  private readonly Task workers;
  private int requests;
  private long receivedBytes;

  public WebBrowserProxy(X509Certificate2 certificate) : this(certificate, CreateUpstreamHandler()) { }

  internal WebBrowserProxy(X509Certificate2 certificate, HttpMessageHandler upstream) {
    tls = new SslServerAuthenticationOptions {
      ServerCertificate = certificate,
      ApplicationProtocols = [SslApplicationProtocol.Http11],
      AllowRenegotiation = false
    };
    listener.Start(backlog: 16);
    client = new HttpClient(upstream);
    workers = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AcceptAsync(stopping.Token)));
  }

  private static SocketsHttpHandler CreateUpstreamHandler() => new() {
    UseProxy = false,
    UseCookies = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.All,
    MaxResponseHeadersLength = 32,
    PooledConnectionLifetime = TimeSpan.FromSeconds(30),
    ConnectTimeout = TimeSpan.FromSeconds(6),
    ConnectCallback = WebBrowserAddress.ConnectAsync,
    // Don't let OS chain building initiate requests outside the public-address transport.
    SslOptions = new SslClientAuthenticationOptions {
      CertificateChainPolicy = new X509ChainPolicy {
        DisableCertificateDownloads = true,
        ApplicationPolicy = { new Oid("1.3.6.1.5.5.7.3.1") }
      }
    }
  };

  public string Address => WebBrowserProxyProtocol.AddressOf(listener.LocalEndpoint);

  public async ValueTask DisposeAsync() {
    await stopping.CancelAsync();
    try {
      await workers;
    } finally {
      listener.Stop();
      client.Dispose();
      stopping.Dispose();
    }
  }

  private async Task AcceptAsync(CancellationToken cancellation) {
    while (!cancellation.IsCancellationRequested) {
      try {
        using TcpClient connection = await listener.AcceptTcpClientAsync(cancellation);
        connection.NoDelay = true;
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        await HandleAsync(connection.GetStream(), requestTimeout.Token);
      } catch (Exception failure) when (failure is IOException or SocketException or OperationCanceledException
          or System.Security.Authentication.AuthenticationException) {
        // A preconnect, closed tab or malformed TLS conversation is scoped to this socket.
      }
    }
  }

  private async Task HandleAsync(Stream stream, CancellationToken cancellation) {
    WebBrowserProxyRequest? request = await WebBrowserProxyProtocol.ReadRequestAsync(stream, cancellation);
    if (request is null)
      return;
    if (request.Method != "CONNECT") {
      Uri.TryCreate(request.Target, UriKind.Absolute, out Uri? plainUrl);
      await ForwardAsync(stream, request, plainUrl?.Scheme == Uri.UriSchemeHttp ? plainUrl : null, cancellation);
      return;
    }

    if (!Uri.TryCreate($"https://{request.Target}/", UriKind.Absolute, out Uri? origin)
        || !WebBrowserAddress.IsHttpUrl(origin) || origin.PathAndQuery != "/" || origin.Fragment.Length != 0)
      return;
    await WebBrowserProxyProtocol.ConnectedAsync(stream, cancellation);
    using var secure = new SslStream(stream, leaveInnerStreamOpen: true);
    await secure.AuthenticateAsServerAsync(tls, cancellation);
    WebBrowserProxyRequest? inner = await WebBrowserProxyProtocol.ReadRequestAsync(secure, cancellation);
    if (inner is null)
      return;

    Uri? url = inner.Target.StartsWith('/') && Uri.TryCreate(origin.GetLeftPart(UriPartial.Authority) + inner.Target,
        UriKind.Absolute, out Uri? parsed) ? parsed : null;
    await ForwardAsync(secure, inner, url, cancellation);
  }

  private async Task ForwardAsync(Stream stream, WebBrowserProxyRequest request, Uri? url, CancellationToken cancellation) {
    if (Interlocked.Increment(ref requests) > MaximumRequests) {
      await WebBrowserProxyProtocol.FailureAsync(stream, HttpStatusCode.TooManyRequests, "request-limit", cancellation);
      return;
    }
    if (!WebBrowserAddress.IsHttpUrl(url) || !WebBrowserAddress.CouldBePublic(url!)) {
      await WebBrowserProxyProtocol.FailureAsync(stream, HttpStatusCode.UnavailableForLegalReasons, "destination", cancellation);
      return;
    }
    if (request.Method is not ("GET" or "HEAD") || request.Headers.ContainsKey("Upgrade")
        || request.Headers.ContainsKey("Transfer-Encoding") || request.Headers.GetValueOrDefault("Content-Length", "0") != "0") {
      await WebBrowserProxyProtocol.FailureAsync(stream, HttpStatusCode.MethodNotAllowed, "read-only", cancellation);
      return;
    }

    using HttpRequestMessage upstream = WebBrowserProxyProtocol.UpstreamRequest(request, url!);
    try {
      using HttpResponseMessage response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, cancellation);
      await ReplyAsync(stream, url!, request.Method == "HEAD", response, cancellation);
    } catch (HttpRequestException failure) {
      bool blocked = failure is WebBrowserDestinationException || failure.InnerException is WebBrowserDestinationException;
      await WebBrowserProxyProtocol.FailureAsync(stream,
          blocked ? HttpStatusCode.UnavailableForLegalReasons : HttpStatusCode.BadGateway,
          blocked ? "destination" : "upstream", cancellation);
    }
  }

  private async Task ReplyAsync(Stream stream, Uri url, bool head, HttpResponseMessage response, CancellationToken cancellation) {
    if (response.Headers.Location is { } location && (int)response.StatusCode is >= 300 and < 400
        && (!Uri.TryCreate(url, location, out Uri? next) || !WebBrowserAddress.IsHttpUrl(next))) {
      await WebBrowserProxyProtocol.FailureAsync(stream, HttpStatusCode.UnavailableForLegalReasons, "destination", cancellation);
      return;
    }
    if (head || response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified) {
      await WebBrowserProxyProtocol.ResponseAsync(stream, response, response.Content.Headers.ContentLength, cancellation);
      return;
    }
    if (response.Content.Headers.ContentEncoding.Count != 0) {
      await WebBrowserProxyProtocol.FailureAsync(stream, HttpStatusCode.BadGateway, "content-encoding", cancellation);
      return;
    }

    using MemoryStream? body = await ReadBodyAsync(response.Content, cancellation);
    if (body is null) {
      await WebBrowserProxyProtocol.FailureAsync(stream, HttpStatusCode.RequestEntityTooLarge, "body-limit", cancellation);
      return;
    }
    await WebBrowserProxyProtocol.ResponseAsync(stream, response, body.Length, cancellation);
    await body.CopyToAsync(stream, cancellation);
  }

  private async Task<MemoryStream?> ReadBodyAsync(HttpContent content, CancellationToken cancellation) {
    if (content.Headers.ContentLength > MaximumBodyBytes || Interlocked.Read(ref receivedBytes) >= MaximumTotalBytes)
      return null;

    await using Stream input = await content.ReadAsStreamAsync(cancellation);
    byte[] buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
    using var body = new MemoryStream((int)(content.Headers.ContentLength ?? 0));
    try {
      int read;
      while ((read = await input.ReadAsync(buffer, cancellation)) > 0) {
        if (Interlocked.Add(ref receivedBytes, read) > MaximumTotalBytes || body.Length + read > MaximumBodyBytes)
          return null;
        await body.WriteAsync(buffer.AsMemory(0, read), cancellation);
      }
      return new MemoryStream(body.GetBuffer(), 0, (int)body.Length, writable: false);
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }
}
