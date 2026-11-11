using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Urd.Core.MarkdownRendering;
using Urd.Core.Sessions;

namespace Urd.Server;

// Adapts page-owned Markdown generations to bounded HTTP reads.
internal static class MarkdownRoutes {
  internal static RouteGroupBuilder MapMarkdown(this IEndpointRouteBuilder app,
      SessionManager sessions, SessionEventHub hub, MessageMarkdownService markdown) {
    ArgumentNullException.ThrowIfNull(app);
    ArgumentNullException.ThrowIfNull(sessions);
    ArgumentNullException.ThrowIfNull(hub);
    ArgumentNullException.ThrowIfNull(markdown);

    RouteGroupBuilder routes = app.MapGroup("/api/events/{streamId:guid}/markdown/{leaseId:guid}");
    routes.AddEndpointFilter(HandleRequestAsync);
    routes.MapPost("", (HttpContext http, Guid streamId, Guid leaseId) =>
        OpenAsync(http, sessions, hub, markdown, streamId, leaseId));
    routes.MapGet("/pieces", (HttpContext http, Guid streamId, Guid leaseId,
        [AsParameters] CursorQuery query) => ReadPiecesAsync(http, hub, streamId, leaseId, query));
    routes.MapGet("/pieces/before", (HttpContext http, Guid streamId, Guid leaseId,
        [AsParameters] CursorQuery query) => ReadPiecesBeforeAsync(http, hub, streamId, leaseId, query));
    routes.MapGet("/contexts/{id:int}", (HttpContext http, Guid streamId, Guid leaseId, int id,
        [AsParameters] CursorQuery query) => ReadContextAsync(http, hub, streamId, leaseId, id, query));
    routes.MapGet("/contexts/{id:int}/children/{childIndex:int}", (HttpContext http,
        Guid streamId, Guid leaseId, int id, int childIndex) =>
        LocateChildAsync(http, hub, streamId, leaseId, id, childIndex));
    routes.MapPost("/contexts", (HttpContext http, Guid streamId, Guid leaseId) =>
        ReadContextsAsync(http, hub, streamId, leaseId));
    routes.MapGet("/locate", (HttpContext http, Guid streamId, Guid leaseId,
        [AsParameters] LocateQuery query) => LocateAsync(http, hub, streamId, leaseId, query));
    routes.MapPost("/locate", (HttpContext http, Guid streamId, Guid leaseId) =>
        LocateSourceAsync(http, hub, streamId, leaseId));
    routes.MapGet("/source", (HttpContext http, Guid streamId, Guid leaseId,
        [AsParameters] LocateQuery query) => ReadSourceCursor(http, hub, streamId, leaseId, query));
    routes.MapPost("/release", (HttpContext http, Guid streamId, Guid leaseId) =>
        ReleaseAsync(http, hub, streamId, leaseId));
    return routes;
  }

