using System.Text.Json;
using Urd.Core.Storage;
using static Urd.Server.HttpRouteHelpers;

namespace Urd.Server;

internal static class SetupRoutes {
  internal static void MapSetup(this IEndpointRouteBuilder app, UrdStore store) {
    RouteGroupBuilder routes = app.MapGroup("/api/setup");
    routes.AddEndpointFilter<JsonRequestFilter>();
    routes.AddEndpointFilter(HandleRequestAsync);
    routes.MapGet("", (Delegate)(() => Results.Ok(Response(store.ReadSetupStatus()))));
    routes.MapPost("/complete", (Delegate)(async (HttpContext http) => {
      await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);
      SetupStatus status = store.CompleteSetup();
      return status.Completed ? Results.Ok(Response(status)) : Results.BadRequest();
    }));
  }

  private static object Response(SetupStatus status) => new {
    required = !status.Completed,
    providerConfigured = status.HasProvider,
    bucketsConfigured = status.HasModelBuckets,
    workingDirectoryConfigured = status.HasWorkingDirectory
  };

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
}
