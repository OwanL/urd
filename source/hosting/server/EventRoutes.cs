using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Server;

// Streams bounded session notices without moving writes or request ownership into Core.
internal static class EventRoutes {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
  private static readonly Action<ILogger, Exception?> logStorageFailure = LoggerMessage.Define(
      LogLevel.Error, new EventId(1, "SessionEventStorageUnavailable"), "Session event registration could not read session state.");

  internal static RouteGroupBuilder MapEvents(this IEndpointRouteBuilder app,
      SessionManager sessions, SessionEventHub hub) {
    RouteGroupBuilder routes = app.MapGroup("/api/events");
    routes.AddEndpointFilter(HandleRequestAsync);
    routes.MapGet("", (Delegate)((HttpContext http) => StreamAsync(http, hub)));
    routes.MapPost("/{streamId:guid}", (HttpContext http, Guid streamId) => UpdateAsync(http, sessions, hub, streamId));
    return routes;
  }

  private static async ValueTask<object?> HandleRequestAsync(
      EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) {
    HttpContext http = invocation.HttpContext;
    if (HttpMethods.IsGet(http.Request.Method)
        && http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
      return Results.BadRequest();
    if (HttpMethods.IsPost(http.Request.Method) && !HttpJson.TryPrepare(http.Request))
      return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

    try {
      return await next(invocation);
    } catch (KeyNotFoundException) {
      return Results.NotFound();
    } catch (JsonException) {
      return Results.BadRequest();
    } catch (BadHttpRequestException failure) {
      return Results.StatusCode(failure.StatusCode);
    } catch (ArgumentException) {
      return Results.BadRequest();
    } catch (SqliteException failure) {
      return StorageUnavailable(http, failure);
    } catch (InvalidDataException failure) {
      return StorageUnavailable(http, failure);
    } catch (InvalidOperationException) when (
        http.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
  }

  private static async Task<IResult> StreamAsync(HttpContext http, SessionEventHub hub) {
    if (!hub.TryCreate(out SessionEventPage page))
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    try {
      http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
      http.Response.StatusCode = StatusCodes.Status200OK;
      http.Response.ContentType = "text/event-stream; charset=utf-8";
      http.Response.Headers.CacheControl = "no-cache, no-store, no-transform";
      http.Response.Headers.ContentEncoding = "identity";
      http.Response.Headers["X-Accel-Buffering"] = "no";
      http.Response.Headers["X-Content-Type-Options"] = "nosniff";
      await WriteEventAsync(http, "connected", new { stream = page.Id, boot = page.Boot });

      while (!http.RequestAborted.IsCancellationRequested) {
        SessionSseRead next = await page.ReadNextAsync(http.RequestAborted);
        if (next.Closed)
          break;
        if (next.Heartbeat) {
          await http.Response.WriteAsync(": heartbeat\n\n", http.RequestAborted);
          await http.Response.Body.FlushAsync(http.RequestAborted);
        } else if (next.Event is { } message) {
          await WriteEventAsync(http, message.Name, message.Data);
        }
      }
    } catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) {
      // A disconnected page releases its bounded registrations below.
    } catch (IOException) {
      // The peer or an intermediary closed the streaming response.
    } finally {
      page.Dispose();
    }

    return Results.Empty;
  }

  private static async Task<IResult> UpdateAsync(HttpContext http, SessionManager sessions,
      SessionEventHub hub, Guid streamId) {
    if (streamId == Guid.Empty)
      return Results.BadRequest();
    SessionSetUpdate? update = await http.Request.ReadFromJsonAsync<SessionSetUpdate>(http.RequestAborted);
    if (update?.Open is null || update.Watched is null
        || update.Open.Length > SessionEventQueue.MaxOpenSessions
        || update.Watched.Length > SessionEventQueue.MaxOpenSessions
        || update.Open.Any(id => id == Guid.Empty) || update.Watched.Any(id => id == Guid.Empty)
        || update.Open.Distinct().Count() != update.Open.Length
        || update.Watched.Distinct().Count() != update.Watched.Length
        || update.Watched.Except(update.Open).Any())
      return Results.BadRequest();
    if (!hub.TryGet(streamId, out SessionEventPage page))
      return Results.NotFound();

    await page.UpdateAsync(sessions, update.Open, update.Watched, http.RequestAborted);
    return Results.NoContent();
  }

  private static async Task WriteEventAsync(HttpContext http, string name, object data) {
    string payload = JsonSerializer.Serialize(data, json);
    await http.Response.WriteAsync($"event: {name}\ndata: {payload}\n\n", http.RequestAborted);
    await http.Response.Body.FlushAsync(http.RequestAborted);
  }

  private static IResult StorageUnavailable(HttpContext http, Exception failure) {
    ILogger logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EventRoutes).FullName!);
    logStorageFailure(logger, failure);
    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
  }

  private sealed record SessionSetUpdate(Guid[]? Open, Guid[]? Watched);
}
