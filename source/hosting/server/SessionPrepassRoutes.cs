using Urd.Core.Sessions;
using static Urd.Server.SessionTranscriptRoutes;

namespace Urd.Server;

// Exposes bounded preparation details without adding them to transcript metadata pages.
internal static class SessionPrepassRoutes {
  private static readonly HashSet<string> Categories = ["tools", "skills", "instructions", "title"];

  internal static void MapSessionPrepassRoutes(this RouteGroupBuilder routes, SessionManager sessions) {
    routes.MapGet("/{sessionId}/prepass/{boundaryId}/{category}", (HttpContext http, Guid sessionId,
        Guid boundaryId, string category, [AsParameters] PrepassDetailsQuery query) =>
        ReadAsync(http, sessions, sessionId, boundaryId, category, query));
  }

  private static Task<IResult> ReadAsync(HttpContext http, SessionManager sessions, Guid sessionId,
      Guid boundaryId, string category, PrepassDetailsQuery query) {
    bool missingRevision = !http.Request.Query.ContainsKey("historyRevision") || query.HistoryRevision is null;
    bool missingCandidateOffset = http.Request.Query.ContainsKey("candidateOffset") && query.CandidateOffset is null;
    bool missingResultOffset = http.Request.Query.ContainsKey("resultOffset") && query.ResultOffset is null;
    if (missingRevision || missingCandidateOffset || missingResultOffset
        || query.HistoryRevision < 0 || query.CandidateOffset < 0 || query.ResultOffset < 0
        || !Categories.Contains(category))
      return Task.FromResult<IResult>(Results.BadRequest());

    long historyRevision = query.HistoryRevision!.Value;
    int candidateOffset = query.CandidateOffset ?? 0;
    int resultOffset = query.ResultOffset ?? 0;
    return WithTranscriptAsync(http, sessions, sessionId, async (transcript, _) => {
      PrepassDetails? details = await transcript.ReadPrepassDetailsAsync(boundaryId, category,
          historyRevision, candidateOffset, resultOffset, http.RequestAborted);
      return details is null ? Results.NotFound() : Results.Ok(details);
    });
  }

  private sealed record PrepassDetailsQuery(long? HistoryRevision, int? CandidateOffset, int? ResultOffset);
}
