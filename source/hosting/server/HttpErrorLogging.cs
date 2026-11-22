using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Urd.Server;

internal sealed class HttpErrorLogging(RequestDelegate next, ServerErrorLog log, bool lan) {
  public async Task InvokeAsync(HttpContext context) {
    try {
      await next(context);
    } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
      throw;
    } catch (Exception failure) {
      int statusCode = failure is BadHttpRequestException rejected ? rejected.StatusCode : StatusCodes.Status500InternalServerError;
      if (context.Response.HasStarted)
        statusCode = context.Response.StatusCode;
      log.Write("http-exception", new {
        surface = lan ? "lan" : "local",
        route = RoutePattern(context),
        statusCode,
        exceptionType = failure.GetType().Name
      });
      throw;
    }

    if (context.Response.StatusCode >= 400 && !context.RequestAborted.IsCancellationRequested)
      log.Write("http-response-error", new {
        surface = lan ? "lan" : "local",
        route = RoutePattern(context),
        statusCode = context.Response.StatusCode
      });
  }

  private static string? RoutePattern(HttpContext context) =>
      (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
}
