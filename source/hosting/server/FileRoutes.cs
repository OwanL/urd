using System.Diagnostics;
using System.Text.Json;

namespace Urd.Server;

internal static class FileRoutes {
  internal static RouteGroupBuilder MapFiles(this IEndpointRouteBuilder app) {
    RouteGroupBuilder routes = app.MapGroup("/api/files");
    routes.AddEndpointFilter(HandleRequestAsync);
    routes.MapPost("/reveal", (Delegate)RevealAsync);
    return routes;
  }

  private static async ValueTask<object?> HandleRequestAsync(
      EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) {
    HttpContext http = invocation.HttpContext;
    if (HttpMethods.IsPost(http.Request.Method) && !HttpJson.TryPrepare(http.Request))
      return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

    try {
      return await next(invocation);
    } catch (JsonException) {
      return Results.BadRequest();
    } catch (BadHttpRequestException failure) {
      return Results.StatusCode(failure.StatusCode);
    } catch (ArgumentException) {
      return Results.BadRequest();
    }
  }

  private static async Task<IResult> RevealAsync(HttpContext http) {
    RevealPathRequest? request = await http.Request.ReadFromJsonAsync<RevealPathRequest>(http.RequestAborted);
    if (request?.Path is not { Length: > 0 } path || !System.IO.Path.IsPathFullyQualified(path))
      return Results.BadRequest();
    if (!File.Exists(path) && !Directory.Exists(path))
      return Results.NotFound();
    if (!OperatingSystem.IsWindows())
      return Results.StatusCode(StatusCodes.Status501NotImplemented);

    var start = new ProcessStartInfo {
      FileName = Path.GetFullPath(Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")),
      UseShellExecute = false
    };
    start.ArgumentList.Add($"/select,\"{path}\"");
    using Process? explorer = Process.Start(start);
    return explorer is null ? Results.StatusCode(StatusCodes.Status500InternalServerError) : Results.NoContent();
  }

  private sealed record RevealPathRequest(string? Path);
}
