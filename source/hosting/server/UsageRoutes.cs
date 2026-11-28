using Microsoft.AspNetCore.Http.Features;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using static Urd.Server.HttpRouteHelpers;

namespace Urd.Server;

internal static class UsageRoutes {
  internal static void MapUsage(this IEndpointRouteBuilder app, UrdStore store, SessionManager sessions) {
    app.MapGet("/api/usage/prompt-cache", async (HttpContext http) => {
      if (http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        return Results.BadRequest();
      http.Response.Headers.CacheControl = "no-store";
      if (!Guid.TryParse(http.Request.Query["sessionId"].ToString(), out Guid sessionId) || sessionId == Guid.Empty)
        return Results.BadRequest();
      try {
        return Results.Ok(await sessions.ReadPromptCacheAsync(sessionId, http.RequestAborted));
      } catch (ArgumentException) {
        return Results.BadRequest();
      } catch (Exception failure) when (IsStorageFailure(failure)) {
        return HttpStorageFailure.Unavailable(http, failure);
      }
    });

    app.MapGet("/api/usage", (HttpContext http, Guid? sessionId, DateTimeOffset? from,
        DateTimeOffset? to, bool? detail) => {
      if (http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        return Results.BadRequest();
      http.Response.Headers.CacheControl = "no-store";
      try {
        return Results.Ok(store.ReadUsage(sessionId, from, to, detail ?? true));
      } catch (ArgumentException) {
        return Results.BadRequest();
      } catch (Exception failure) when (IsStorageFailure(failure)) {
        return HttpStorageFailure.Unavailable(http, failure);
      }
    });
  }
}
