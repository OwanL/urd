using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Net.Http.Headers;
using Urd.Core.Sessions;

namespace Urd.Server;

// Binds session HTTP operations to Core without owning admission or execution policy.
internal static class SessionRoutes {
  private const int MaximumInputBytes = 16 * 1024;
  private static readonly UTF8Encoding strictUtf8 = new(false, true);
  private static readonly Action<ILogger, Exception?> logStorageFailure = LoggerMessage.Define(
      LogLevel.Error, new EventId(1, nameof(StorageUnavailable)), "Session storage could not complete an HTTP operation.");

  internal static RouteGroupBuilder MapSessions(this IEndpointRouteBuilder app, SessionManager sessions) {
    ArgumentNullException.ThrowIfNull(app);
    ArgumentNullException.ThrowIfNull(sessions);
    RouteGroupBuilder routes = app.MapGroup("/api/sessions");
    routes.AddEndpointFilter(HandleRequestAsync);
    routes.MapGet("", (Delegate)((HttpContext http) => ListAsync(http, sessions)));
    routes.MapPost("", (Delegate)((HttpContext http) => CreateAsync(http, sessions)));
    routes.MapPost("/{sessionId}/inputs", (HttpContext http, Guid sessionId) =>
        SubmitAsync(http, sessions, sessionId));
    routes.MapGet("/{sessionId}/inputs/{inputId}", (HttpContext http, Guid sessionId, Guid inputId) =>
        ReadReceiptAsync(http, sessions, sessionId, inputId));
    routes.MapPost("/{sessionId}/runs/{runId}/stop", (HttpContext http, Guid sessionId, Guid runId) =>
        StopAsync(http, sessions, sessionId, runId));
    routes.MapGet("/{sessionId}/snapshot", (HttpContext http, Guid sessionId) =>
        WithSessionAsync(http, sessions, sessionId, async session =>
            Results.Ok(await session.ReadSnapshotAsync(http.RequestAborted))));
    routes.MapGet("/{sessionId}/content", (HttpContext http, Guid sessionId, [AsParameters] ContentQuery query) =>
        ReadContentAsync(http, sessions, sessionId, query));
    return routes;
  }

  private static async ValueTask<object?> HandleRequestAsync(
      EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) {
    HttpContext http = invocation.HttpContext;
    if (HttpMethods.IsGet(http.Request.Method)
        && http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
      return Results.BadRequest();

    if (HttpMethods.IsPost(http.Request.Method) && !TryPrepareJsonRequest(http.Request))
      return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

    try {
      return await next(invocation);
    } catch (InputConflictException) {
      return Results.Conflict();
    } catch (HistoryRevisionConflictException) {
      return Results.Conflict();
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
    }
  }

  private static bool TryPrepareJsonRequest(HttpRequest request) {
    if (!request.HasJsonContentType())
      return false;

    MediaTypeHeaderValue mediaType = MediaTypeHeaderValue.Parse(request.ContentType!);
    if (!mediaType.Charset.HasValue)
      return true;

    try {
      Encoding encoding = Encoding.GetEncoding(HeaderUtilities.RemoveQuotes(mediaType.Charset).Value!);
      // ASP.NET's JSON reader passes charset directly to GetEncoding, including any quotes.
      mediaType.Charset = encoding.WebName;
      request.ContentType = mediaType.ToString();
      return true;
    } catch (ArgumentException) {
      return false;
    } catch (NotSupportedException) {
      return false;
    }
  }

  private static async Task<IResult> ListAsync(HttpContext http, SessionManager sessions) {
    // Preserve a supplied empty cursor so Core can reject it rather than treating it as absent.
    string? cursor = http.Request.Query.TryGetValue("cursor", out var supplied) ? supplied.ToString() : null;
    try {
      return Results.Ok(await sessions.ListPrimaryIdsAsync(cursor, http.RequestAborted));
    } catch (InvalidOperationException) when (IsStopping(http)) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
  }

  private static async Task<IResult> CreateAsync(HttpContext http, SessionManager sessions) {
    JsonElement body = await http.Request.ReadFromJsonAsync<JsonElement>(http.RequestAborted);
    if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any())
      return Results.BadRequest();

    PrimarySession session;
    try {
      // Once Core is called, disconnecting does not cancel creation.
      session = await sessions.CreatePrimaryAsync();
    } catch (InvalidOperationException) when (IsStopping(http)) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Created($"/api/sessions/{session.Id:D}/snapshot", new { sessionId = session.Id });
  }