  internal static async ValueTask<object?> HandleRequestAsync(
      EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) {
    HttpContext http = invocation.HttpContext;
    if (HttpMethods.IsGet(http.Request.Method)
        && http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
      return Results.BadRequest();
    if (HttpMethods.IsPost(http.Request.Method) && !HttpJson.TryPrepare(http.Request))
      return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

    try {
      return await next(invocation);
    } catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested) {
      return Results.StatusCode(IsStopping(http)
          ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status410Gone);
    } catch (MarkdownPageLeases.DuplicateTokenException) {
      return Results.Conflict();
    } catch (HistoryRevisionConflictException) {
      return Results.Conflict();
    } catch (MarkdownSupersededException) {
      return Results.Conflict();
    } catch (MarkdownUnavailableException) when (IsStopping(http)) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    } catch (MarkdownUnavailableException) {
      return Results.StatusCode(StatusCodes.Status410Gone);
    } catch (MarkdownCacheFullException) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    } catch (MarkdownQueueFullException) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    } catch (MarkdownTooLargeException) {
      return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
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
    } catch (InvalidOperationException) when (IsStopping(http)) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
  }

  private static async Task<IResult> OpenAsync(HttpContext http, SessionManager sessions,
      SessionEventHub hub, MessageMarkdownService markdown, Guid streamId, Guid leaseId) {
    if (streamId == Guid.Empty || leaseId == Guid.Empty)
      return Results.BadRequest();

    OpenRequest? request = await http.Request.ReadFromJsonAsync<OpenRequest>(http.RequestAborted);
    TranscriptEntryRef? entry = request?.Entry;
    if (request is null || request.SessionId == Guid.Empty || entry is null
        || entry.ExecutionId == Guid.Empty || entry.EntryId == Guid.Empty || entry.HistoryRevision < 0
        || !Enum.IsDefined(entry.Kind) || entry.Kind is not (TranscriptEntryKind.User or TranscriptEntryKind.Text))
      return Results.BadRequest();
    if (!TryGetPage(hub, streamId, out SessionEventPage page))
      return MissingPageResult(http);

    PrimarySession session;
    try {
      session = await sessions.GetPrimaryAsync(request.SessionId);
    } catch (InvalidOperationException) when (IsStopping(http)) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    MessageMarkdownLease lease = await page.MarkdownLeases.OpenAsync(leaseId,
        cancellation => markdown.OpenAsync(session, entry, cancellation), http.RequestAborted);
    // Source facts use the server serializer's string enum contract; only render pages
    // use the Core options whose numeric enum widths are included in byte accounting.
    IResult response = Results.Ok(new { manifest = lease.Manifest, source = lease.Source });
    return new OwnedOpenResult(page, leaseId, response);
  }

  private static Task<IResult> ReadPiecesAsync(HttpContext http, SessionEventHub hub,
      Guid streamId, Guid leaseId, CursorQuery query) {
    if (!TryReadCursor(streamId, leaseId, query, out MarkdownRenderCursor cursor, out IResult? invalid))
      return Task.FromResult(invalid!);
    if (!TryGetPage(hub, streamId, out SessionEventPage page))
      return Task.FromResult(MissingPageResult(http));

    MarkdownPiecePage result = page.MarkdownLeases.Read(leaseId, lease => lease.ReadPieces(cursor));
    return Task.FromResult<IResult>(RenderJson(result));
  }

  private static Task<IResult> ReadPiecesBeforeAsync(HttpContext http, SessionEventHub hub,
      Guid streamId, Guid leaseId, CursorQuery query) {
    if (!TryReadCursor(streamId, leaseId, query, out MarkdownRenderCursor cursor, out IResult? invalid))
      return Task.FromResult(invalid!);
    if (!TryGetPage(hub, streamId, out SessionEventPage page))
      return Task.FromResult(MissingPageResult(http));

    MarkdownPiecePage result = page.MarkdownLeases.Read(leaseId, lease => lease.ReadPiecesBefore(cursor));
    return Task.FromResult<IResult>(RenderJson(result));
  }

  private static Task<IResult> ReadContextAsync(HttpContext http, SessionEventHub hub,
      Guid streamId, Guid leaseId, int id, CursorQuery query) {
    if (streamId == Guid.Empty || leaseId == Guid.Empty || id < 0
        || !TryReadCursor(query, out MarkdownRenderCursor cursor))
      return Task.FromResult<IResult>(Results.BadRequest());
    if (!TryGetPage(hub, streamId, out SessionEventPage page))
      return Task.FromResult(MissingPageResult(http));

    MarkdownContextPage result = page.MarkdownLeases.Read(leaseId, lease => lease.ReadContext(id, cursor));
    return Task.FromResult<IResult>(RenderJson(result));
  }

  private static Task<IResult> LocateChildAsync(HttpContext http, SessionEventHub hub,
      Guid streamId, Guid leaseId, int contextId, int childIndex) {
    if (streamId == Guid.Empty || leaseId == Guid.Empty || contextId < 0 || childIndex < 0)
      return Task.FromResult<IResult>(Results.BadRequest());
    if (!TryGetPage(hub, streamId, out SessionEventPage page))
      return Task.FromResult(MissingPageResult(http));

    MarkdownRenderCursor cursor = page.MarkdownLeases.Read(leaseId,
        lease => lease.LocateChild(contextId, childIndex));
    return Task.FromResult<IResult>(RenderJson(cursor));
  }

  private static async Task<IResult> ReadContextsAsync(HttpContext http, SessionEventHub hub,
      Guid streamId, Guid leaseId) {
    if (streamId == Guid.Empty || leaseId == Guid.Empty)
      return Results.BadRequest();
    ContextsRequest? request = await http.Request.ReadFromJsonAsync<ContextsRequest>(http.RequestAborted);
    if (request is null || request.Generation == Guid.Empty || request.Index is not int index || index < 0
        || request.Ids is null || request.Ids.Length > 256)
      return Results.BadRequest();
    if (!TryGetPage(hub, streamId, out SessionEventPage page))
      return MissingPageResult(http);

    var cursor = new MarkdownRenderCursor(request.Generation, index);
    MarkdownContextDescriptorPage result = page.MarkdownLeases.Read(leaseId, lease =>
        lease.ReadContexts(request.Ids, cursor));
    return RenderJson(result);
  }

  private static Task<IResult> LocateAsync(HttpContext http, SessionEventHub hub,
      Guid streamId, Guid leaseId, LocateQuery query) {
    if (streamId == Guid.Empty || leaseId == Guid.Empty || query.SourceUtf8Offset is null
        || query.SourceUtf8Offset < 0)
      return Task.FromResult<IResult>(Results.BadRequest());
    if (!TryGetPage(hub, streamId, out SessionEventPage page))
      return Task.FromResult(MissingPageResult(http));

    MarkdownRenderCursor cursor = page.MarkdownLeases.Read(leaseId,
        lease => lease.Locate(query.SourceUtf8Offset.Value));
    return Task.FromResult<IResult>(RenderJson(cursor));
  }

  private static async Task<IResult> LocateSourceAsync(HttpContext http, SessionEventHub hub,
      Guid streamId, Guid leaseId) {
    if (streamId == Guid.Empty || leaseId == Guid.Empty)
      return Results.BadRequest();
    MessageContentCursor? cursor = await http.Request.ReadFromJsonAsync<MessageContentCursor>(http.RequestAborted);
    if (cursor is null)
      return Results.BadRequest();
    if (!TryGetPage(hub, streamId, out SessionEventPage page))
      return MissingPageResult(http);
    return RenderJson(page.MarkdownLeases.Read(leaseId, lease => lease.Locate(cursor)));
  }

  private static IResult ReadSourceCursor(HttpContext http, SessionEventHub hub,
      Guid streamId, Guid leaseId, LocateQuery query) {
    if (streamId == Guid.Empty || leaseId == Guid.Empty || query.SourceUtf8Offset is null
        || query.SourceUtf8Offset < 0)
      return Results.BadRequest();
    if (!TryGetPage(hub, streamId, out SessionEventPage page))
      return MissingPageResult(http);
    // Persisted source identity uses the normal session wire contract, including role strings.
    return Results.Ok(page.MarkdownLeases.Read(leaseId,
        lease => lease.SourceCursor(query.SourceUtf8Offset.Value)));
  }

  private static async Task<IResult> ReleaseAsync(HttpContext http, SessionEventHub hub,
      Guid streamId, Guid leaseId) {
    if (streamId == Guid.Empty || leaseId == Guid.Empty)
      return Results.BadRequest();
    await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);
    if (TryGetPage(hub, streamId, out SessionEventPage page))
      page.MarkdownLeases.Release(leaseId);
    return Results.NoContent();
  }

  private static bool TryReadCursor(Guid streamId, Guid leaseId, CursorQuery query,
      out MarkdownRenderCursor cursor, out IResult? invalid) {
    if (streamId == Guid.Empty || leaseId == Guid.Empty || !TryReadCursor(query, out cursor)) {
      cursor = default;
      invalid = Results.BadRequest();
      return false;
    }

    invalid = null;
    return true;
  }

  private static bool TryReadCursor(CursorQuery query, out MarkdownRenderCursor cursor) {
    if (query.Generation is not Guid generation || generation == Guid.Empty
        || query.Index is not int index || index < 0) {
      cursor = default;
      return false;
    }

    cursor = new(generation, index);
    return true;
  }

  private static bool TryGetPage(SessionEventHub hub, Guid streamId, out SessionEventPage page) {
    if (streamId != Guid.Empty && hub.TryGet(streamId, out page!) && !page.IsClosed)
      return true;
    page = null!;
    return false;
  }

  private static IResult RenderJson<T>(T value) => Results.Json(value, options: MarkdownRenderJson.Options);

  private static IResult MissingPageResult(HttpContext http) => IsStopping(http)
      ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.NotFound();

  private static bool IsStopping(HttpContext http)
      => http.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;

  private sealed class OwnedOpenResult(SessionEventPage page, Guid leaseId, IResult response) : IResult {
    public async Task ExecuteAsync(HttpContext httpContext) {
      try {
        await response.ExecuteAsync(httpContext);
        httpContext.RequestAborted.ThrowIfCancellationRequested();
      } catch {
        page.MarkdownLeases.Release(leaseId);
        throw;
      }
    }
  }

  private sealed record CursorQuery(Guid? Generation, int? Index);
  private sealed record LocateQuery(int? SourceUtf8Offset);
  private sealed record OpenRequest(Guid SessionId, TranscriptEntryRef? Entry);
  private sealed record ContextsRequest(Guid Generation, int? Index, int[]? Ids);
}
