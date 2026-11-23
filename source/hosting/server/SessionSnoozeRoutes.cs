using Urd.Core.Sessions;

namespace Urd.Server;

internal static class SessionSnoozeRoutes {
  internal static void MapSessionSnoozeRoutes(this RouteGroupBuilder routes, SessionManager sessions) {
    routes.MapGet("/snoozed", (Delegate)(async (HttpContext http) =>
        Results.Ok(await sessions.ReadSnoozedAsync(http.RequestAborted))));
    routes.MapPost("/{sessionId}/snooze", async (HttpContext http, Guid sessionId) => {
      SnoozeChange? request = await http.Request.ReadFromJsonAsync<SnoozeChange>(http.RequestAborted);
      if (request is null)
        return Results.BadRequest();

      SessionSnoozeStatus status = await sessions.SetSnoozeAsync(sessionId, request.Until, request.ExpectedUntil, http.RequestAborted);
      return status == SessionSnoozeStatus.Conflict ? Results.Conflict() : Results.NoContent();
    });
  }

  private sealed record SnoozeChange(DateTimeOffset? Until = null, DateTimeOffset? ExpectedUntil = null);
}
