using System.Net;
using Microsoft.AspNetCore.Http;

namespace Urd.Server;

// Admits local health requests only while the server has a published origin.
internal sealed class LocalBrowserGate(RequestDelegate next, Func<Uri?> servedOrigin) {
  public Task InvokeAsync(HttpContext context) {
    Uri? origin = servedOrigin();
    if (origin is null) {
      context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
      return Task.CompletedTask;
    }

    IPAddress? peer = context.Connection.RemoteIpAddress;
    HostString host = context.Request.Host;
    int? port = host.Value == "127.0.0.1" ? 80 : host.Port;
    if (peer is null || !IPAddress.IsLoopback(peer)
        || !string.Equals(host.Host, "127.0.0.1", StringComparison.Ordinal)
        || port != origin.Port) {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      return Task.CompletedTask;
    }

    if (context.Request.Headers.TryGetValue("Origin", out var suppliedOrigin)
        && (suppliedOrigin.Count != 1
            || !string.Equals(suppliedOrigin[0], origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))) {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      return Task.CompletedTask;
    }

    return next(context);
  }
}
