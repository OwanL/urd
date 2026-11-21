using System.Net;
using System.Text.Json;

namespace Urd.Server;

internal sealed record LanHostingStatus(bool Enabled, bool Remembered, IReadOnlyList<string> Origins, string? StartupError);

internal static class LanHostingRoutes {
  internal static void MapLanHosting(this IEndpointRouteBuilder app, Func<LanHosting?> hosting, Func<Uri?> origin) {
    app.MapGet("/api/hosting/lan", async (HttpContext http) => {
      if (!IsLocal(http, origin()))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
      return Results.Ok(await hosting()!.StatusAsync());
    });
    app.MapPost("/api/hosting/lan", async (HttpContext http) => {
      if (!IsLocal(http, origin()))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
      bool enabled;
      try {
        using JsonDocument body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        if (body.RootElement.ValueKind != JsonValueKind.Object ||
            !body.RootElement.TryGetProperty("enabled", out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
          return Results.BadRequest(new { error = "Expected a boolean enabled value." });
        enabled = value.GetBoolean();
      } catch (JsonException) {
        return Results.BadRequest(new { error = "Invalid JSON body." });
      }
      LanHosting service = hosting()!;
      try {
        return Results.Ok(await service.ChangeAsync(enabled));
      } catch (Exception failure) {
        await Console.Error.WriteLineAsync($"Could not change LAN hosting: {failure}");
        LanHostingStatus status = await service.StatusAsync();
        return Results.Json(new { error = "Could not change LAN hosting.", status.Enabled, status.Remembered, status.Origins, status.StartupError },
            statusCode: StatusCodes.Status500InternalServerError);
      }
    });
  }

  internal static bool IsLocal(HttpContext http, Uri? origin) {
    IPAddress? peer = http.Connection.RemoteIpAddress;
    if (peer?.IsIPv4MappedToIPv6 == true)
      peer = peer.MapToIPv4();
    if (origin is null || peer is null || !IPAddress.IsLoopback(peer))
      return false;
    string authority = origin.Authority;
    if (!string.Equals(http.Request.Host.Value, authority, StringComparison.Ordinal))
      return false;
    return !http.Request.Headers.TryGetValue("Origin", out var supplied) ||
        (supplied.Count == 1 && string.Equals(supplied[0], origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal));
  }
}
