using Urd.Core.Storage;

namespace Urd.Server;

internal static class SetupRoutes {
  internal static void MapSetup(this IEndpointRouteBuilder app, UrdStore store) {
    RouteGroupBuilder routes = app.MapGroup("/api/setup");
    routes.AddEndpointFilter<JsonRequestFilter>();
    routes.AddRouteExceptionFilter(RouteExceptionPolicy.StorageAndShutdown
        | RouteExceptionPolicy.ArgumentErrorBody | RouteExceptionPolicy.CancellationStatus);
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
}
