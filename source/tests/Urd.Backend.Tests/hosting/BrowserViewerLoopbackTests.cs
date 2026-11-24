using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

// Opt in on Windows: $env:URD_TEST_BROWSER_VIEWER='1'; dotnet test source/tests/Urd.Backend.Tests -c Release --filter FullyQualifiedName~BrowserViewerLoopbackTests
// BrowserHost uses the managed Chromium cache and may download its pinned browser on first use.
public sealed class BrowserViewerLoopbackTests {
  [Fact]
  [Trait("Category", "BrowserViewerLoopback")]
  public async Task PopupSignInPreservesDraftAndSessionAcrossRestartsUntilLogout() {
    if (Environment.GetEnvironmentVariable("URD_TEST_BROWSER_VIEWER") != "1")
      return;

    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    using var database = new TemporaryDatabase("urd-viewer-login");
    using UrdStore store = UrdStore.Open(database.Path);
    Guid owner = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    string root = Path.GetDirectoryName(database.Path)!;

    // Each phase starts a new manager, host, browser and loopback server against the same profile.
    for (int phase = 0; phase < 3; phase++) {
      await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("Must not run."));
      manager.ConfigureBrowser(root);
      BrowserHost host = manager.BrowserHost!;
      Uri? origin = null;
      var builder = WebApplication.CreateBuilder();
      builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
      await using WebApplication app = builder.Build();
      app.Use(next => new LocalBrowserGate(next, () => origin).InvokeAsync);
      app.UseWebSockets();
      app.MapGet("/fixture", () => Results.Content("""
        <!doctype html><textarea id="draft" autofocus></textarea>
        <button id="signin" onclick="window.open('/popup', 'signin', 'width=400,height=300')">Sign in</button>
        <button id="logout" onclick="fetch('/logout', {method:'POST'}).then(update)">Log out</button>
        <span id="status"></span>
        <script>
          function update() { document.querySelector('#status').textContent = document.cookie.includes('toy_session=yes') ? 'authorized' : 'logged out'; }
          window.addEventListener('message', update); update();
        </script>
        """, "text/html"));
      app.MapGet("/popup", () => Results.Content("""
        <!doctype html><button id="authorize" onclick="location.href='/authorize'">Authorize</button>
        """, "text/html"));
      app.MapGet("/authorize", (HttpContext http) => {
        http.Response.Cookies.Append("toy_session", "yes", new CookieOptions { Path = "/", SameSite = SameSiteMode.Lax });
        return Results.Content("<script>opener.postMessage('authorized', location.origin); window.close()</script>", "text/html");
      });
      app.MapPost("/logout", (HttpContext http) => {
        http.Response.Cookies.Delete("toy_session", new CookieOptions { Path = "/" });
        return Results.Ok();
      });
      app.MapBrowserViewer(manager);
      await app.StartAsync(deadline.Token);
      origin = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

      BrowserPage<IPage> original = await host.OpenAsync(owner, deadline.Token);
      await original.Page.GotoAsync(new Uri(origin, "/fixture").ToString());
      await original.Page.WaitForFunctionAsync(phase == 1
        ? "document.querySelector('#status').textContent === 'authorized'"
        : "document.querySelector('#status').textContent === 'logged out'", null, new() { Timeout = 10000 });
      using ClientWebSocket viewer = await ConnectAsync(original.Id);
      if (phase == 0) {
        await SendAsync(viewer, "{\"type\":\"text\",\"text\":\"unsent draft\"}");
        await original.Page.WaitForFunctionAsync("document.querySelector('#draft').value === 'unsent draft'", null, new() { Timeout = 10000 });
        await ClickAsync(viewer, original.Page, "#signin");
        BrowserPage<IPage>? popup;
        while ((popup = host.Pages.List(owner).FirstOrDefault(item => item.Id != original.Id)) is null)
          await Task.Delay(50, deadline.Token);
        await popup.Page.WaitForSelectorAsync("#authorize");
        using ClientWebSocket popupViewer = await ConnectAsync(popup.Id);
        await ClickAsync(popupViewer, popup.Page, "#authorize");
        await original.Page.WaitForFunctionAsync("document.querySelector('#status').textContent === 'authorized'", null, new() { Timeout = 10000 });
        Assert.Equal("unsent draft", await original.Page.Locator("#draft").InputValueAsync());
      } else if (phase == 1) {
        await ClickAsync(viewer, original.Page, "#logout");
        await original.Page.WaitForFunctionAsync("document.querySelector('#status').textContent === 'logged out'", null, new() { Timeout = 10000 });
      }
      viewer.Abort();
      await app.StopAsync(deadline.Token);

      async Task<ClientWebSocket> ConnectAsync(Guid pageId) {
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.SetRequestHeader("Origin", origin.GetLeftPart(UriPartial.Authority));
        var uri = new UriBuilder(new Uri(origin, $"/browser-view/{owner:D}/{pageId:D}")) { Scheme = "ws" }.Uri;
        await socket.ConnectAsync(uri, deadline.Token);
        byte[] buffer = new byte[1024 * 1024];
        WebSocketReceiveResult frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
        Assert.Equal(WebSocketMessageType.Binary, frame.MessageType);
        return socket;
      }
      async Task ClickAsync(ClientWebSocket socket, IPage page, string selector) {
        var box = await page.Locator(selector).BoundingBoxAsync();
        Assert.NotNull(box);
        await SendAsync(socket, System.Text.Json.JsonSerializer.Serialize(new { type = "click", x = box.X + box.Width / 2, y = box.Y + box.Height / 2 }));
      }
      async Task SendAsync(ClientWebSocket socket, string json) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, deadline.Token);
    }
  }

  [Fact]
  [Trait("Category", "BrowserViewerLoopback")]
  public async Task RealPageStreamsAndAcceptsInputOnlyFromItsTreeAndOrigin() {
    if (Environment.GetEnvironmentVariable("URD_TEST_BROWSER_VIEWER") != "1")
      return;

    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    using var database = new TemporaryDatabase("urd-viewer-loopback");
    using UrdStore store = UrdStore.Open(database.Path);
    Guid primary = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid foreign = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("Must not run."));
    manager.ConfigureBrowser(Path.GetDirectoryName(database.Path)!);
    BrowserHost host = manager.BrowserHost!;

    Uri? origin = null;
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
    await using WebApplication app = builder.Build();
    app.Use(next => new LocalBrowserGate(next, () => origin).InvokeAsync);
    app.UseWebSockets();
    app.MapGet("/fixture", () => Results.Content("<!doctype html><input id='input' autofocus>", "text/html"));
    app.MapBrowserViewer(manager);
    await app.StartAsync(deadline.Token);
    origin = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    BrowserPage<IPage> registered = await host.OpenAsync(primary, deadline.Token);
    IPage page = registered.Page;
    await page.GotoAsync(new Uri(origin, "/fixture").ToString());
    string path = $"/browser-view/{primary:D}/{registered.Id:D}";
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = origin };
    foreach ((string target, string? supplied, HttpStatusCode status) in new[] {
      (path, (string?)null, HttpStatusCode.Forbidden),
      (path, "http://foreign.example", HttpStatusCode.Forbidden),
      ($"/browser-view/{foreign:D}/{registered.Id:D}", origin.GetLeftPart(UriPartial.Authority), HttpStatusCode.NotFound)
    }) {
      using var request = new HttpRequestMessage(HttpMethod.Get, target);
      request.Headers.Connection.Add("Upgrade");
      request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
      request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
      request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
      if (supplied is not null) request.Headers.Add("Origin", supplied);
      using HttpResponseMessage response = await http.SendAsync(request, deadline.Token);
      Assert.Equal(status, response.StatusCode);
    }

    using var socket = new ClientWebSocket();
    socket.Options.Proxy = null;
    socket.Options.SetRequestHeader("Origin", origin.GetLeftPart(UriPartial.Authority));
    var socketUri = new UriBuilder(new Uri(origin, path)) { Scheme = "ws" }.Uri;
    await socket.ConnectAsync(socketUri, deadline.Token);
    byte[] buffer = new byte[1024 * 1024];
    using var frame = new MemoryStream();
    WebSocketReceiveResult result;
    do {
      result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
      Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
      await frame.WriteAsync(buffer.AsMemory(0, result.Count), deadline.Token);
    } while (!result.EndOfMessage);
    byte[] jpeg = frame.ToArray();
    Assert.True(jpeg.Length > 4 && jpeg[0] == 0xff && jpeg[1] == 0xd8 && jpeg[^2] == 0xff && jpeg[^1] == 0xd9);

    await SendAsync("{\"type\":\"text\",\"text\":\"viewer input\"}");
    await page.WaitForFunctionAsync("document.querySelector('#input').value === 'viewer input'", null, new() { Timeout = 10000 });
    await SendAsync("{\"type\":\"resize\",\"width\":640,\"height\":480}");
    await page.WaitForFunctionAsync("innerWidth === 640 && innerHeight === 480", null, new() { Timeout = 10000 });
    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token);
    await page.WaitForFunctionAsync("innerWidth === 1280 && innerHeight === 800", null, new() { Timeout = 10000 });

    async Task SendAsync(string json) => await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, deadline.Token);
  }
}
