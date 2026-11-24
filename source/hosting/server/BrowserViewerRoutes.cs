using System.Net.WebSockets;
using Microsoft.Playwright;
using Urd.Core.Sessions;
using Urd.Core.Tools;

namespace Urd.Server;

// Viewer access is scoped to the primary and its live, persisted descendant identities.
internal static class BrowserViewerRoutes {
  private static readonly TimeSpan TitleTimeout = TimeSpan.FromSeconds(2);

  internal static void MapBrowserViewer(this IEndpointRouteBuilder app, SessionManager sessions) {
    app.MapGet("/api/sessions/{primaryId:guid}/browser", (HttpContext http, Guid primaryId) =>
        ListAsync(http, sessions, primaryId));
    app.MapGet("/browser-view/{primaryId:guid}/{pageId:guid}", (HttpContext http, Guid primaryId, Guid pageId) =>
        ViewAsync(http, sessions, primaryId, pageId));
  }

  private static async Task<IResult> ListAsync(HttpContext http, SessionManager sessions, Guid primaryId) {
    IReadOnlyList<Guid>? owners = await sessions.BrowserViewerOwnersAsync(primaryId, http.RequestAborted);
    if (owners is null)
      return Results.NotFound();
    BrowserHost? host = sessions.BrowserHost;
    if (host is null)
      return Results.Ok(new { pages = Array.Empty<BrowserPageDto>() });

    var pages = owners.SelectMany(owner => {
      Guid? selected = host.Pages.Selected(owner)?.Id;
      return host.Pages.List(owner).Select(page => (Page: page, Active: page.Id == selected));
    }).ToArray();
    BrowserPageDto?[] details = await Task.WhenAll(pages.Select(item => DescribeAsync(host, item.Page, item.Active, http.RequestAborted)));
    return Results.Ok(new { pages = details.OfType<BrowserPageDto>().ToArray() });
  }

  private static async Task<BrowserPageDto?> DescribeAsync(BrowserHost host, BrowserPage<IPage> page, bool active,
      CancellationToken cancellation) {
    try {
      if (page.Page.IsClosed)
        return null;
      string title;
      try {
        title = await page.Page.TitleAsync().WaitAsync(TitleTimeout, cancellation);
      } catch (Exception error) when (error is PlaywrightException or TimeoutException) {
        title = "";
      }
      if (page.Page.IsClosed || host.Pages.Get(page.OwnerId, page.Id) is null)
        return null;
      return new(page.Id, page.OwnerId, title, page.Page.Url, active);
    } catch (PlaywrightException) {
      return null;
    }
  }

  private static async Task ViewAsync(HttpContext http, SessionManager sessions, Guid primaryId, Guid pageId) {
    if (!http.WebSockets.IsWebSocketRequest) {
      http.Response.StatusCode = StatusCodes.Status400BadRequest;
      return;
    }
    IReadOnlyList<Guid>? owners = await sessions.BrowserViewerOwnersAsync(primaryId, http.RequestAborted);
    BrowserHost? host = sessions.BrowserHost;
    BrowserPage<IPage>? page = host is null || owners is null ? null : owners
        .Select(owner => host.Pages.Get(owner, pageId)).FirstOrDefault(candidate => candidate is not null);
    if (page is null) {
      http.Response.StatusCode = StatusCodes.Status404NotFound;
      return;
    }
    using WebSocket socket = await http.WebSockets.AcceptWebSocketAsync();
    await BrowserViewerStreams.Shared.RunAsync(host!, page.OwnerId, page, socket, http.RequestAborted);
  }

  private sealed record BrowserPageDto(Guid Id, Guid OwnerId, string Title, string Url, bool Active);
}
