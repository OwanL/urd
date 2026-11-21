using Urd.Core.Sessions;
using static Urd.Server.HttpRouteHelpers;

namespace Urd.Server;

internal static class SessionBrowserSignInRoutes {
  internal static void MapSessionBrowserSignInRoutes(this IEndpointRouteBuilder app, SessionManager sessions, bool enabled) {
    RouteGroupBuilder routes = app.MapGroup("/api/sessions");
    routes.AddEndpointFilter<JsonRequestFilter>();
    routes.AddEndpointFilter(async (invocation, next) => {
      try {
        return await next(invocation);
      } catch (KeyNotFoundException) {
        return Results.NotFound();
      } catch (System.Text.Json.JsonException) {
        return Results.BadRequest();
      } catch (BadHttpRequestException failure) {
        return Results.StatusCode(failure.StatusCode);
      } catch (ArgumentException) {
        return Results.BadRequest();
      } catch (InvalidOperationException) when (IsApplicationStopping(invocation.HttpContext)) {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
      } catch (Exception failure) when (IsStorageFailure(failure)) {
        return HttpStorageFailure.Unavailable(invocation.HttpContext, failure);
      }
    });
    routes.MapGet("/{sessionId}/browser-sign-in", async (HttpContext http, Guid sessionId) => {
      if (sessionId == Guid.Empty)
        return Results.BadRequest();
      return Results.Ok(new { handoff = enabled ? await sessions.ReadBrowserSignInAsync(sessionId, http.RequestAborted) : null });
    });
    routes.MapPost("/{sessionId}/browser-sign-in/{id}/{action}", async (HttpContext http, Guid sessionId, Guid id, string action) => {
      if (sessionId == Guid.Empty || id == Guid.Empty || action is not ("open" or "continue" or "cancel"))
        return Results.BadRequest();
      await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);
      if (!enabled)
        return Results.Conflict();
      return await sessions.ActOnBrowserSignInAsync(sessionId, id, action, http.RequestAborted)
          ? Results.NoContent() : Results.Conflict();
    });
  }
}
