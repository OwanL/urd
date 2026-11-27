using System.Text.Json;
using static Urd.Server.HttpRouteHelpers;

namespace Urd.Server;

// Optional mappings that differ across route groups; bad requests are always handled.
[Flags]
internal enum RouteExceptionPolicy {
  None = 0,
  MissingKey = 1,
  StorageAndShutdown = 2,
  ArgumentErrorBody = 4,
  CancellationStatus = 8
}

// Translates known endpoint failures without changing admission or masking unknown exceptions.
internal static class RouteExceptionFilter {
  internal static void AddRouteExceptionFilter(this RouteGroupBuilder routes,
      RouteExceptionPolicy policy = RouteExceptionPolicy.None,
      Func<HttpContext, Exception, IResult?>? mapRouteException = null) =>
      routes.AddEndpointFilter((invocation, next) => HandleAsync(invocation, next, policy, mapRouteException));

  internal static async ValueTask<object?> HandleAsync(EndpointFilterInvocationContext invocation,
      EndpointFilterDelegate next, RouteExceptionPolicy policy,
      Func<HttpContext, Exception, IResult?>? mapRouteException) {
    try {
      return await next(invocation);
    } catch (Exception failure) {
      HttpContext http = invocation.HttpContext;
      // Domain failures take precedence over broader mappings such as shutdown's InvalidOperationException.
      IResult? result = mapRouteException?.Invoke(http, failure) ?? MapCommonException(http, failure, policy);
      if (result is null)
        throw;
      return result;
    }
  }

  private static IResult? MapCommonException(HttpContext http, Exception failure, RouteExceptionPolicy policy) =>
      failure switch {
        JsonException => Results.BadRequest(),
        BadHttpRequestException badRequest => Results.StatusCode(badRequest.StatusCode),
        ArgumentException when policy.HasFlag(RouteExceptionPolicy.ArgumentErrorBody) =>
            Results.Json(new { error = failure.Message }, statusCode: StatusCodes.Status400BadRequest),
        ArgumentException => Results.BadRequest(),
        KeyNotFoundException when policy.HasFlag(RouteExceptionPolicy.MissingKey) => Results.NotFound(),
        _ when policy.HasFlag(RouteExceptionPolicy.StorageAndShutdown) && IsStorageFailure(failure) =>
            HttpStorageFailure.Unavailable(http, failure),
        InvalidOperationException when policy.HasFlag(RouteExceptionPolicy.StorageAndShutdown)
            && IsApplicationStopping(http) => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        OperationCanceledException when policy.HasFlag(RouteExceptionPolicy.CancellationStatus) =>
            Results.StatusCode(http.RequestAborted.IsCancellationRequested
                ? StatusCodes.Status499ClientClosedRequest : StatusCodes.Status504GatewayTimeout),
        _ => null
      };
}
