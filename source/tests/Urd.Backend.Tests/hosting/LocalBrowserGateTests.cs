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

  private static async Task<int> InvokeAsync(DefaultHttpContext context, bool ready = true) {
    int endpointCalls = 0;
    var gate = new LocalBrowserGate(_ => {
      endpointCalls++;
      context.Response.StatusCode = StatusCodes.Status204NoContent;
      return Task.CompletedTask;
    }, () => ready ? origin : null);
    await gate.InvokeAsync(context);
    Assert.Equal(context.Response.StatusCode == StatusCodes.Status204NoContent ? 1 : 0, endpointCalls);
    if (context.Request.Path.StartsWithSegments("/api") && context.Response.StatusCode != StatusCodes.Status204NoContent)
      Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    return context.Response.StatusCode;
  }
}
