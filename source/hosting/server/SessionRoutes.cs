using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Server;

// Binds session HTTP operations to Core without owning admission or execution policy.
internal static class SessionRoutes {
  private const int MaximumSubmissionBytes = 16 * 1024;
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
    routes.MapPost("/{sessionId}/message-submissions", (HttpContext http, Guid sessionId) =>
        SubmitAsync(http, sessions, sessionId));
    routes.MapGet("/{sessionId}/message-submissions/{submissionId}", (HttpContext http, Guid sessionId, Guid submissionId) =>
        ReadReceiptAsync(http, sessions, sessionId, submissionId));
    routes.MapPost("/{sessionId}/executions/{executionId}/stop", (HttpContext http, Guid sessionId, Guid executionId) =>
        StopAsync(http, sessions, sessionId, executionId));
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

    if (HttpMethods.IsPost(http.Request.Method) && !HttpJson.TryPrepare(http.Request))
      return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

    try {
      return await next(invocation);
    } catch (UserMessageConflictException) {
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
    UserMessageSubmission? submission = await http.Request.ReadFromJsonAsync<UserMessageSubmission>(http.RequestAborted);
    if (submission is null || submission.SubmissionId == Guid.Empty || submission.ExpectedHistoryRevision < 0
        || submission.Text is null)
      return Results.BadRequest();

    if (strictUtf8.GetByteCount(submission.Text) > MaximumSubmissionBytes)
      return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    return await WithSessionAsync(http, sessions, sessionId, async session => {
      // Core retains the decision; a disconnected request never owns accepted work.
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(submission);
      return Results.Ok(receipt);
    });
  }

  private static async Task<IResult> StopAsync(HttpContext http, SessionManager sessions, Guid sessionId, Guid executionId) {
    if (executionId == Guid.Empty)
      return Results.BadRequest();

    JsonElement body = await http.Request.ReadFromJsonAsync<JsonElement>(http.RequestAborted);
    if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any())
      return Results.BadRequest();

    return await WithSessionAsync(http, sessions, sessionId, async session => {
      // Disconnecting cannot undo admitted Stop intent; cleanup remains owned by Core.
      StopReceipt receipt = await session.RequestStopAsync(executionId);
      return receipt.Status switch {
        StopStatus.Requested or StopStatus.Settled => Results.Ok(receipt),
        StopStatus.NotFound => Results.NotFound(),
        StopStatus.Unavailable => Results.Conflict(receipt),
        _ => throw new InvalidOperationException("Unknown Stop status.")
      };
    });
  }

  private static Task<IResult> ReadReceiptAsync(
      HttpContext http, SessionManager sessions, Guid sessionId, Guid submissionId) {
    if (submissionId == Guid.Empty)
      return Task.FromResult<IResult>(Results.BadRequest());

    return WithSessionAsync(http, sessions, sessionId, async session => {
      UserMessageReceipt? receipt = await session.GetUserMessageReceiptAsync(submissionId, http.RequestAborted);
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
    if (query.ExecutionId == Guid.Empty || query.MessageId == Guid.Empty || role is null
        || query.HistoryRevision < 0 || query.SegmentIndex < 0 || query.Utf8Offset < 0)
      return Task.FromResult<IResult>(Results.BadRequest());

    var message = new MessageRef(query.ExecutionId, query.MessageId, role.Value, query.HistoryRevision);
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
      Guid ExecutionId, Guid MessageId, string Role, long HistoryRevision, long SegmentIndex, int Utf8Offset);
}
