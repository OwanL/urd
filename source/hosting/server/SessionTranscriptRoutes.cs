using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.ToolCallDisplay;

namespace Urd.Server;

// Binds transcript reads and navigation to the session route group.
internal static class SessionTranscriptRoutes {
  private const int MaxToolCallTranscriptCharacters = 65_536;
  private const int MaxPromptPageSize = 60;

  internal static void MapSessionTranscriptRoutes(this RouteGroupBuilder routes, SessionManager sessions) {
    routes.MapGet("/{sessionId}/history", (HttpContext http, Guid sessionId, [AsParameters] HistoryQuery query) =>
        ReadHistoryAsync(http, sessions, sessionId, query));
    routes.MapGet("/{sessionId}/tool-calls/{requestId}/{callId}", (HttpContext http, Guid sessionId,
        Guid requestId, string callId, [AsParameters] ToolCallTranscriptQuery query) =>
        ReadToolCallTranscriptAsync(http, sessions, sessionId, requestId, callId, query));
    routes.MapGet("/{sessionId}/positions", (HttpContext http, Guid sessionId,
        [AsParameters] PositionSummaryQuery query) => ReadPositionSummaryAsync(http, sessions, sessionId, query));
    routes.MapGet("/{sessionId}/positions/locate", (HttpContext http, Guid sessionId,
        [AsParameters] PositionLocateQuery query) => LocatePositionAsync(http, sessions, sessionId, query));
    routes.MapGet("/{sessionId}/positions/entries/{executionId}/{entryId}", (HttpContext http, Guid sessionId,
        Guid executionId, Guid entryId, [AsParameters] EntryPositionQuery query) =>
        ReadEntryPositionAsync(http, sessions, sessionId, executionId, entryId, query));
    routes.MapGet("/{sessionId}/positions/prompts", (HttpContext http, Guid sessionId,
        [AsParameters] PromptPageQuery query) => ReadPromptPageAsync(http, sessions, sessionId, query));
    routes.MapGet("/{sessionId}/positions/prompts/{executionId}/{entryId}", (HttpContext http, Guid sessionId,
        Guid executionId, Guid entryId, [AsParameters] PromptActivationQuery query) =>
        ActivatePromptAsync(http, sessions, sessionId, executionId, entryId, query));
    routes.MapGet("/{sessionId:guid}/markdown-images/{executionId:guid}/{messageId:guid}/{key}",
        (HttpContext http, Guid sessionId, Guid executionId, Guid messageId, string key, long historyRevision) =>
            ReadMarkdownImageAsync(http, sessions, sessionId, executionId, messageId, key, historyRevision));
    routes.MapGet("/{sessionId}/content", (HttpContext http, Guid sessionId, [AsParameters] ContentQuery query) =>
        ReadContentAsync(http, sessions, sessionId, query));
    routes.MapGet("/{sessionId}/compactions/{compactionId}", (HttpContext http, Guid sessionId,
        Guid compactionId, long expectedHistoryRevision) =>
        WithTranscriptAsync(http, sessions, sessionId, async (transcript, _) => {
          CompactionSummary? summary = await transcript.ReadCompactionAsync(compactionId,
              expectedHistoryRevision, http.RequestAborted);
          return summary is null ? Results.NotFound() : Results.Ok(summary);
        }));
    routes.MapGet("/{sessionId}/subagent-view", (HttpContext http, Guid sessionId) =>
        ReadSubagentViewAsync(http, sessions, sessionId));
  }

  private static async Task<IResult> ReadSubagentViewAsync(HttpContext http, SessionManager sessions, Guid sessionId) {
    if (sessionId == Guid.Empty)
      return Results.BadRequest();

    SubagentView? view = await sessions.ReadSubagentViewAsync(sessionId, http.RequestAborted);
    return view is null ? Results.NotFound() : Results.Ok(view);
  }