  private static async Task<IResult> SubmitAsync(HttpContext http, SessionManager sessions, Guid sessionId) {
    InputCommand? command = await http.Request.ReadFromJsonAsync<InputCommand>(http.RequestAborted);
    if (command is null || command.InputId == Guid.Empty || command.ExpectedHistoryRevision < 0
        || command.Text is null)
      return Results.BadRequest();

    if (strictUtf8.GetByteCount(command.Text) > MaximumInputBytes)
      return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    return await WithSessionAsync(http, sessions, sessionId, async session => {
      // Core retains the decision; a disconnected request never owns accepted work.
      InputReceipt receipt = await session.SubmitUserInputAsync(command);
      return Results.Ok(receipt);
    });
  }

  private static async Task<IResult> StopAsync(HttpContext http, SessionManager sessions, Guid sessionId, Guid runId) {
    if (runId == Guid.Empty)
      return Results.BadRequest();

    JsonElement body = await http.Request.ReadFromJsonAsync<JsonElement>(http.RequestAborted);
    if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any())
      return Results.BadRequest();

    return await WithSessionAsync(http, sessions, sessionId, async session => {
      // Disconnecting cannot undo admitted Stop intent; cleanup remains owned by Core.
      StopReceipt receipt = await session.RequestStopAsync(runId);
      return receipt.Status switch {
        StopStatus.Requested or StopStatus.Settled => Results.Ok(receipt),
        StopStatus.NotFound => Results.NotFound(),
        StopStatus.Unavailable => Results.Conflict(receipt),
        _ => throw new InvalidOperationException("Unknown Stop status.")
      };
    });
  }

  private static Task<IResult> ReadReceiptAsync(
      HttpContext http, SessionManager sessions, Guid sessionId, Guid inputId) {
    if (inputId == Guid.Empty)
      return Task.FromResult<IResult>(Results.BadRequest());

    return WithSessionAsync(http, sessions, sessionId, async session => {
      InputReceipt? receipt = await session.GetInputReceiptAsync(inputId, http.RequestAborted);
      // Absence is not a refusal decision, and must be JSON null rather than an empty response.
      return receipt is null ? Results.Content("null", "application/json") : Results.Ok(receipt);
    });
  }

  private static Task<IResult> ReadContentAsync(
      HttpContext http, SessionManager sessions, Guid sessionId, ContentQuery query) {
    MessageRole? role = query.Role switch {
      "user" => MessageRole.User,
      "assistant" => MessageRole.Assistant,
      _ => null
    };
    if (query.RunId == Guid.Empty || query.MessageId == Guid.Empty || role is null
        || query.HistoryRevision < 0 || query.SegmentIndex < 0 || query.Utf8Offset < 0)
      return Task.FromResult<IResult>(Results.BadRequest());

    var message = new MessageRef(query.RunId, query.MessageId, role.Value, query.HistoryRevision);
    var cursor = new MessageContentCursor(message, query.SegmentIndex, query.Utf8Offset);
    return WithSessionAsync(http, sessions, sessionId, async session =>
        Results.Ok(await session.ReadMessageContentAsync(cursor, http.RequestAborted)));
  }

  private static async Task<IResult> WithSessionAsync(HttpContext http, SessionManager sessions,
      Guid sessionId, Func<PrimarySession, Task<IResult>> operation) {
    if (sessionId == Guid.Empty)
      return Results.BadRequest();

    PrimarySession session;
    try {
      session = await sessions.GetPrimaryAsync(sessionId);
    } catch (InvalidOperationException) when (IsStopping(http)) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return await operation(session);
  }

  private static bool IsStopping(HttpContext http)
      => http.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;

  private static IResult StorageUnavailable(HttpContext http, Exception failure) {
    ILogger logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SessionRoutes).FullName!);
    logStorageFailure(logger, failure);
    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
  }

  // Flat, required query members construct Core's segment-local content cursor.
  private sealed record ContentQuery(
      Guid RunId, Guid MessageId, string Role, long HistoryRevision, long SegmentIndex, int Utf8Offset);
}
