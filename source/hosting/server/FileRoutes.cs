using System.Diagnostics;
using System.Security;
using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace Urd.Server;

internal static class FileRoutes {
  internal static RouteGroupBuilder MapFiles(this IEndpointRouteBuilder app) {
    RouteGroupBuilder routes = app.MapGroup("/api/files");
    routes.AddEndpointFilter<JsonRequestFilter>();
    routes.AddEndpointFilter(HandleRequestAsync);
    routes.MapGet("/directories", (Delegate)ListDirectories);
    routes.MapPost("/reveal", (Delegate)RevealAsync);
    return routes;
  }

  private static async ValueTask<object?> HandleRequestAsync(
      EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) {
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

  private static IResult ListDirectories(HttpContext http) {
    if (!TryReadQueryValue(http.Request.Query, "path", out string? path)
        || !TryReadQueryValue(http.Request.Query, "filter", out string? filter))
      return Error(StatusCodes.Status400BadRequest, "Query parameters must not be repeated.");
    if (path is not null && !System.IO.Path.IsPathFullyQualified(path))
      return Error(StatusCodes.Status400BadRequest, "Path must be an absolute directory path.");
    if (filter is { Length: > 256 })
      return Error(StatusCodes.Status400BadRequest, "Filter is too long.");

    try {
      DirectoryListing listing = path is null
          ? DirectoryListing.Roots(filter, http.RequestAborted)
          : DirectoryListing.ForPath(path, filter, http.RequestAborted);
      return Results.Ok(listing);
    } catch (UnauthorizedAccessException) {
      return Error(StatusCodes.Status403Forbidden, "Access to the directory was denied.");
    } catch (SecurityException) {
      return Error(StatusCodes.Status403Forbidden, "Access to the directory was denied.");
    } catch (DirectoryNotFoundException) {
      return Error(StatusCodes.Status404NotFound, "Directory was not found.");
    } catch (FileNotFoundException) {
      return Error(StatusCodes.Status404NotFound, "Directory was not found.");
    } catch (PathTooLongException) {
      return Error(StatusCodes.Status400BadRequest, "Path is too long.");
    } catch (ArgumentException) {
      return Error(StatusCodes.Status400BadRequest, "Path is invalid.");
    } catch (NotSupportedException) {
      return Error(StatusCodes.Status400BadRequest, "Path is invalid.");
    } catch (IOException) {
      return Error(StatusCodes.Status503ServiceUnavailable, "Directory could not be read.");
    }
  }

  private static bool TryReadQueryValue(IQueryCollection query, string name, out string? value) {
    value = null;
    if (!query.TryGetValue(name, out StringValues values))
      return true;
    if (values.Count != 1)
      return false;
    value = values[0];
    return true;
  }

  private static IResult Error(int statusCode, string message) =>
      Results.Json(new { error = message }, statusCode: statusCode);

  private static async Task<IResult> RevealAsync(HttpContext http) {
    RevealPathRequest? request = await http.Request.ReadFromJsonAsync<RevealPathRequest>(http.RequestAborted);
    if (request?.Path is not { Length: > 0 } path || !System.IO.Path.IsPathFullyQualified(path))
      return Results.BadRequest();
    if (!File.Exists(path) && !Directory.Exists(path))
      return Results.NotFound();
    if (!OperatingSystem.IsWindows())
      return Results.StatusCode(StatusCodes.Status501NotImplemented);

    using Process? explorer = Process.Start(ExplorerSelection(path));
    return explorer is null ? Results.StatusCode(StatusCodes.Status500InternalServerError) : Results.NoContent();
  }

  internal static ProcessStartInfo ExplorerSelection(string path) => new() {
    FileName = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")),
    // Explorer parses its own command line; ArgumentList would backslash-escape these quotes.
    Arguments = $"/select,\"{path}\"",
    UseShellExecute = false
  };

  private sealed record RevealPathRequest(string? Path);
}
