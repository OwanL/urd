using System.Net;
using Microsoft.AspNetCore.Http;

namespace Urd.Server;

// Checks the local browser boundary before private routes read state or admit work.
internal sealed class LocalBrowserGate(RequestDelegate next, Func<Uri?> servedOrigin, LanAccess? lanAccess = null) {
  public Task InvokeAsync(HttpContext context) {
    bool privateApi = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
    bool requiresOrigin = context.Request.Path.StartsWithSegments("/browser-view", StringComparison.OrdinalIgnoreCase);
    if (privateApi || context.Request.Path.Equals("/auth/callback", StringComparison.OrdinalIgnoreCase))
      context.Response.Headers.CacheControl = "no-store";

    Uri? origin = servedOrigin();
    if (origin is null) {
      context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
      return Task.CompletedTask;
    }

    IPAddress? peer = context.Connection.RemoteIpAddress;
    if (peer?.IsIPv4MappedToIPv6 == true)
      peer = peer.MapToIPv4();

    HostString host = context.Request.Host;
    int? port = host.Port ?? (host.Value == host.Host ? 80 : null);
    string? expectedOrigin = null;
    if (peer is not null && port == origin.Port) {
      bool loopbackPeer = IPAddress.IsLoopback(peer);
      if (loopbackPeer && string.Equals(host.Host, "127.0.0.1", StringComparison.Ordinal)) {
        expectedOrigin = origin.GetLeftPart(UriPartial.Authority);
      } else if (loopbackPeer || LanAccess.IsPrivateIPv4(peer)) {
        LanAccessSnapshot? snapshot = lanAccess?.Snapshot;
        if (snapshot is { Enabled: true } && snapshot.Addresses.Contains(host.Host, StringComparer.Ordinal))
          expectedOrigin = LanAccess.Origin(host.Host, origin.Port);
      }
    }

    bool hasOrigin = context.Request.Headers.TryGetValue("Origin", out var suppliedOrigin);
    if (expectedOrigin is null || (requiresOrigin && !hasOrigin)
        || (hasOrigin && (suppliedOrigin.Count != 1
            || !string.Equals(suppliedOrigin[0], expectedOrigin, StringComparison.Ordinal)))) {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      return Task.CompletedTask;
    }

    if (privateApi && (!context.Request.Headers.TryGetValue("X-Urd-Client", out var marker)
        || marker.Count != 1 || !string.Equals(marker[0], "browser", StringComparison.Ordinal))) {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      return Task.CompletedTask;
    }

    return next(context);
  }
}
