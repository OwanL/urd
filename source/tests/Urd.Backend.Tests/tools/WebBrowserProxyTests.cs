using System.Net;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class WebBrowserProxyTests {
  [Theory]
  [InlineData("0.42.0.1", false)]
  [InlineData("10.0.0.1", false)]
  [InlineData("100.64.10.1", false)]
  [InlineData("127.1.2.3", false)]
  [InlineData("169.254.169.254", false)]
  [InlineData("172.31.0.1", false)]
  [InlineData("172.32.0.1", true)]
  [InlineData("192.0.0.1", false)]
  [InlineData("192.168.0.1", false)]
  [InlineData("198.18.0.1", false)]
  [InlineData("224.0.0.1", false)]
  [InlineData("240.0.0.1", false)]
  [InlineData("8.8.8.8", true)]
  [InlineData("::", false)]
  [InlineData("::1", false)]
  [InlineData("::ffff:127.0.0.1", false)]
  [InlineData("::ffff:8.8.8.8", true)]
  [InlineData("fc00::1", false)]
  [InlineData("fe80::1", false)]
  [InlineData("ff02::1", false)]
  [InlineData("64:ff9b::a00:1", false)]
  [InlineData("2002:7f00:1::1", false)]
  [InlineData("2001:db8::1", false)]
  [InlineData("3fff::1", false)]
  [InlineData("2606:4700:4700::1111", true)]
  public void OnlyPublicAddressesCanBeDialed(string address, bool expected) =>
      Assert.Equal(expected, WebBrowserAddress.IsPublic(IPAddress.Parse(address)));

  [Fact]
  public void DisallowMixedResolutionAndPublicAddressesAssignedLocally() {
    IPAddress publicAddress = IPAddress.Parse("8.8.8.8");
    WebBrowserAddress.RequirePublicAddresses([publicAddress], []);
    Assert.Throws<WebBrowserDestinationException>(() => WebBrowserAddress.RequirePublicAddresses([publicAddress, IPAddress.Loopback], []));
    Assert.Throws<WebBrowserDestinationException>(() => WebBrowserAddress.RequirePublicAddresses([publicAddress], [publicAddress]));
  }

  [Fact]
  public async Task PrivateRedirectsWritesAndOversizedBodiesAreBlocked() {
    using var certificate = WebBrowserCertificate.Create();
    using var upstream = new FixtureHandler();
    await using var proxy = new WebBrowserProxy(certificate, upstream);
    using var client = new HttpClient(new SocketsHttpHandler {
      Proxy = new FixtureProxy(new Uri(proxy.Address)),
      SslOptions = new() {
        RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.Equals(certificate) == true
      }
    });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    Uri fixture = new UriBuilder(Uri.UriSchemeHttps, "fixture.example").Uri;
    await AssertBlockedAsync(client, HttpMethod.Get, new Uri(fixture, "redirect"), 451, "destination", timeout.Token);
    await AssertBlockedAsync(client, HttpMethod.Post, fixture, 405, "read-only", timeout.Token);
    await AssertBlockedAsync(client, HttpMethod.Get, new Uri(fixture, "large"), 413, "body-limit", timeout.Token);
    Assert.Equal("hello", await client.GetStringAsync(fixture, timeout.Token));
    Assert.Equal(3, upstream.Requests);
  }

  private static async Task AssertBlockedAsync(HttpClient client, HttpMethod method, Uri url, int status, string reason,
      CancellationToken cancellation) {
    using var request = new HttpRequestMessage(method, url);
    using HttpResponseMessage response = await client.SendAsync(request, cancellation);
    Assert.Equal(status, (int)response.StatusCode);
    Assert.Equal(reason, Assert.Single(response.Headers.GetValues("X-Urd-Web-Blocked")));
  }

  private sealed class FixtureProxy(Uri address) : IWebProxy {
    public ICredentials? Credentials { get; set; }
    public Uri GetProxy(Uri destination) => address;
    public bool IsBypassed(Uri host) => false;
  }

  private sealed class FixtureHandler : HttpMessageHandler {
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      Requests++;
      if (request.RequestUri!.AbsolutePath == "/redirect") {
        var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
        redirect.Headers.Location = new UriBuilder(Uri.UriSchemeHttps, IPAddress.Loopback.ToString(), 12456).Uri;
        return Task.FromResult(redirect);
      }
      HttpContent content = request.RequestUri.AbsolutePath == "/large"
          ? new StreamContent(new LargeStream()) : new StringContent("hello");
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
  }

  private sealed class LargeStream() : MemoryStream(new byte[WebBrowserProxy.MaximumBodyBytes + 1]) {
    public override bool CanSeek => false;
  }
}
