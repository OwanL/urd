using Microsoft.Data.Sqlite;

namespace Urd.Server;

// Shares shutdown, storage-failure and nullable JSON response rules across routes.
internal static class HttpRouteHelpers {
  internal static bool IsApplicationStopping(HttpContext http) =>
      http.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;

  internal static bool IsStorageFailure(Exception failure) => failure is SqliteException or InvalidDataException;

  // Absence is not a refusal decision, and must be JSON null rather than an empty response.
  internal static IResult JsonNullOrOk(object? value) =>
      value is null ? Results.Content("null", "application/json") : Results.Ok(value);
}
