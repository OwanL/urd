using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Server;

// Binds session HTTP operations to Core without owning admission or execution policy.
internal static class SessionRoutes {
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
    routes.MapGet("/{sessionId}/not-sent", (HttpContext http, Guid sessionId) =>
        WithSessionAsync(http, sessions, sessionId, async session => {
          string? cursor = http.Request.Query.TryGetValue("cursor", out var supplied) ? supplied.ToString() : null;
          return Results.Ok(await session.ReadNotSentInputsAsync(cursor, http.RequestAborted));
        }));
    routes.MapGet("/{sessionId}/history", (HttpContext http, Guid sessionId, [AsParameters] HistoryQuery query) =>
        ReadHistoryAsync(http, sessions, sessionId, query));
    routes.MapGet("/{sessionId}/positions", (HttpContext http, Guid sessionId,
        [AsParameters] PositionSummaryQuery query) => ReadPositionSummaryAsync(http, sessions, sessionId, query));
    routes.MapGet("/{sessionId}/positions/locate", (HttpContext http, Guid sessionId,
        [AsParameters] PositionLocateQuery query) => LocatePositionAsync(http, sessions, sessionId, query));
    routes.MapGet("/{sessionId}/positions/prompts", (HttpContext http, Guid sessionId,
        [AsParameters] PromptPageQuery query) => ReadPromptPageAsync(http, sessions, sessionId, query));
    routes.MapGet("/{sessionId}/positions/prompts/{executionId}/{entryId}", (HttpContext http, Guid sessionId,
        Guid executionId, Guid entryId, [AsParameters] PromptActivationQuery query) =>
        ActivatePromptAsync(http, sessions, sessionId, executionId, entryId, query));
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
      return HttpStorageFailure.Unavailable(http, failure);
    } catch (InvalidDataException failure) {
      return HttpStorageFailure.Unavailable(http, failure);
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
    await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);

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

    return await WithSessionAsync(http, sessions, sessionId, async session => {
      // Core retains the decision; a disconnected request never owns accepted work.
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(submission);
      return Results.Ok(receipt);
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
      UserMessageReceipt? receipt = await session.GetUserMessageReceiptAsync(submissionId, http.RequestAborted);
      // Absence is not a refusal decision, and must be JSON null rather than an empty response.
      return receipt is null ? Results.Content("null", "application/json") : Results.Ok(receipt);
    });
  }

  private static Task<IResult> ReadHistoryAsync(
      HttpContext http, SessionManager sessions, Guid sessionId, HistoryQuery query) {
    TranscriptHistoryMode? mode = query.Mode switch {
      "latest" => TranscriptHistoryMode.Latest,
      "before" => TranscriptHistoryMode.Before,
      "after" => TranscriptHistoryMode.After,
      "around" => TranscriptHistoryMode.Around,
      _ => null
    };
    TranscriptEntryKind? kind = query.Kind switch {
      "user" => TranscriptEntryKind.User,
      "text" => TranscriptEntryKind.Text,
      "tool" => TranscriptEntryKind.Tool,
      _ => null
    };
    if (mode is null || query.ExpectedHistoryRevision < 0)
      return Task.FromResult<IResult>(Results.BadRequest());
    bool hasAnchor = query.ExecutionId is not null || query.EntryId is not null || query.Kind is not null;
    if ((mode == TranscriptHistoryMode.Latest && hasAnchor)
        || (mode != TranscriptHistoryMode.Latest && (query.ExecutionId is null || query.EntryId is null || kind is null)))
      return Task.FromResult<IResult>(Results.BadRequest());
    TranscriptEntryRef? anchor = hasAnchor
        ? new(query.ExecutionId!.Value, query.EntryId!.Value, kind!.Value, query.ExpectedHistoryRevision) : null;
    var history = new TranscriptHistoryQuery(mode.Value, query.ExpectedHistoryRevision, anchor);
    return WithSessionAsync(http, sessions, sessionId, async session =>
        Results.Ok(await session.ReadHistoryAsync(history, http.RequestAborted)));
  }

  private static Task<IResult> ReadPositionSummaryAsync(
      HttpContext http, SessionManager sessions, Guid sessionId, PositionSummaryQuery query) {
    if (query.ExpectedHistoryRevision < 0)
      return Task.FromResult<IResult>(Results.BadRequest());

    return WithSessionAsync(http, sessions, sessionId, async session => {
      TranscriptPositionState state = await session.ReadTranscriptPositionSummaryAsync(
          query.ExpectedHistoryRevision, http.RequestAborted);
      return Results.Ok(ToPositionSummary(state));
    });
  }

  private static Task<IResult> LocatePositionAsync(
      HttpContext http, SessionManager sessions, Guid sessionId, PositionLocateQuery query) {
    if (query.ExpectedHistoryRevision < 0 || query.Fraction is not double fraction
        || !double.IsFinite(fraction) || fraction is < 0 or > 1)
      return Task.FromResult<IResult>(Results.BadRequest());

    return WithSessionAsync(http, sessions, sessionId, async session => {
      TranscriptPositionWindow window = await session.LocateTranscriptPositionAsync(
          query.ExpectedHistoryRevision, fraction, http.RequestAborted);
      return Results.Ok(ToPositionWindow(window));
    });
  }

  private static Task<IResult> ReadPromptPageAsync(
      HttpContext http, SessionManager sessions, Guid sessionId, PromptPageQuery query) {
    int limit = query.Limit ?? 60;
    bool missingCursorValue = http.Request.Query.ContainsKey("afterRank") && query.AfterRank is null;
    bool missingLimitValue = http.Request.Query.ContainsKey("limit") && query.Limit is null;
    if (query.ExpectedHistoryRevision < 0 || missingCursorValue || missingLimitValue
        || query.AfterRank is < 0 || limit is < 1 or > 60)
      return Task.FromResult<IResult>(Results.BadRequest());

    return WithSessionAsync(http, sessions, sessionId, async session => {
      TranscriptPromptPage page = await session.ReadTranscriptPromptPageAsync(
          query.ExpectedHistoryRevision, query.AfterRank, limit, http.RequestAborted);
      return Results.Ok(new PromptPageDto(ToPositionSummary(page.State),
          page.Prompts.Select(ToPosition).ToArray(), page.NextAfterRank));
    });
  }

  private static Task<IResult> ActivatePromptAsync(HttpContext http, SessionManager sessions, Guid sessionId,
      Guid executionId, Guid entryId, PromptActivationQuery query) {
    if (executionId == Guid.Empty || entryId == Guid.Empty || query.ExpectedHistoryRevision < 0)
      return Task.FromResult<IResult>(Results.BadRequest());
    var prompt = new TranscriptEntryRef(executionId, entryId, TranscriptEntryKind.User, query.ExpectedHistoryRevision);

    return WithSessionAsync(http, sessions, sessionId, async session => {
      TranscriptPositionWindow window = await session.ActivateTranscriptPromptAsync(
          query.ExpectedHistoryRevision, prompt, http.RequestAborted);
      return Results.Ok(ToPositionWindow(window));
    });
  }

  private static PositionSummaryDto ToPositionSummary(TranscriptPositionState state) => new(
      state.SessionId, state.HistoryRevision, state.PositionVersion, state.EntryCount, state.TotalWeight);

  private static TranscriptPositionDto ToPosition(TranscriptPosition position) => new(
      position.Entry.ExecutionId, position.Entry.EntryId, position.Entry.Kind,
      position.Entry.HistoryRevision, position.Rank, position.Weight, position.PrefixWeight);

  private static PositionWindowDto ToPositionWindow(TranscriptPositionWindow window) => new(
      ToPositionSummary(window.State), window.Target is null ? null : ToPosition(window.Target),
      window.Entries.Select(entry => new PositionedEntryDto(entry.Descriptor, ToPosition(entry.Position))).ToArray(),
      window.HasBefore, window.HasAfter, window.Settlements);

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

  private sealed record HistoryQuery(
      string Mode, long ExpectedHistoryRevision, Guid? ExecutionId, Guid? EntryId, string? Kind);

  private sealed record PositionSummaryQuery(long ExpectedHistoryRevision);
  private sealed record PositionLocateQuery(long ExpectedHistoryRevision, double? Fraction);
  private sealed record PromptPageQuery(
      long ExpectedHistoryRevision, long? AfterRank, int? Limit);
  private sealed record PromptActivationQuery(long ExpectedHistoryRevision);

  private sealed record PositionSummaryDto(
      Guid SessionId, long HistoryRevision, long PositionVersion, long EntryCount, long TotalWeight);

  private sealed record TranscriptPositionDto(
      Guid ExecutionId, Guid EntryId, TranscriptEntryKind Kind, long HistoryRevision,
      long Rank, long Weight, long PrefixWeight);

  private sealed record PositionedEntryDto(TranscriptEntryDescriptor Descriptor, TranscriptPositionDto Position);

  private sealed record PositionWindowDto(
      PositionSummaryDto State, TranscriptPositionDto? Target, IReadOnlyList<PositionedEntryDto> Entries,
      bool HasBefore, bool HasAfter, IReadOnlyList<SessionExecutionResult> Settlements);

  private sealed record PromptPageDto(
      PositionSummaryDto State, IReadOnlyList<TranscriptPositionDto> Prompts, long? NextAfterRank);

  // Flat, required query members construct Core's segment-local content cursor.
  private sealed record ContentQuery(
      Guid ExecutionId, Guid MessageId, string Role, long HistoryRevision, long SegmentIndex, int Utf8Offset);
}
