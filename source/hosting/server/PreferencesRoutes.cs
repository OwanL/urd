using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Core.Preferences;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using static Urd.Server.HttpRouteHelpers;

namespace Urd.Server;

// Exposes simple application preferences through the local browser API.
internal static class PreferencesRoutes {
  internal static void MapPreferences(this IEndpointRouteBuilder app, PreferenceManager preferences, SessionManager sessions, UrdStore store) {
    RouteGroupBuilder routes = app.MapGroup("/api/preferences");
    routes.AddEndpointFilter<JsonRequestFilter>();
    routes.AddEndpointFilter(HandleRequestAsync);
    routes.MapGet("", (Delegate)(() => Results.Ok(preferences.Read())));
    routes.MapGet("/resources", (Delegate)(async (HttpContext http) =>
        Results.Ok(await sessions.ReadStartingResourcesAsync(http.RequestAborted))));
    routes.MapPost("/working-directory", (Delegate)((HttpContext http) => SetWorkingDirectoryAsync(http, preferences, store)));
    routes.MapPost("/compaction-trigger",
        (Delegate)((HttpContext http) => SetCompactionTriggerTokenCountAsync(http, preferences)));
    routes.MapPost("/model-choice", (Delegate)((HttpContext http) => SetModelChoiceAsync(http, preferences)));
    routes.MapPost("/model-bucket", (Delegate)((HttpContext http) => SetBucketModelsAsync(http, preferences)));
    routes.MapPost("/subagent-depth", (Delegate)((HttpContext http) => SetMaxSubagentDepthAsync(http, preferences)));
    routes.MapPost("/subagent-providers", (Delegate)((HttpContext http) => SetSubagentProvidersAsync(http, preferences)));
    routes.MapPost("/protected-resources", (Delegate)((HttpContext http) => SetProtectedItemsAsync(http, preferences)));
    routes.MapPost("/source-preview", (Delegate)((HttpContext http) => PreviewSourcesAsync(http, sessions)));
    routes.MapPost("/source-ignores", (Delegate)((HttpContext http) => SetSourceIgnoresAsync(http, preferences)));
  }

  private static async Task<IResult> SetWorkingDirectoryAsync(HttpContext http, PreferenceManager preferences, UrdStore store) {
    WorkingDirectoryRequest? body = await http.Request.ReadFromJsonAsync<WorkingDirectoryRequest>(http.RequestAborted);
    if (body is null)
      return Results.BadRequest();

    preferences.SetWorkingDirectory(body.WorkingDirectory);
    if (body.WorkingDirectory is not null)
      store.WarmInstructionResources();
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

  private static async Task<IResult> SetBucketModelsAsync(HttpContext http, PreferenceManager preferences) {
    ModelBucketRequest? body = await http.Request.ReadFromJsonAsync<ModelBucketRequest>(http.RequestAborted);
    if (body is null)
      return Results.BadRequest();

    preferences.SetBucketModels(body.Bucket, body.Models);
    return Results.Ok(preferences.Read());
  }

  private static async Task<IResult> SetMaxSubagentDepthAsync(HttpContext http, PreferenceManager preferences) {
    SubagentDepthRequest? body = await http.Request.ReadFromJsonAsync<SubagentDepthRequest>(http.RequestAborted);
    if (body is null)
      return Results.BadRequest();

    preferences.SetMaxSubagentDepth(body.MaxSubagentDepth);
    return Results.Ok(preferences.Read());
  }

  private static async Task<IResult> SetSubagentProvidersAsync(HttpContext http, PreferenceManager preferences) {
    SubagentProvidersRequest? body = await http.Request.ReadFromJsonAsync<SubagentProvidersRequest>(http.RequestAborted);
    if (body is null)
      return Results.BadRequest();

    preferences.SetSubagentProviders(body.ConnectionIds);
    return Results.Ok(preferences.Read());
  }

  private static async Task<IResult> SetProtectedItemsAsync(HttpContext http, PreferenceManager preferences) {
    ProtectedItemsRequest? body = await http.Request.ReadFromJsonAsync<ProtectedItemsRequest>(http.RequestAborted);
    if (body is null)
      return Results.BadRequest();

    preferences.SetProtectedItems(body.Category, body.Items);
    return Results.Ok(preferences.Read());
  }

  private static async Task<IResult> PreviewSourcesAsync(HttpContext http, SessionManager sessions) {
    SourcePreviewRequest? body = await http.Request.ReadFromJsonAsync<SourcePreviewRequest>(http.RequestAborted);
    if (body?.Rules?.Paths is null || body.Rules.Filenames is null)
      return Results.BadRequest();

    SourceIgnoreRules rules = new() {
      Instructions = body.Category == "instructions" ? body.Rules.Paths : [],
      Skills = body.Category == "skills" ? body.Rules.Paths : [],
      InstructionIncludes = body.Category == "instructions" ? body.Rules.Includes ?? [] : [],
      SkillIncludes = body.Category == "skills" ? body.Rules.Includes ?? [] : [],
      Filenames = body.Rules.Filenames
    };
    SourcePreview preview = await sessions.PreviewSourcesAsync(body.Category!, rules, http.RequestAborted);
    return Results.Ok(new {
      preview.Paths, preview.WorkingDirectory, preview.EligibleCount, preview.EstimatedTokens, preview.Partial,
      Filenames = body.Category == "instructions" ? SourceIgnoreRules.InstructionFilenames : []
    });
  }

  private static async Task<IResult> SetSourceIgnoresAsync(HttpContext http, PreferenceManager preferences) {
    SourceIgnoresRequest? body = await http.Request.ReadFromJsonAsync<SourceIgnoresRequest>(http.RequestAborted);
    if (body?.Paths is null || body.Filenames is null)
      return Results.BadRequest();
    preferences.SetSourceIgnores(body.Category!, body.Paths, body.Filenames, body.Includes);
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
      [property: JsonRequired] ConfiguredModel[] Models);
  private sealed record SubagentDepthRequest(
      [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int MaxSubagentDepth);
  private sealed record SubagentProvidersRequest([property: JsonRequired] Guid[]? ConnectionIds);
  private sealed record ProtectedItemsRequest(ProtectedResourceCategory Category, string[] Items);
  private sealed record SourceRulesRequest(string[]? Paths, string[]? Filenames, string[]? Includes = null);
  private sealed record SourcePreviewRequest(string? Category, SourceRulesRequest? Rules);
  private sealed record SourceIgnoresRequest(string? Category, string[]? Paths, string[]? Filenames, string[]? Includes = null);
}
