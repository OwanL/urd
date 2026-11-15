using System.Text.Json;
using Urd.Core.Execution;
using Urd.Core.Compaction;
using Urd.Core.Sessions;
using static Urd.Server.HttpRouteHelpers;

namespace Urd.Server;

// Binds session HTTP operations to Core without owning admission or execution policy.
internal static class SessionRoutes {
  internal static RouteGroupBuilder MapSessions(this IEndpointRouteBuilder app, SessionManager sessions) {
    ArgumentNullException.ThrowIfNull(app);
    ArgumentNullException.ThrowIfNull(sessions);
    RouteGroupBuilder routes = app.MapGroup("/api/sessions");
    routes.AddEndpointFilter<JsonRequestFilter>();
    routes.AddEndpointFilter(HandleRequestAsync);
    routes.MapGet("", (Delegate)((HttpContext http) => ListAsync(http, sessions)));
    routes.MapPost("", (Delegate)((HttpContext http) => CreateAsync(http, sessions)));
    routes.MapPost("/{sessionId}/message-submissions", (HttpContext http, Guid sessionId) =>
        SubmitAsync(http, sessions, sessionId));
    routes.MapGet("/{sessionId}/message-submissions/{submissionId}", (HttpContext http, Guid sessionId, Guid submissionId) =>
        ReadReceiptAsync(http, sessions, sessionId, submissionId));
    routes.MapPost("/{sessionId}/continuations", (HttpContext http, Guid sessionId) =>
        ContinueAsync(http, sessions, sessionId));
    routes.MapGet("/{sessionId}/continuations/{operationId}", (HttpContext http, Guid sessionId, Guid operationId) =>
        ReadContinuationReceiptAsync(http, sessions, sessionId, operationId));
    routes.MapPost("/{sessionId}/executions/{executionId}/pending-inputs/{submissionId}/remove",
        (HttpContext http, Guid sessionId, Guid executionId, Guid submissionId) =>
            RemovePendingInputAsync(http, sessions, sessionId, executionId, submissionId));
    routes.MapPost("/{sessionId}/message-edits", (HttpContext http, Guid sessionId) =>
        EditAsync(http, sessions, sessionId));
    routes.MapPost("/{sessionId}/working-directory", (HttpContext http, Guid sessionId) =>
        SetWorkingDirectoryAsync(http, sessions, sessionId));
    routes.MapGet("/{sessionId}/message-edits/{operationId}", (HttpContext http, Guid sessionId, Guid operationId) =>
        WithSessionAsync(http, sessions, sessionId, async session => {
          MessageEditReceipt? receipt = await session.ReadMessageEditReceiptAsync(operationId, http.RequestAborted);
          return JsonNullOrOk(receipt);
        }));
    routes.MapPost("/{sessionId}/executions/{executionId}/stop", (HttpContext http, Guid sessionId, Guid executionId) =>
        StopAsync(http, sessions, sessionId, executionId));
    routes.MapPost("/{sessionId}/compactions", (HttpContext http, Guid sessionId) => CompactAsync(http, sessions, sessionId));
    routes.MapGet("/{sessionId}/compactions/operations/{operationId}", (HttpContext http, Guid sessionId, Guid operationId) =>
        WithSessionAsync(http, sessions, sessionId, async session => {
          CompactionReceipt? receipt = await session.ReadCompactionReceiptAsync(operationId, http.RequestAborted);
          return JsonNullOrOk(receipt);
        }));
    routes.MapGet("/{sessionId}/compactions/{compactionId}", (HttpContext http, Guid sessionId, Guid compactionId, long expectedHistoryRevision) =>
        WithSessionAsync(http, sessions, sessionId, async session => {
          CompactionSummary? summary = await session.ReadCompactionAsync(compactionId, expectedHistoryRevision, http.RequestAborted);
          return summary is null ? Results.NotFound() : Results.Ok(summary);
        }));
    routes.MapGet("/{sessionId}/snapshot", (HttpContext http, Guid sessionId) =>
        WithSessionAsync(http, sessions, sessionId, async session =>
            Results.Ok(await session.ReadSnapshotAsync(http.RequestAborted))));
    routes.MapGet("/{sessionId}/not-sent", (HttpContext http, Guid sessionId) =>
        WithSessionAsync(http, sessions, sessionId, async session => {
          string? cursor = http.Request.Query.TryGetValue("cursor", out var supplied) ? supplied.ToString() : null;
          return Results.Ok(await session.ReadNotSentInputsAsync(cursor, http.RequestAborted));
        }));
    routes.MapSessionTranscriptRoutes(sessions);
    return routes;
  }

