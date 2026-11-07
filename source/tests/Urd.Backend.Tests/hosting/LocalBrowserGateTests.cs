using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

// Public and private routes share local admission; only private routes require the browser marker.
public sealed class LocalBrowserGateTests {
  private static readonly Uri origin = new("http://127.0.0.1:43127");

  [Theory]
  [InlineData("127.0.0.1")]
  [InlineData("127.9.8.7")]
  [InlineData("::1")]
  public async Task AcceptsLoopbackPeerWithMatchingHostAndOrigin(string peer) {
    DefaultHttpContext context = LocalRequest();
    context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
    context.Request.Headers.Origin = origin.GetLeftPart(UriPartial.Authority);

    await AssertAdmissionAsync(context, StatusCodes.Status204NoContent);
  }

  [Fact]
  public async Task AllowsMissingOriginWithoutImposingRouteMethodOrBodyRules() {
    DefaultHttpContext context = LocalRequest();
    context.Request.Method = HttpMethods.Post;
    context.Request.ContentLength = 12;

    await AssertAdmissionAsync(context, StatusCodes.Status204NoContent);
  }

  [Theory]
  [InlineData("127.0.0.1")]
  [InlineData("127.0.0.1:80")]
  public async Task AcceptsImplicitAndExplicitDefaultHostPort(string host) {
    DefaultHttpContext context = LocalRequest();
    context.Request.Host = new HostString(host);
    context.Request.Headers.Origin = "http://127.0.0.1";

    await AssertAdmissionAsync(context, StatusCodes.Status204NoContent, () => new Uri("http://127.0.0.1:80"));
  }

  [Theory]
  [InlineData("127.0.0.1:invalid")]
  [InlineData("127.0.0.1:")]
  public async Task MalformedPortDoesNotBecomeDefaultPort(string host) {
    DefaultHttpContext context = LocalRequest();
    context.Request.Host = new HostString(host);

    await AssertAdmissionAsync(context, StatusCodes.Status403Forbidden, () => new Uri("http://127.0.0.1"));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("192.0.2.1")]
  public async Task RejectsMissingOrNonLoopbackPeer(string? peer) {
    DefaultHttpContext context = LocalRequest();
    context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);

