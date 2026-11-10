using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Urd.Core.Providers;

namespace Urd.Server;

// Exposes provider management without starting work on offline reads or returning credentials.
internal static class ProviderRoutes {
  internal static void MapProviders(this IEndpointRouteBuilder app, ProviderManager providers) {
    RouteGroupBuilder routes = app.MapGroup("/api/providers");
    routes.AddEndpointFilter(HandleRequestAsync);
    routes.MapGet("", (Delegate)(async (HttpContext http) => Results.Ok(await providers.ListAsync(http.RequestAborted))));
    routes.MapPost("/openai", (Delegate)((HttpContext http) => AddAsync(http, providers.AddOpenAIAsync)));
    routes.MapPost("/chatgpt", (Delegate)((HttpContext http) => AddAsync(http, providers.AddChatGptAsync)));
    routes.MapPost("/custom", async (HttpContext http) => {
      CustomProviderSettings settings = await ReadAsync<CustomProviderSettings>(http);
      ProviderConnection connection = await providers.AddCustomAsync(settings);
      return Results.Created($"/api/providers/{connection.Id:D}", connection);
    });
    routes.MapGet("/{connectionId:guid}", async (HttpContext http, Guid connectionId) =>
        Results.Ok(await providers.GetAsync(connectionId, http.RequestAborted)));
    routes.MapPost("/{connectionId:guid}/rename", async (HttpContext http, Guid connectionId) => {
      NameRequest body = await ReadAsync<NameRequest>(http);
      return Results.Ok(await providers.RenameAsync(connectionId, body.Name));
    });
    routes.MapPost("/{connectionId:guid}/custom-settings", async (HttpContext http, Guid connectionId) =>
        Results.Ok(await providers.UpdateCustomAsync(connectionId, await ReadAsync<CustomProviderSettings>(http))));
    routes.MapPost("/{connectionId:guid}/remove", async (HttpContext http, Guid connectionId) => {
      await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);
      await providers.RemoveAsync(connectionId);
      return Results.NoContent();
    });
    routes.MapPost("/{connectionId:guid}/api-key", async (HttpContext http, Guid connectionId) => {
      ApiKeyRequest body = await ReadAsync<ApiKeyRequest>(http);
      if (body.Connection is null || body.Connection.Id != connectionId)
        return Results.BadRequest();
      return Results.Ok(await providers.SetApiKeyAsync(body.Connection, body.ApiKey));
    });
    routes.MapGet("/{connectionId:guid}/credentials", async (HttpContext http, Guid connectionId) =>
        Results.Ok(await providers.GetCredentialStatusAsync(connectionId, http.RequestAborted)));
    routes.MapPost("/{connectionId:guid}/sign-in", async (HttpContext http, Guid connectionId) => {
      await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);
      return Results.Ok(await providers.BeginSignInAsync(connectionId));
    });
    routes.MapGet("/sign-ins/{signInId:guid}", async (HttpContext http, Guid signInId) =>
        Results.Ok(await providers.GetSignInAsync(signInId, http.RequestAborted)));
    routes.MapPost("/sign-ins/{signInId:guid}/cancel", async (HttpContext http, Guid signInId) => {
      await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);
      return Results.Ok(await providers.CancelSignInAsync(signInId));
    });
    routes.MapPost("/{connectionId:guid}/disconnect", async (HttpContext http, Guid connectionId) => {
      await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);
      return Results.Ok(await providers.DisconnectAsync(connectionId));
    });
    routes.MapGet("/{connectionId:guid}/models", async (HttpContext http, Guid connectionId) =>
        Results.Ok(await providers.GetModelCatalogAsync(connectionId, http.RequestAborted)));
    routes.MapGet("/{connectionId:guid}/model-settings", async (HttpContext http, Guid connectionId) =>
        Results.Ok(await providers.GetModelSettingsAsync(connectionId, http.RequestAborted)));
    routes.MapPost("/{connectionId:guid}/model-settings", async (HttpContext http, Guid connectionId) => {
      ModelSettings settings = await ReadAsync<ModelSettings>(http);
      if (settings.ConnectionId != connectionId)
        return Results.BadRequest();
      return Results.Ok(await providers.SaveModelSettingsAsync(settings));
    });
    routes.MapPost("/{connectionId:guid}/models/refresh", async (HttpContext http, Guid connectionId) => {
      await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);
      return Results.Ok(await providers.RefreshModelCatalogAsync(connectionId));
    });
    routes.MapPost("/{connectionId:guid}/model-tests", async (HttpContext http, Guid connectionId) => {
      ModelTestRequest body = await ReadAsync<ModelTestRequest>(http);
      return Results.Ok(await providers.TestModelAsync(connectionId, body.ModelId, http.RequestAborted));
    });
    app.MapGet("/auth/callback", (Delegate)((HttpContext http) => CompleteSignInAsync(http, providers)));
  }

  private static async Task<IResult> AddAsync(HttpContext http, Func<string, Task<ProviderConnection>> add) {
    NameRequest body = await ReadAsync<NameRequest>(http);
    ProviderConnection connection = await add(body.Name);
    return Results.Created($"/api/providers/{connection.Id:D}", connection);
  }

  private static async Task<T> ReadAsync<T>(HttpContext http) where T : class =>
      await http.Request.ReadFromJsonAsync<T>(http.RequestAborted)
          ?? throw new ArgumentException("A JSON object is required.");

  private static async ValueTask<object?> HandleRequestAsync(
      EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) {
    HttpContext http = invocation.HttpContext;
    if (HttpMethods.IsGet(http.Request.Method) && CanHaveBody(http))
      return Results.BadRequest();
    if (HttpMethods.IsPost(http.Request.Method) && !HttpJson.TryPrepare(http.Request))
      return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    try {
      return await next(invocation);
    } catch (ProviderOperationException failure) {
      int status = failure.Failure switch {
        ProviderFailure.ConnectionChanged or ProviderFailure.Disconnecting or
            ProviderFailure.AuthenticationRequired or ProviderFailure.PlanAccessDenied => StatusCodes.Status409Conflict,
        ProviderFailure.SignInRejected => StatusCodes.Status400BadRequest,
        ProviderFailure.DiscoveryUnsupported => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status502BadGateway
      };
      return Results.Json(new { error = failure.Failure }, statusCode: status);
    } catch (KeyNotFoundException) {
      return Results.NotFound();
    } catch (JsonException) {
      return Results.BadRequest();
    } catch (BadHttpRequestException failure) {
      return Results.StatusCode(failure.StatusCode);
    } catch (ArgumentException) {
      return Results.BadRequest();
    } catch (SqliteException failure) {
      return HttpStorageFailure.Unavailable(http, failure);
    } catch (InvalidDataException failure) {
      return HttpStorageFailure.Unavailable(http, failure);
    } catch (InvalidOperationException) when (http.RequestServices.GetRequiredService<IHostApplicationLifetime>()
        .ApplicationStopping.IsCancellationRequested) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    } catch (OperationCanceledException) {
      return Results.StatusCode(http.RequestAborted.IsCancellationRequested
          ? StatusCodes.Status499ClientClosedRequest : StatusCodes.Status504GatewayTimeout);
    }
  }

  private static async Task<IResult> CompleteSignInAsync(HttpContext http, ProviderManager providers) {
    http.Response.Headers.CacheControl = "no-store";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (CanHaveBody(http) || http.Request.Query.Any(pair => pair.Value.Count != 1)
        || !http.Request.Query.TryGetValue("state", out var state) || string.IsNullOrEmpty(state[0]))
      return Results.BadRequest();
    try {
      ProviderSignIn result = await providers.CompleteSignInAsync(state[0]!, http.Request.GetEncodedUrl());
      return Results.Text(result.Status == ProviderSignInStatus.Completed
          ? "Sign-in completed. You can close this page."
          : "Sign-in did not complete. Check its status through Urd.");
    } catch (ProviderOperationException) {
      return Results.BadRequest();
    } catch (InvalidOperationException) when (http.RequestServices.GetRequiredService<IHostApplicationLifetime>()
        .ApplicationStopping.IsCancellationRequested) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
  }

  private static bool CanHaveBody(HttpContext http) =>
      http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true;

  private sealed record NameRequest(string Name);
  private sealed record ModelTestRequest(string ModelId);
  private sealed record ApiKeyRequest(ProviderConnection Connection, string ApiKey) {
    public override string ToString() => "[API key request]";
  }
}
