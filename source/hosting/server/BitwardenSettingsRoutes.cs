using System.Text.Json;

namespace Urd.Server;

internal static class BitwardenSettingsRoutes {
  internal static void MapBitwarden(this IEndpointRouteBuilder app, BitwardenInstallation installation, Func<Uri?> origin) {
    const string path = "/api/browser/bitwarden";
    app.MapGet(path, (HttpContext http) => LanHostingRoutes.IsLocal(http, origin())
        ? Results.Ok(Status(installation))
        : Results.StatusCode(StatusCodes.Status403Forbidden));
    app.MapPut(path, async (HttpContext http) => {
      if (!LanHostingRoutes.IsLocal(http, origin()))
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

      try {
        await installation.ChangeAsync(enabled, http.RequestAborted);
        return Results.Ok(Status(installation));
      } catch (Exception error) when (!http.RequestAborted.IsCancellationRequested && BitwardenInstallation.IsInstallFailure(error)) {
        await Console.Error.WriteLineAsync($"Could not change Bitwarden setting: {error.Message}");
        return Results.Json(new { error = enabled ? "Could not install Bitwarden." : "Could not disable Bitwarden." },
            statusCode: StatusCodes.Status500InternalServerError);
      }
    });
  }

  private static object Status(BitwardenInstallation installation) {
    (bool enabled, bool active, bool installed) = installation.Status();
    return new { enabled, active, installed };
  }
}