    await AssertAdmissionAsync(context, StatusCodes.Status403Forbidden);
  }

  [Theory]
  [InlineData("")]
  [InlineData("localhost:43127")]
  [InlineData("[::1]:43127")]
  [InlineData("127.0.0.1")]
  [InlineData("127.0.0.1:80")]
  [InlineData("127.0.0.1:43128")]
  [InlineData("127.0.0.1:invalid")]
  public async Task RejectsWrongHostOrEffectivePort(string host) {
    DefaultHttpContext context = LocalRequest();
    context.Request.Host = new HostString(host);

    await AssertAdmissionAsync(context, StatusCodes.Status403Forbidden);
  }

  [Theory]
  [InlineData("http://foreign.example")]
  [InlineData("null")]
  [InlineData("http://127.0.0.1:43127/")]
  [InlineData("HTTP://127.0.0.1:43127")]
  [InlineData("")]
  public async Task RejectsNonCanonicalOrigin(string suppliedOrigin) {
    DefaultHttpContext context = LocalRequest();
    context.Request.Headers.Origin = suppliedOrigin;

    await AssertAdmissionAsync(context, StatusCodes.Status403Forbidden);
  }

  [Fact]
  public async Task RejectsMultipleOriginsEvenWhenBothMatch() {
    DefaultHttpContext context = LocalRequest();
    string authority = origin.GetLeftPart(UriPartial.Authority);
    context.Request.Headers.Origin = new StringValues([authority, authority]);

    await AssertAdmissionAsync(context, StatusCodes.Status403Forbidden);
  }

  [Fact]
  public async Task ReadsCurrentReadinessOnEveryRequest() {
    Uri? publishedOrigin = null;
    int calls = 0;
    var gate = new LocalBrowserGate(_ => {
      calls++;
      return Task.CompletedTask;
    }, () => publishedOrigin);

    DefaultHttpContext starting = LocalRequest();
    await gate.InvokeAsync(starting);
    Assert.Equal(StatusCodes.Status503ServiceUnavailable, starting.Response.StatusCode);
    Assert.Equal(0, calls);

    publishedOrigin = origin;
    await gate.InvokeAsync(LocalRequest());
    Assert.Equal(1, calls);

    publishedOrigin = null;
    DefaultHttpContext stopping = LocalRequest();
    await gate.InvokeAsync(stopping);
    Assert.Equal(StatusCodes.Status503ServiceUnavailable, stopping.Response.StatusCode);
    Assert.Equal(1, calls);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ForwardedHeadersCannotReplacePeerOrHost(bool wrongHost) {
    DefaultHttpContext context = LocalRequest();
    if (wrongHost)
      context.Request.Host = new HostString("foreign.example:43127");
    else
      context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
    context.Request.Headers["Forwarded"] = "for=127.0.0.1;host=127.0.0.1:43127;proto=http";
    context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
    context.Request.Headers["X-Forwarded-Host"] = "127.0.0.1:43127";
    context.Request.Headers["X-Forwarded-Proto"] = "http";

    await AssertAdmissionAsync(context, StatusCodes.Status403Forbidden);
  }

  [Theory]
  [InlineData("/api")]
  [InlineData("/api/")]
  [InlineData("/API/sessions")]
  [InlineData("/api/unknown")]
  public async Task PrivatePathsRequireMarkerEvenWithoutOrigin(string path) {
    DefaultHttpContext context = LocalRequest();
    context.Request.Path = path;

    await AssertAdmissionAsync(context, StatusCodes.Status403Forbidden);
    Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());

    context.Request.Headers["X-Urd-Client"] = "browser";
    await AssertAdmissionAsync(context, StatusCodes.Status204NoContent);
  }

  [Theory]
  [InlineData("")]
  [InlineData("Browser")]
  [InlineData("browser ")]
  [InlineData("native")]
  [InlineData("browser,browser")]
  public async Task PrivatePathsRequireExactMarker(string marker) {
    DefaultHttpContext context = LocalRequest();
    context.Request.Path = "/api/sessions";
    context.Request.Headers["X-Urd-Client"] = marker;
    context.Request.Headers.Origin = origin.GetLeftPart(UriPartial.Authority);

    await AssertAdmissionAsync(context, StatusCodes.Status403Forbidden);
  }

  [Fact]
  public async Task DuplicateMarkersAreNotAnAdmissionCredential() {
    DefaultHttpContext context = LocalRequest();
    context.Request.Path = "/api/sessions";
    context.Request.Headers["X-Urd-Client"] = new StringValues(["browser", "browser"]);

    await AssertAdmissionAsync(context, StatusCodes.Status403Forbidden);
  }

  [Theory]
  [InlineData("/health")]
  [InlineData("/apiculture")]
  [InlineData("/api-other")]
  public async Task PublicPathsRemainUnmarkedAndDoNotGetPrivateCachePolicy(string path) {
    DefaultHttpContext context = LocalRequest();
    context.Request.Path = path;

    await AssertAdmissionAsync(context, StatusCodes.Status204NoContent);
    Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task PrivateRejectionsAreNotCacheableBeforeAnyAdmissionCheck(bool ready) {
    DefaultHttpContext context = LocalRequest();
    context.Request.Path = "/api/sessions";
    context.Request.Headers.Origin = "http://foreign.example";

    await AssertAdmissionAsync(context, ready ? StatusCodes.Status403Forbidden : StatusCodes.Status503ServiceUnavailable,
        () => ready ? origin : null);
    Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
  }

  private static DefaultHttpContext LocalRequest() {
    var context = new DefaultHttpContext();
    context.Connection.RemoteIpAddress = IPAddress.Loopback;
    context.Request.Host = new HostString("127.0.0.1", origin.Port);
    return context;
  }

  private static async Task AssertAdmissionAsync(DefaultHttpContext context, int expectedStatus,
      Func<Uri?>? servedOrigin = null) {
    int calls = 0;
    var gate = new LocalBrowserGate(received => {
      Assert.Same(context, received);
      calls++;
      received.Response.StatusCode = StatusCodes.Status204NoContent;
      return Task.CompletedTask;
    }, servedOrigin ?? (() => origin));

    await gate.InvokeAsync(context);

    Assert.Equal(expectedStatus, context.Response.StatusCode);
    Assert.Equal(expectedStatus == StatusCodes.Status204NoContent ? 1 : 0, calls);
    Assert.False(context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
  }
}
