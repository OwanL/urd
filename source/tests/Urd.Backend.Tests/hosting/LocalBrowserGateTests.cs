using System.Net;
using Microsoft.AspNetCore.Http;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

// Representative admission checks for private browser routes and public health.
public sealed class LocalBrowserGateTests {
  private static readonly Uri origin = new("http://127.0.0.1:43127");

  [Fact]
  public async Task RequiresReadyLoopbackOriginHostAndExactMarkerOnlyForPrivateRoutes() {
    DefaultHttpContext accepted = Request("/api/sessions", marker: "browser", suppliedOrigin: Authority);
    Assert.Equal(StatusCodes.Status204NoContent, await InvokeAsync(accepted));

    foreach (DefaultHttpContext rejected in new[] {
      Request("/api/sessions"),
      Request("/api/sessions", marker: "Browser", suppliedOrigin: Authority),
      Request("/api/sessions", marker: "browser", suppliedOrigin: "http://foreign.example"),
      Request("/api/sessions", marker: "browser", host: "foreign.example:43127", suppliedOrigin: Authority),
      Request("/api/sessions", marker: "browser", suppliedOrigin: Authority,
          remoteAddress: IPAddress.Parse("192.0.2.1"))
    })
      Assert.Equal(StatusCodes.Status403Forbidden, await InvokeAsync(rejected));

    DefaultHttpContext publicHealth = Request("/health");
    Assert.Equal(StatusCodes.Status204NoContent, await InvokeAsync(publicHealth));
    Assert.False(publicHealth.Response.Headers.ContainsKey("Cache-Control"));

    DefaultHttpContext unavailable = Request("/api/sessions", marker: "browser", suppliedOrigin: Authority);
    Assert.Equal(StatusCodes.Status503ServiceUnavailable, await InvokeAsync(unavailable, ready: false));
  }

  [Fact]
  public async Task LanAdmissionRequiresEnabledBoundHostPrivatePeerAndMatchingOrigin() {
    var access = new LanAccess();
    access.Replace(new[] { IPAddress.Parse("192.168.1.10"), IPAddress.Parse("169.254.1.2") });
    string host = "192.168.1.10:43127";
    string lanOrigin = "http://192.168.1.10:43127";
    IPAddress peer = IPAddress.Parse("10.0.0.4");

    foreach (DefaultHttpContext accepted in new[] {
      Request("/api/sessions", marker: "browser", host: host, remoteAddress: peer),
      Request("/api/sessions", marker: "browser", host: host, suppliedOrigin: lanOrigin, remoteAddress: peer),
      Request("/health", host: "169.254.1.2:43127", remoteAddress: IPAddress.Parse("169.254.5.6")),
      Request("/health", host: host, remoteAddress: peer.MapToIPv6()),
      Request("/health", host: host, remoteAddress: IPAddress.Loopback),
      Request("/health", remoteAddress: IPAddress.Loopback.MapToIPv6())
    })
      Assert.Equal(StatusCodes.Status204NoContent, await InvokeAsync(accepted, access: access));

    foreach (DefaultHttpContext rejected in new[] {
      Request("/health", host: "192.168.1.11:43127", remoteAddress: peer),
      Request("/health", host: "192.168.1.10:43128", remoteAddress: peer),
      Request("/health", host: host, suppliedOrigin: Authority, remoteAddress: peer),
      Request("/health", host: host, suppliedOrigin: lanOrigin + "/", remoteAddress: peer),
      Request("/health", host: host, remoteAddress: IPAddress.Parse("192.0.2.1")),
      Request("/health", host: host, remoteAddress: IPAddress.Parse("2001:db8::1")),
      Request("/health", remoteAddress: peer),
      Request("/api/sessions", host: host, remoteAddress: peer)
    })
      Assert.Equal(StatusCodes.Status403Forbidden, await InvokeAsync(rejected, access: access));

    DefaultHttpContext multipleOrigins = Request("/health", host: host, remoteAddress: peer);
    multipleOrigins.Request.Headers.Origin = new Microsoft.Extensions.Primitives.StringValues(new[] { lanOrigin, lanOrigin });
    Assert.Equal(StatusCodes.Status403Forbidden, await InvokeAsync(multipleOrigins, access: access));

    DefaultHttpContext callback = Request("/auth/callback", host: host, remoteAddress: peer);
    Assert.Equal(StatusCodes.Status204NoContent, await InvokeAsync(callback, access: access));
    Assert.Equal("no-store", callback.Response.Headers.CacheControl.ToString());

    access.Disable();
    Assert.Equal(StatusCodes.Status403Forbidden, await InvokeAsync(Request("/health", host: host, remoteAddress: peer), access: access));
    Assert.Equal(StatusCodes.Status204NoContent, await InvokeAsync(Request("/health"), access: access));
  }

  [Fact]
  public void LanSnapshotsAreImmutableDistinctAndLimitedToPrivateIPv4() {
    var access = new LanAccess();
    Assert.False(access.Snapshot.Enabled);
    access.Replace(new[] {
      IPAddress.Parse("192.168.1.10"), IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.1"),
      IPAddress.Parse("172.16.0.1"), IPAddress.Parse("172.31.255.255"), IPAddress.Parse("169.254.1.1"),
      IPAddress.Parse("172.15.255.255"), IPAddress.Parse("172.32.0.1"), IPAddress.Loopback,
      IPAddress.IPv6Loopback
    });
    LanAccessSnapshot snapshot = access.Snapshot;
    Assert.True(snapshot.Enabled);
    Assert.Equal(new[] { "10.0.0.1", "169.254.1.1", "172.16.0.1", "172.31.255.255", "192.168.1.10" }, snapshot.Addresses);
    Assert.Equal("http://10.0.0.1:43127", snapshot.Origins(43127)[0]);
    access.Disable();
    Assert.False(access.Snapshot.Enabled);
    Assert.Empty(access.Snapshot.Addresses);
    Assert.True(snapshot.Enabled);
    Assert.Equal(5, snapshot.Addresses.Length);
  }

  private static string Authority => origin.GetLeftPart(UriPartial.Authority);

  private static DefaultHttpContext Request(string path, string? marker = null, string? suppliedOrigin = null,
      string? host = null, IPAddress? remoteAddress = null) {
    var context = new DefaultHttpContext();
    context.Connection.RemoteIpAddress = remoteAddress ?? IPAddress.Loopback;
    context.Request.Host = new HostString(host ?? $"127.0.0.1:{origin.Port}");
    context.Request.Path = path;
    if (marker is not null)
      context.Request.Headers["X-Urd-Client"] = marker;
    if (suppliedOrigin is not null)
      context.Request.Headers.Origin = suppliedOrigin;
    return context;
  }

  private static async Task<int> InvokeAsync(DefaultHttpContext context, bool ready = true, LanAccess? access = null) {
    int endpointCalls = 0;
    var gate = new LocalBrowserGate(_ => {
      endpointCalls++;
      context.Response.StatusCode = StatusCodes.Status204NoContent;
      return Task.CompletedTask;
    }, () => ready ? origin : null, access);
    await gate.InvokeAsync(context);
    Assert.Equal(context.Response.StatusCode == StatusCodes.Status204NoContent ? 1 : 0, endpointCalls);
    if (context.Request.Path.StartsWithSegments("/api") && context.Response.StatusCode != StatusCodes.Status204NoContent)
      Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    return context.Response.StatusCode;
  }
}
