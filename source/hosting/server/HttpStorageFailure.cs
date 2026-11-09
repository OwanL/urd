namespace Urd.Server;

// Logs storage failures consistently without exposing their details in HTTP responses.
internal static class HttpStorageFailure {
  private static readonly Action<ILogger, Exception?> logFailure = LoggerMessage.Define(
      LogLevel.Error, new EventId(1, nameof(HttpStorageFailure)), "Storage could not complete an HTTP operation.");

  internal static IResult Unavailable(HttpContext http, Exception failure) {
    ILogger logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HttpStorageFailure).FullName!);
    logFailure(logger, failure);
    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
  }
}