  private static async ValueTask<object?> HandleRequestAsync(
      EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) {
    HttpContext http = invocation.HttpContext;
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
    } catch (Exception failure) when (IsStorageFailure(failure)) {
      return HttpStorageFailure.Unavailable(http, failure);
    }
  }

  private static async Task<IResult> ListAsync(HttpContext http, SessionManager sessions) {
    // Preserve a supplied empty cursor so Core can reject it rather than treating it as absent.
    string? cursor = http.Request.Query.TryGetValue("cursor", out var supplied) ? supplied.ToString() : null;
    try {
      return Results.Ok(await sessions.ListPrimaryIdsAsync(cursor, http.RequestAborted));
    } catch (InvalidOperationException) when (IsApplicationStopping(http)) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
  }

  private static async Task<IResult> CreateAsync(HttpContext http, SessionManager sessions) {
    await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);

    PrimarySession session;
    try {
      // Once Core is called, disconnecting does not cancel creation.
      session = await sessions.CreatePrimaryAsync();
    } catch (InvalidOperationException) when (IsApplicationStopping(http)) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Created($"/api/sessions/{session.Id:D}/snapshot", new { sessionId = session.Id });
  }

  private static async Task<IResult> SubmitAsync(HttpContext http, SessionManager sessions, Guid sessionId) {
    UserMessageSubmission? submission = await http.Request.ReadFromJsonAsync<UserMessageSubmission>(http.RequestAborted);
    if (submission is null || submission.SubmissionId == Guid.Empty || submission.ExpectedHistoryRevision < 0
        || submission.Text is null)
      return Results.BadRequest();

    return await WithSessionAsync(http, sessions, sessionId, async session => {
      // Core retains the decision; a disconnected request never owns accepted work.
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(submission);
      return Results.Ok(receipt);
    });
  }

  private static async Task<IResult> ContinueAsync(HttpContext http, SessionManager sessions, Guid sessionId) {
    ContinuationSubmission? submission = await http.Request.ReadFromJsonAsync<ContinuationSubmission>(http.RequestAborted);
    if (submission is null)
      return Results.BadRequest();

    return await WithSessionAsync(http, sessions, sessionId, async session => {
      ContinuationReceipt? receipt = await session.ContinueAsync(submission);
      return receipt is null ? Results.Conflict() : Results.Ok(receipt);
    });
  }

  private static Task<IResult> ReadContinuationReceiptAsync(
      HttpContext http, SessionManager sessions, Guid sessionId, Guid operationId) {
    if (operationId == Guid.Empty)
      return Task.FromResult<IResult>(Results.BadRequest());

    return WithSessionAsync(http, sessions, sessionId, async session => {
      ContinuationReceipt? receipt = await session.ReadContinuationReceiptAsync(operationId, http.RequestAborted);
      return JsonNullOrOk(receipt);
    });
  }

  private static async Task<IResult> RemovePendingInputAsync(HttpContext http, SessionManager sessions,
      Guid sessionId, Guid executionId, Guid submissionId) {
    if (executionId == Guid.Empty || submissionId == Guid.Empty)
      return Results.BadRequest();
    await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);

    return await WithSessionAsync(http, sessions, sessionId, async session =>
        Results.Ok(await session.RemovePendingInputAsync(executionId, submissionId)));
  }

  private static async Task<IResult> EditAsync(HttpContext http, SessionManager sessions, Guid sessionId) {
    MessageEditSubmission? submission = await http.Request.ReadFromJsonAsync<MessageEditSubmission>(http.RequestAborted);
    if (submission is null)
      return Results.BadRequest();
    return await WithSessionAsync(http, sessions, sessionId, async session => {
      // Core owns Stop, settlement and replacement even if this browser disconnects.
      return Results.Ok(await session.EditUserMessageAsync(submission));
    });
  }

  private static async Task<IResult> SetWorkingDirectoryAsync(HttpContext http, SessionManager sessions, Guid sessionId) {
    WorkingDirectoryRequest? request = await http.Request.ReadFromJsonAsync<WorkingDirectoryRequest>(http.RequestAborted);
    if (request?.WorkingDirectory is null)
      return Results.BadRequest();

    return await WithSessionAsync(http, sessions, sessionId, async session => {
      // Request cancellation no longer owns the admitted durable mutation.
      string? workingDirectory = await session.TryChangeWorkingDirectoryAsync(request.WorkingDirectory);
      return workingDirectory is null ? Results.Conflict() : Results.Ok(new { workingDirectory });
    });
  }

  private static async Task<IResult> CompactAsync(HttpContext http, SessionManager sessions, Guid sessionId) {
    ManualCompactionRequest? submission = await http.Request.ReadFromJsonAsync<ManualCompactionRequest>(http.RequestAborted);
    if (submission is null)
      return Results.BadRequest();
    return await WithSessionAsync(http, sessions, sessionId, async session => {
      CompactionReceipt? receipt = await session.CompactAsync(submission);
      return receipt is null ? Results.Conflict() : Results.Ok(receipt);
    });
  }

  private static async Task<IResult> StopAsync(HttpContext http, SessionManager sessions, Guid sessionId, Guid executionId) {
    if (executionId == Guid.Empty)
      return Results.BadRequest();

    await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);

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
      UserMessageReceipt? receipt = await session.ReadUserMessageReceiptAsync(submissionId, http.RequestAborted);
      return JsonNullOrOk(receipt);
    });
  }

  internal static async Task<IResult> WithSessionAsync(HttpContext http, SessionManager sessions,
      Guid sessionId, Func<PrimarySession, Task<IResult>> operation) {
    if (sessionId == Guid.Empty)
      return Results.BadRequest();

    PrimarySession session;
    try {
      session = await sessions.GetPrimaryAsync(sessionId);
    } catch (InvalidOperationException) when (IsApplicationStopping(http)) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return await operation(session);
  }

  private sealed record WorkingDirectoryRequest(string? WorkingDirectory);
}
