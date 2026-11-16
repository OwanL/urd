using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Core.Preferences;
using Urd.Core.Sessions;
using static Urd.Server.HttpRouteHelpers;

namespace Urd.Server;

// Exposes simple application preferences through the local browser API.
internal static class PreferencesRoutes {
  internal static void MapPreferences(this IEndpointRouteBuilder app, PreferenceManager preferences, SessionManager sessions) {
    RouteGroupBuilder routes = app.MapGroup("/api/preferences");
    routes.AddEndpointFilter<JsonRequestFilter>();
    routes.AddEndpointFilter(HandleRequestAsync);
    routes.MapGet("", (Delegate)(() => Results.Ok(preferences.Read())));
    routes.MapGet("/resources", (Delegate)(async (HttpContext http) =>
        Results.Ok(await sessions.ReadStartingResourcesAsync(http.RequestAborted))));
    routes.MapPost("/working-directory", (Delegate)((HttpContext http) => SetWorkingDirectoryAsync(http, preferences)));
    routes.MapPost("/compaction-trigger",
        (Delegate)((HttpContext http) => SetCompactionTriggerTokenCountAsync(http, preferences)));
    routes.MapPost("/model-choice", (Delegate)((HttpContext http) => SetModelChoiceAsync(http, preferences)));
    routes.MapPost("/model-bucket", (Delegate)((HttpContext http) => SetBucketModelAsync(http, preferences)));
    routes.MapPost("/protected-resources", (Delegate)((HttpContext http) => SetProtectedItemsAsync(http, preferences)));
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

  private static async Task<IResult> SetModelChoiceAsync(HttpContext http, PreferenceManager preferences) {
    ModelChoiceRequest? body = await http.Request.ReadFromJsonAsync<ModelChoiceRequest>(http.RequestAborted);
    if (body is null)
      return Results.BadRequest();

    preferences.SetModelChoice(body.Purpose, body.Choice);
    return Results.Ok(preferences.Read());
  }

  private static async Task<IResult> SetBucketModelAsync(HttpContext http, PreferenceManager preferences) {
    ModelBucketRequest? body = await http.Request.ReadFromJsonAsync<ModelBucketRequest>(http.RequestAborted);
    if (body is null)
      return Results.BadRequest();

    preferences.SetBucketModel(body.Bucket, body.Model);
    return Results.Ok(preferences.Read());
  }

  private static async Task<IResult> SetProtectedItemsAsync(HttpContext http, PreferenceManager preferences) {
    ProtectedItemsRequest? body = await http.Request.ReadFromJsonAsync<ProtectedItemsRequest>(http.RequestAborted);
    if (body is null)
      return Results.BadRequest();

    preferences.SetProtectedItems(body.Category, body.Items);
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
  private sealed record ModelChoiceRequest(AuxiliaryModelPurpose Purpose, ModelChoice Choice);
  private sealed record ModelBucketRequest(ModelBucket Bucket,
      [property: JsonRequired] ConfiguredModel? Model);
  private sealed record ProtectedItemsRequest(ProtectedResourceCategory Category, string[] Items);
}
