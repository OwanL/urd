using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Core.Preferences;
using static Urd.Server.HttpRouteHelpers;

namespace Urd.Server;

// Exposes simple application preferences through the local browser API.
internal static class PreferencesRoutes {
  internal static void MapPreferences(this IEndpointRouteBuilder app, PreferenceManager preferences) {
    RouteGroupBuilder routes = app.MapGroup("/api/preferences");
    routes.AddEndpointFilter<JsonRequestFilter>();
    routes.AddEndpointFilter(HandleRequestAsync);
    routes.MapGet("", (Delegate)(() => Results.Ok(preferences.Read())));
    routes.MapPost("/working-directory", (Delegate)((HttpContext http) => SetWorkingDirectoryAsync(http, preferences)));
    routes.MapPost("/compaction-trigger",
        (Delegate)((HttpContext http) => SetCompactionTriggerTokenCountAsync(http, preferences)));
  }

  private static async Task<IResult> SetWorkingDirectoryAsync(HttpContext http, PreferenceManager preferences) {
    WorkingDirectoryRequest? body = await http.Request.ReadFromJsonAsync<WorkingDirectoryRequest>(http.RequestAborted);
    if (body is null)
      return Results.BadRequest();

    preferences.SetWorkingDirectory(body.WorkingDirectory);
    return Results.Ok(preferences.Read());
  }

  private static async Task<IResult> SetCompactionTriggerTokenCountAsync(
      HttpContext http, PreferenceManager preferences) {
    CompactionTriggerRequest? body = await http.Request.ReadFromJsonAsync<CompactionTriggerRequest>(http.RequestAborted);
    if (body is null) return Results.BadRequest();

    preferences.SetCompactionTriggerTokenCount(body.CompactionTriggerTokenCount);
    return Results.Ok(preferences.Read());
  }

  private static async ValueTask<object?> HandleRequestAsync(
      EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) {
    HttpContext http = invocation.HttpContext;
    try {
      return await next(invocation);
    } catch (JsonException) {
      return Results.BadRequest();
    } catch (BadHttpRequestException failure) {
      return Results.StatusCode(failure.StatusCode);
    } catch (ArgumentException failure) {
      return Results.Json(new { error = failure.Message }, statusCode: StatusCodes.Status400BadRequest);
    } catch (Exception failure) when (IsStorageFailure(failure)) {
      return HttpStorageFailure.Unavailable(http, failure);
    } catch (InvalidOperationException) when (IsApplicationStopping(http)) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    } catch (OperationCanceledException) {
      return Results.StatusCode(http.RequestAborted.IsCancellationRequested
          ? StatusCodes.Status499ClientClosedRequest : StatusCodes.Status504GatewayTimeout);
    }
  }

  private sealed record WorkingDirectoryRequest(string? WorkingDirectory);
  private sealed record CompactionTriggerRequest(
      [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int? CompactionTriggerTokenCount);
}
