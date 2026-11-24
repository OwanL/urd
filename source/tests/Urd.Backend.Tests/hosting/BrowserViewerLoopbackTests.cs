using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using SkiaSharp;
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
    app.MapGet("/fixture", () => Results.Content("""
      <!doctype html>
      <style>
        html,body{margin:0;overflow:hidden}
        #content{width:1800px;height:1200px;background:#ddd}
        #target{position:fixed;width:48px;height:48px;border:0;display:none}
      </style>
      <main id="content"><input id="input"><button id="target" aria-label="Click target"
        onclick="this.dataset.click = `${event.clientX},${event.clientY}`"></button></main>
      """, "text/html"));
    app.MapBrowserViewer(manager);
    await app.StartAsync(deadline.Token);
    origin = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

    BrowserPage<IPage> registered = await host.OpenAsync(primary, deadline.Token);
    IPage page = registered.Page;
    Assert.False(await page.EvaluateAsync<bool>("navigator.webdriver"));
    Assert.DoesNotContain("Headless", await page.EvaluateAsync<string>("navigator.userAgent"));
    await page.GotoAsync(new Uri(origin, "/fixture").ToString());
    int[] initialViewport = await page.EvaluateAsync<int[]>("() => [innerWidth, innerHeight]");
    Assert.Equal(new[] { 1280, 800 }, initialViewport);
    ICDPSession devTools = await page.Context.NewCDPSessionAsync(page);
    try {
      JsonElement window = (await devTools.SendAsync("Browser.getWindowForTarget"))!.Value;
      int windowId = window.GetProperty("windowId").GetInt32();
      await devTools.SendAsync("Browser.setContentsSize", new Dictionary<string, object> {
        ["windowId"] = windowId,
        ["width"] = 500,
        ["height"] = 145
      });
    } finally {
      await devTools.DetachAsync();
    }
    Assert.Equal(initialViewport, await page.EvaluateAsync<int[]>("() => [innerWidth, innerHeight]"));
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
    Assert.Equal((1280, 800), ReadJpegDimensions(jpeg));
    using (SKBitmap bitmap = SKBitmap.Decode(jpeg)) {
      SKColor contentColor = new(0xdd, 0xdd, 0xdd);
      foreach ((int x, int y) in new[] { (1270, 10), (10, 790), (1270, 790) })
        Assert.True(PixelMatches(bitmap.GetPixel(x, y), contentColor), $"Expected fixture content at desktop edge pixel ({x},{y}).");
    }

    var streamedFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
    Task readingFrames = ReadStreamFramesAsync();
    foreach ((int width, int height) in new[] { (600, 320), (1400, 900), (360, 650) }) {
      await SendAsync($"{{\"type\":\"resize\",\"width\":{width},\"height\":{height}}}");
      await page.WaitForFunctionAsync($"innerWidth === {width} && innerHeight === {height}", null, new() { Timeout = 10000 });
      int[] bounds = await page.EvaluateAsync<int[]>("""() => [innerWidth, innerHeight, document.querySelector('#content').getBoundingClientRect().width, document.querySelector('#content').getBoundingClientRect().height]""");
      Assert.Equal(new[] { width, height, 1800, 1200 }, bounds);

      // A quiet resize must produce a correctly sized frame without an extra paint probe.
      await ExpectFrameAsync(width, height);

      // Move and recolor a visible target to require two fresh compositor frames, not the
      // startup screenshot or a stale resize frame with coincidentally matching dimensions.
      int y = height - 63;
      int x = width - 160;
      await MoveTargetAsync(x, y, "red");
      await ExpectFrameAsync(width, height, x, y, SKColors.Red);
      x = width - 59;
      await MoveTargetAsync(x, y, "blue");
      await ExpectFrameAsync(width, height, x, y, SKColors.Blue);
      await SendAsync($"{{\"type\":\"click\",\"x\":{x},\"y\":{y}}}");
      await page.WaitForFunctionAsync("click => document.querySelector('#target').dataset.click === click", $"{x},{y}", new() { Timeout = 10000 });
    }

    await page.Locator("#input").FocusAsync();
    await SendAsync("{\"type\":\"text\",\"text\":\"viewer input\"}");
    await page.WaitForFunctionAsync("document.querySelector('#input').value === 'viewer input'", null, new() { Timeout = 10000 });
    await SendAsync("{\"type\":\"resize\",\"width\":640,\"height\":480}");
    await page.WaitForFunctionAsync("innerWidth === 640 && innerHeight === 480", null, new() { Timeout = 10000 });
    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token);
    await page.WaitForFunctionAsync("innerWidth === 1280 && innerHeight === 800", null, new() { Timeout = 10000 });
    await readingFrames.WaitAsync(TimeSpan.FromSeconds(5));

    Task MoveTargetAsync(int x, int y, string color) => page.EvaluateAsync("""
      ({ x, y, color }) => {
        const target = document.querySelector('#target');
        delete target.dataset.click;
        Object.assign(target.style, { display: 'block', left: `${x - 24}px`, top: `${y - 24}px`, background: color });
      }
      """, new { x, y, color });

    async Task ExpectFrameAsync(int width, int height, int x = 0, int y = 0, SKColor? color = null) {
      using var resizeDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
      resizeDeadline.CancelAfter(TimeSpan.FromSeconds(3));
      var observedDimensions = new HashSet<(int Width, int Height)>();
      try {
        while (await streamedFrames.Reader.WaitToReadAsync(resizeDeadline.Token)) {
          while (streamedFrames.Reader.TryRead(out byte[]? received)) {
            var dimensions = ReadJpegDimensions(received);
            observedDimensions.Add(dimensions);
            if (dimensions != (width, height))
              continue;
            if (color is not { } expected)
              return;
            using SKBitmap bitmap = SKBitmap.Decode(received);
            if (PixelMatches(bitmap.GetPixel(x, y), expected))
              return;
          }
        }
      } catch (OperationCanceledException) when (!deadline.IsCancellationRequested) {
        // Include every size observed before the wait expired.
      }
      Assert.Fail($"Expected viewer JPEG {width}x{height} (target {color} at {x},{y}); observed sizes: {string.Join(", ", observedDimensions)}.");
    }

    async Task ReadStreamFramesAsync() {
      try {
        while (!deadline.IsCancellationRequested) {
          using var streamedFrame = new MemoryStream();
          WebSocketReceiveResult framePart;
          do {
            framePart = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            if (framePart.MessageType == WebSocketMessageType.Close)
              return;
            if (framePart.MessageType != WebSocketMessageType.Binary)
              throw new Xunit.Sdk.XunitException("Viewer sent a non-binary frame.");
            await streamedFrame.WriteAsync(buffer.AsMemory(0, framePart.Count), deadline.Token);
          } while (!framePart.EndOfMessage);
          streamedFrames.Writer.TryWrite(streamedFrame.ToArray());
        }
      } catch (OperationCanceledException) when (deadline.IsCancellationRequested) {
        // The test deadline closed the viewer.
      } finally {
        streamedFrames.Writer.TryComplete();
      }
    }

    async Task SendAsync(string json) => await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, deadline.Token);
  }

  private static bool PixelMatches(SKColor actual, SKColor expected) =>
    Math.Abs(actual.Red - expected.Red) < 25 && Math.Abs(actual.Green - expected.Green) < 25
    && Math.Abs(actual.Blue - expected.Blue) < 25;

  private static (int Width, int Height) ReadJpegDimensions(ReadOnlySpan<byte> jpeg) {
    Assert.True(jpeg.Length >= 4 && jpeg[0] == 0xff && jpeg[1] == 0xd8, "Frame is not a JPEG.");
    int offset = 2;
    while (offset < jpeg.Length) {
      Assert.Equal((byte)0xff, jpeg[offset++]);
      while (offset < jpeg.Length && jpeg[offset] == 0xff)
        offset++;
      Assert.True(offset < jpeg.Length, "JPEG ended before its dimensions were found.");
      byte marker = jpeg[offset++];
      if (marker is 0xd8 or 0xd9 or 0x01 or >= 0xd0 and <= 0xd7)
        continue;
      Assert.True(offset + 1 < jpeg.Length, "JPEG marker has no segment length.");
      int length = jpeg[offset] << 8 | jpeg[offset + 1];
      Assert.True(length >= 2 && offset + length <= jpeg.Length, "JPEG marker has an invalid segment length.");
      if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf) {
        Assert.True(length >= 7, "JPEG frame header is incomplete.");
        int height = jpeg[offset + 3] << 8 | jpeg[offset + 4];
        int width = jpeg[offset + 5] << 8 | jpeg[offset + 6];
        return (width, height);
      }
      offset += length;
    }
    throw new Xunit.Sdk.XunitException("JPEG has no frame header.");
  }
}
