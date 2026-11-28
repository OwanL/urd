using Microsoft.AspNetCore.Http.Features;
using Urd.Core.Sessions;

namespace Urd.Server;

internal static class SubagentRoutes {
  internal static void MapSubagentRoutes(this RouteGroupBuilder routes, SessionManager sessions) {
    routes.MapGet("/{primaryId}/subagents/live", (HttpContext http, Guid primaryId) =>
        ListLiveAsync(http, sessions, primaryId));
    routes.MapGet("/{parentId}/subagents/by-call/{requestId}/{callId}",
        (HttpContext http, Guid parentId, Guid requestId, string callId) =>
            FindByCallAsync(http, sessions, parentId, requestId, callId));
  }

  private static async Task<IResult> ListLiveAsync(HttpContext http, SessionManager sessions, Guid primaryId) {
    if (primaryId == Guid.Empty)
      return Results.BadRequest();

    return Results.Ok(await sessions.ListLiveSubagentsAsync(primaryId, http.RequestAborted));
  }

  private static async Task<IResult> FindByCallAsync(HttpContext http, SessionManager sessions, Guid parentId,
      Guid requestId, string callId) {
    callId = DecodeCallId(http, callId);
    if (parentId == Guid.Empty || requestId == Guid.Empty || string.IsNullOrWhiteSpace(callId))
      return Results.BadRequest();

    SubagentCall? child = await sessions.FindSubagentByCallAsync(parentId, requestId, callId, http.RequestAborted);
    return child is null ? Results.NotFound() : Results.Ok(child);
  }

  private static string DecodeCallId(HttpContext http, string callId) {
    // Routing leaves encoded slashes alone. Use the original segment to distinguish '/' from a literal '%2F'.
    if (!callId.Contains("%2F", StringComparison.OrdinalIgnoreCase)
        || http.Features.Get<IHttpRequestFeature>()?.RawTarget is not { } target)
      return callId;

    int query = target.IndexOf('?');
    ReadOnlySpan<char> path = target.AsSpan(0, query < 0 ? target.Length : query);
    return Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..].ToString());
  }
}
