using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
using static Urd.Backend.Tests.Support.TestAsync;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class BrowserViewerRoutesTests {
  [Fact]
  public async Task ListsPagesOnlyForExistingPrimarySessions() {
    using var database = new TemporaryDatabase();
    Guid primary, other, child;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      primary = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      other = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      child = store.Sessions.CreateSubagent(Guid.NewGuid(), primary, "child").Id;
    }
    await using var server = new RunningServer(RunningServer.OptionsForIsolatedWorkspace(database));
    await server.WaitForReadyAsync();

    foreach (Guid id in new[] { primary, other }) {
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get, $"/api/sessions/{id:D}/browser");
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      AssertPrivateResponse(response);
      using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      Assert.Empty(body.RootElement.GetProperty("pages").EnumerateArray());
    }
    foreach (Guid id in new[] { child, Guid.NewGuid() }) {
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get, $"/api/sessions/{id:D}/browser");
      Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
      AssertPrivateResponse(response);
    }
    using HttpResponseMessage unmarked = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{primary:D}/browser", marker: false);
    Assert.Equal(HttpStatusCode.Forbidden, unmarked.StatusCode);
    AssertPrivateResponse(unmarked);
  }

  [Fact]
  public async Task ViewerUpgradeRequiresExactOriginAndExistingPrimaryAndPage() {
    using var database = new TemporaryDatabase();
    Guid primary, other, child;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      primary = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      other = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      child = store.Sessions.CreateSubagent(Guid.NewGuid(), primary, "child").Id;
    }
    await using var server = new RunningServer(RunningServer.OptionsForIsolatedWorkspace(database));
    Uri origin = await server.WaitForReadyAsync();
    string authority = origin.GetLeftPart(UriPartial.Authority);
    Guid missingPage = Guid.NewGuid();

    foreach (string? suppliedOrigin in new[] { null, "http://foreign.example", authority + "/" }) {
      using HttpResponseMessage response = await UpgradeAsync(primary, missingPage, suppliedOrigin);
      Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      AssertNoCors(response);
    }
    foreach (Guid id in new[] { primary, other, child, Guid.NewGuid() }) {
      using HttpResponseMessage response = await UpgradeAsync(id, missingPage, authority);
      Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
      AssertNoCors(response);
    }
    using HttpResponseMessage nonUpgrade = await RequestAsync(server.Client, HttpMethod.Get,
        $"/browser-view/{primary:D}/{missingPage:D}", marker: false, suppliedOrigin: authority);
    Assert.Equal(HttpStatusCode.BadRequest, nonUpgrade.StatusCode);
    AssertNoCors(nonUpgrade);

    async Task<HttpResponseMessage> UpgradeAsync(Guid session, Guid page, string? suppliedOrigin) {
      using var request = new HttpRequestMessage(HttpMethod.Get, $"/browser-view/{session:D}/{page:D}");
      request.Headers.Connection.Add("Upgrade");
      request.Headers.Upgrade.Add(new ProductHeaderValue("websocket"));
      request.Headers.Add("Sec-WebSocket-Version", "13");
      request.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
      if (suppliedOrigin is not null)
        request.Headers.Add("Origin", suppliedOrigin);
      return await server.Client.SendAsync(request);
    }
  }

  [Fact]
  public async Task ExistingForeignPageIsNeitherListedNorViewable() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid primary = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid foreign = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("Must not run."));
    manager.ConfigureBrowser(Path.GetDirectoryName(database.Path)!);
    BrowserHost host = manager.BrowserHost!;
    IPage FakePage(string title) => Proxy<IPage>((method, _) => method.Name switch {
      "get_IsClosed" => false,
      "get_Url" => $"https://example.test/{title}",
      "TitleAsync" => Task.FromResult(title),
      _ => throw new NotSupportedException(method.Name)
    });
    BrowserPage<IPage> ownPage = host.Pages.Register(primary, FakePage("own")).Page;
    BrowserPage<IPage> foreignPage = host.Pages.Register(foreign, FakePage("foreign")).Page;

    var builder = WebApplication.CreateBuilder();
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
    await using WebApplication app = builder.Build();
    app.UseWebSockets();
    app.MapBrowserViewer(manager);
    await app.StartAsync().WaitAsync(TestTimeout);
    string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) {
      BaseAddress = new Uri(address), Timeout = TestTimeout
    };
    foreach ((Guid session, Guid expectedPage) in new[] { (primary, ownPage.Id), (foreign, foreignPage.Id) }) {
      using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, $"/api/sessions/{session:D}/browser");
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      JsonElement page = Assert.Single(body.RootElement.GetProperty("pages").EnumerateArray());
      Assert.Equal(expectedPage, page.GetProperty("id").GetGuid());
      Assert.Equal(session, page.GetProperty("ownerId").GetGuid());
      Assert.True(page.GetProperty("active").GetBoolean());
      Assert.NotEmpty(page.GetProperty("title").GetString()!);
      Assert.StartsWith("https://example.test/", page.GetProperty("url").GetString());
    }
    foreach ((Guid session, Guid page) in new[] { (primary, foreignPage.Id), (foreign, ownPage.Id) }) {
      using var request = new HttpRequestMessage(HttpMethod.Get, $"/browser-view/{session:D}/{page:D}");
      request.Headers.Connection.Add("Upgrade");
      request.Headers.Upgrade.Add(new ProductHeaderValue("websocket"));
      request.Headers.Add("Sec-WebSocket-Version", "13");
      request.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
      request.Headers.Add("Origin", new Uri(address).GetLeftPart(UriPartial.Authority));
      using HttpResponseMessage upgrade = await client.SendAsync(request);
      Assert.Equal(HttpStatusCode.NotFound, upgrade.StatusCode);
    }
  }

  [Fact]
  public async Task ViewerForwardsFramesAndInputsWithoutAnInstalledBrowser() {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var commands = Channel.CreateUnbounded<(string Method, object?[] Arguments)>();
    EventHandler<JsonElement?>? frameHandler = null;
    ICDPSessionEvent frameEvent = Proxy<ICDPSessionEvent>((method, args) => {
      if (method.Name == "add_OnEvent") frameHandler = (EventHandler<JsonElement?>)args[0]!;
      else if (method.Name == "remove_OnEvent") frameHandler = null;
      else throw new NotSupportedException(method.Name);
      return null;
    });
    ICDPSession devTools = Proxy<ICDPSession>((method, args) => {
      if (method.Name == "Event") return frameEvent;
      if (method.Name == "DetachAsync") return Task.CompletedTask;
      if (method.Name != "SendAsync") throw new NotSupportedException(method.Name);
      string command = (string)args[0]!;
      commands.Writer.TryWrite((command, args));
      if (command == "Page.startScreencast") started.TrySetResult();
      return Task.FromResult<JsonElement?>(null);
    });
    IBrowserContext context = Proxy<IBrowserContext>((method, _) => method.Name == "NewCDPSessionAsync"
        ? Task.FromResult(devTools) : throw new NotSupportedException(method.Name));
    IMouse mouse = Proxy<IMouse>(RecordInput);
    IKeyboard keyboard = Proxy<IKeyboard>(RecordInput);
    IPage page = Proxy<IPage>((method, args) => method.Name switch {
      "get_IsClosed" => false,
      "get_ViewportSize" => new PageViewportSizeResult { Width = 1280, Height = 800 },
      "get_Context" => context,
      "get_Mouse" => mouse,
      "get_Keyboard" => keyboard,
      "SetViewportSizeAsync" => RecordInput(method, args),
      _ when method.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal) => null,
      _ => throw new NotSupportedException(method.Name)
    });
    Guid owner = Guid.NewGuid();
    BrowserPage<IPage> registered = host.Pages.Register(owner, page).Page;
    using var socket = new ViewerSocket();
    using var cancellation = new CancellationTokenSource(TestTimeout);
    Task watching = new BrowserViewerStreams().RunAsync(host, owner, registered, socket, cancellation.Token);
    try {
      await started.Task.WaitAsync(TestTimeout);
      using JsonDocument payload = JsonDocument.Parse("{\"data\":\"AQID\",\"sessionId\":7}");
      Assert.NotNull(frameHandler);
      frameHandler!(devTools, payload.RootElement);
      Assert.Equal(new byte[] { 1, 2, 3 }, await socket.Frames.Reader.ReadAsync(cancellation.Token));
      Assert.Equal("Emulation.setVisibleSize", (await commands.Reader.ReadAsync(cancellation.Token)).Method);
      Assert.Equal("Page.startScreencast", (await commands.Reader.ReadAsync(cancellation.Token)).Method);
      var ack = await commands.Reader.ReadAsync(cancellation.Token);
      Assert.Equal("Page.screencastFrameAck", ack.Method);
      Assert.Equal(7, ((Dictionary<string, object>)ack.Arguments[1]!)["sessionId"]);

      socket.SendText("{\"type\":\"click\",\"x\":-5,\"y\":9000}");
      socket.SendText("{\"type\":\"wheel\",\"x\":2,\"y\":3,\"dx\":-12000,\"dy\":25}");
      socket.SendText("{\"type\":\"key\",\"key\":\"Enter\"}");
      socket.SendText("{\"type\":\"text\",\"text\":\"hello\"}");
      socket.SendText("{\"type\":\"resize\",\"width\":100,\"height\":5000}");
      await ExpectInput("ClickAsync", 0f, 8192f);
      await ExpectInput("MoveAsync", 2f, 3f);
      await ExpectInput("WheelAsync", -10000f, 25f);
      await ExpectInput("PressAsync", "Enter");
      await ExpectInput("InsertTextAsync", "hello");
      await ExpectInput("SetViewportSizeAsync", 240, 3840);
      socket.CloseFromClient();
      await watching.WaitAsync(TestTimeout);
      Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
    } finally {
      await cancellation.CancelAsync();
      await watching.WaitAsync(TestTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
    }

    object RecordInput(MethodInfo method, object?[] args) {
      commands.Writer.TryWrite((method.Name, args));
      return Task.CompletedTask;
    }
    async Task ExpectInput(string method, params object[] expected) {
      var actual = await commands.Reader.ReadAsync(cancellation.Token);
      Assert.Equal(method, actual.Method);
      Assert.Equal(expected, actual.Arguments.Take(expected.Length));
    }
  }

  private static T Proxy<T>(Func<MethodInfo, object?[], object?> invoke) where T : class {
    T proxy = DispatchProxy.Create<T, BrowserProxy>();
    ((BrowserProxy)(object)proxy).InvokeMethod = invoke;
    return proxy;
  }

  public class BrowserProxy : DispatchProxy {
    public Func<MethodInfo, object?[], object?> InvokeMethod { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeMethod(targetMethod!, args ?? []);
  }

  private sealed class ViewerSocket : WebSocket {
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type)> incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
    private WebSocketState state = WebSocketState.Open;
    private WebSocketCloseStatus? lastCloseStatus;
    public Channel<byte[]> Frames { get; } = Channel.CreateUnbounded<byte[]>();
    public override WebSocketCloseStatus? CloseStatus => lastCloseStatus;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;
    public void SendText(string text) => incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text));
    public void CloseFromClient() => incoming.Writer.TryWrite(([], WebSocketMessageType.Close));
    public override void Abort() => state = WebSocketState.Aborted;
    public override void Dispose() => state = WebSocketState.Closed;
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) {
      lastCloseStatus = closeStatus;
      state = WebSocketState.CloseSent;
      return Task.CompletedTask;
    }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
      Assert.Equal(WebSocketMessageType.Binary, messageType);
      Assert.True(endOfMessage);
      Frames.Writer.TryWrite(buffer.ToArray());
      return Task.CompletedTask;
    }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
      var message = await incoming.Reader.ReadAsync(cancellationToken);
      message.Bytes.CopyTo(buffer.AsMemory());
      return new WebSocketReceiveResult(message.Bytes.Length, message.Type, true);
    }
  }

  private static void AssertNoCors(HttpResponseMessage response) {
    Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
  }
}
