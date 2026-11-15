using Microsoft.AspNetCore.Http.Features;
using Urd.Core.Storage;
using static Urd.Server.HttpRouteHelpers;

namespace Urd.Server;

internal static class UsageRoutes {
  internal static void MapUsage(this IEndpointRouteBuilder app, UrdStore store) {
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