  internal static async Task<IResult> WithTranscriptAsync(HttpContext http, SessionManager sessions, Guid sessionId,
      Func<SessionTranscript, string?, Task<IResult>> read) {
    if (sessionId == Guid.Empty)
      return Results.BadRequest();

    var (transcript, workingDirectory) = await sessions.GetTranscriptAsync(sessionId, http.RequestAborted);
    return await read(transcript, workingDirectory);
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
      "toolCompletion" => TranscriptEntryKind.ToolCompletion,
      "compaction" => TranscriptEntryKind.Compaction,
      _ => null
    };
    if (mode is null || query.ExpectedHistoryRevision < 0)
      return Task.FromResult<IResult>(Results.BadRequest());
    bool hasAnyAnchor = query.ExecutionId is not null || query.EntryId is not null || query.Kind is not null;
    bool hasCompleteAnchor = query.ExecutionId is not null && query.EntryId is not null && kind is not null;
    bool isLatest = mode == TranscriptHistoryMode.Latest;
    if ((isLatest && hasAnyAnchor) || (!isLatest && !hasCompleteAnchor))
      return Task.FromResult<IResult>(Results.BadRequest());
    TranscriptEntryRef? anchor = hasAnyAnchor
        ? new(query.ExecutionId!.Value, query.EntryId!.Value, kind!.Value, query.ExpectedHistoryRevision) : null;
    var history = new TranscriptHistoryQuery(mode.Value, query.ExpectedHistoryRevision, anchor);
    return WithTranscriptAsync(http, sessions, sessionId, async (transcript, workingDirectory) => {
      TranscriptHistoryPage page = await transcript.ReadHistoryAsync(history, http.RequestAborted);
      return Results.Ok(ToHistoryPage(workingDirectory, page));
    });
  }

  private static Task<IResult> ReadToolCallTranscriptAsync(HttpContext http, SessionManager sessions,
      Guid sessionId, Guid requestId, string callId, ToolCallTranscriptQuery query) {
    if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(callId)
        || query.ExpectedHistoryRevision < 0 || query.Returned is null || query.Full is null)
      return Task.FromResult<IResult>(Results.BadRequest());

    return WithTranscriptAsync(http, sessions, sessionId, async (reader, _) => {
      ToolCallTranscript? transcript = await reader.ReadToolCallTranscriptAsync(
          query.ExpectedHistoryRevision, requestId, callId, query.Returned.Value,
          query.Full.Value ? null : MaxToolCallTranscriptCharacters, http.RequestAborted);
      return transcript is null ? Results.NotFound() : Results.Ok(transcript);
    });
  }

  private static Task<IResult> ReadPositionSummaryAsync(
      HttpContext http, SessionManager sessions, Guid sessionId, PositionSummaryQuery query) {
    if (query.ExpectedHistoryRevision < 0)
      return Task.FromResult<IResult>(Results.BadRequest());

    return WithTranscriptAsync(http, sessions, sessionId, async (transcript, _) => {
      TranscriptPositionState state = await transcript.ReadPositionSummaryAsync(
          query.ExpectedHistoryRevision, http.RequestAborted);
      return Results.Ok(ToPositionSummary(state));
    });
  }

  private static Task<IResult> LocatePositionAsync(
      HttpContext http, SessionManager sessions, Guid sessionId, PositionLocateQuery query) {
    if (query.ExpectedHistoryRevision < 0 || query.Fraction is not double fraction
        || !double.IsFinite(fraction) || fraction is < 0 or > 1)
      return Task.FromResult<IResult>(Results.BadRequest());

    return WithTranscriptAsync(http, sessions, sessionId, async (transcript, workingDirectory) => {
      TranscriptPositionWindow window = await transcript.LocatePositionAsync(
          query.ExpectedHistoryRevision, fraction, http.RequestAborted);
      return Results.Ok(ToPositionWindow(workingDirectory, window));
    });
  }

  private static Task<IResult> ReadEntryPositionAsync(HttpContext http, SessionManager sessions, Guid sessionId,
      Guid executionId, Guid entryId, EntryPositionQuery query) {
    if (executionId == Guid.Empty || entryId == Guid.Empty || query.ExpectedHistoryRevision < 0
        || !Enum.TryParse(query.Kind, ignoreCase: true, out TranscriptEntryKind kind) || !Enum.IsDefined(kind))
      return Task.FromResult<IResult>(Results.BadRequest());
    var entry = new TranscriptEntryRef(executionId, entryId, kind, query.ExpectedHistoryRevision);
    return WithTranscriptAsync(http, sessions, sessionId, async (transcript, workingDirectory) => {
      TranscriptPositionWindow window = await transcript.ReadEntryPositionAsync(
          query.ExpectedHistoryRevision, entry, http.RequestAborted);
      return Results.Ok(ToPositionWindow(workingDirectory, window));
    });
  }

  private static Task<IResult> ReadPromptPageAsync(
      HttpContext http, SessionManager sessions, Guid sessionId, PromptPageQuery query) {
    int limit = query.Limit ?? MaxPromptPageSize;
    bool missingCursorValue = http.Request.Query.ContainsKey("afterRank") && query.AfterRank is null;
    bool missingLimitValue = http.Request.Query.ContainsKey("limit") && query.Limit is null;
    if (query.ExpectedHistoryRevision < 0 || missingCursorValue || missingLimitValue
        || query.AfterRank is < 0 || limit is < 1 or > MaxPromptPageSize)
      return Task.FromResult<IResult>(Results.BadRequest());

    return WithTranscriptAsync(http, sessions, sessionId, async (transcript, _) => {
      TranscriptPromptPage page = await transcript.ReadPromptPageAsync(
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

    return WithTranscriptAsync(http, sessions, sessionId, async (transcript, workingDirectory) => {
      TranscriptPositionWindow window = await transcript.ActivatePromptAsync(
          query.ExpectedHistoryRevision, prompt, http.RequestAborted);
      return Results.Ok(ToPositionWindow(workingDirectory, window));
    });
  }

  private static PositionSummaryDto ToPositionSummary(TranscriptPositionState state) => new(
      state.SessionId, state.HistoryRevision, state.PositionVersion, state.EntryCount, state.TotalWeight);

  private static TranscriptPositionDto ToPosition(TranscriptPosition position) => new(
      position.Entry.ExecutionId, position.Entry.EntryId, position.Entry.Kind,
      position.Entry.HistoryRevision, position.Rank, position.Weight, position.PrefixWeight);

  private static HistoryPageDto ToHistoryPage(string? workingDirectory, TranscriptHistoryPage page) => new(
      page.SessionId, page.HistoryRevision, page.Entries, ToolCallLabelSet.CreateLookup(page.Entries, workingDirectory),
      page.HasBefore, page.HasAfter, page.Settlements);

  private static PositionWindowDto ToPositionWindow(string? workingDirectory, TranscriptPositionWindow window) => new(
      ToPositionSummary(window.State), window.Target is null ? null : ToPosition(window.Target),
      window.Entries.Select(entry => new PositionedEntryDto(entry.Descriptor, ToPosition(entry.Position))).ToArray(),
      ToolCallLabelSet.CreateLookup(window.Entries.Select(entry => entry.Descriptor), workingDirectory),
      window.HasBefore, window.HasAfter, window.Settlements);

  private static Task<IResult> ReadMarkdownImageAsync(HttpContext http, SessionManager sessions, Guid sessionId,
      Guid executionId, Guid messageId, string key, long historyRevision) {
    if (executionId == Guid.Empty || messageId == Guid.Empty || historyRevision < 0)
      return Task.FromResult<IResult>(Results.BadRequest());
    var message = new MessageRef(executionId, messageId, MessageRole.Assistant, historyRevision);
    return WithTranscriptAsync(http, sessions, sessionId, async (transcript, _) => {
      var image = await transcript.ReadMarkdownImageAsync(message, key, http.RequestAborted);
      return image is null ? Results.NotFound() : Results.Bytes(image.Data, image.MediaType);
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
    return WithTranscriptAsync(http, sessions, sessionId, async (transcript, _) =>
        Results.Ok(await transcript.ReadContentAsync(cursor, http.RequestAborted)));
  }

  private sealed record HistoryQuery(
      string Mode, long ExpectedHistoryRevision, Guid? ExecutionId, Guid? EntryId, string? Kind);
  private sealed record ToolCallTranscriptQuery(long ExpectedHistoryRevision, bool? Returned, bool? Full);

  private sealed record PositionSummaryQuery(long ExpectedHistoryRevision);
  private sealed record PositionLocateQuery(long ExpectedHistoryRevision, double? Fraction);
  private sealed record EntryPositionQuery(long ExpectedHistoryRevision, string? Kind);
  private sealed record PromptPageQuery(
      long ExpectedHistoryRevision, long? AfterRank, int? Limit);
  private sealed record PromptActivationQuery(long ExpectedHistoryRevision);

  private sealed record PositionSummaryDto(
      Guid SessionId, long HistoryRevision, long PositionVersion, long EntryCount, long TotalWeight);

  private sealed record TranscriptPositionDto(
      Guid ExecutionId, Guid EntryId, TranscriptEntryKind Kind, long HistoryRevision,
      long Rank, long Weight, long PrefixWeight);

  private sealed record HistoryPageDto(
      Guid SessionId, long HistoryRevision, IReadOnlyList<TranscriptEntryDescriptor> Entries,
      IReadOnlyDictionary<string, ToolCallLabel> ToolLabels, bool HasBefore, bool HasAfter,
      IReadOnlyList<SessionExecutionResult> Settlements);

  private sealed record PositionedEntryDto(TranscriptEntryDescriptor Descriptor, TranscriptPositionDto Position);

  private sealed record PositionWindowDto(
      PositionSummaryDto State, TranscriptPositionDto? Target, IReadOnlyList<PositionedEntryDto> Entries,
      IReadOnlyDictionary<string, ToolCallLabel> ToolLabels,
      bool HasBefore, bool HasAfter, IReadOnlyList<SessionExecutionResult> Settlements);

  private sealed record PromptPageDto(
      PositionSummaryDto State, IReadOnlyList<TranscriptPositionDto> Prompts, long? NextAfterRank);

  // Flat, required query members construct Core's segment-local content cursor.
  private sealed record ContentQuery(
      Guid ExecutionId, Guid MessageId, string Role, long HistoryRevision, long SegmentIndex, int Utf8Offset);
}
