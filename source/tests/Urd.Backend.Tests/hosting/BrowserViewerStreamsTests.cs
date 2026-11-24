using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Playwright;
using Urd.Backend.Tests.Support;
using Urd.Core.Tools;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

public sealed class BrowserViewerStreamsTests {
  private static readonly byte[] ScreenshotBytes = [1, 2, 3, 4];

  [Fact]
  public async Task InvalidUnicodeClosesAsInvalidPayload() {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    using var socket = new ViewerSocket();
    using var end = new CancellationTokenSource(TestTimeout);
    Task watching = browser.WatchAsync(socket, end.Token);
    await browser.ExpectAsync("Page.startScreencast");
    socket.SendText("{\"type\":\"\\ud800\"}");
    await watching.WaitAsync(TestTimeout);
    Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, socket.CloseStatus);
  }
  [Theory]
  [InlineData("ClickAsync", "{\"type\":\"click\",\"x\":1,\"y\":2}", true)]
  [InlineData("PressAsync", "{\"type\":\"key\",\"key\":\"Enter\"}", false)]
  public async Task ViewerReopensWhileDialogStillBlocksInput(string openingMethod, string openingInput, bool checkTimeout) {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    var stalled = Signal();
    browser.OnInput = command => {
      if (command != openingMethod)
        return Task.CompletedTask;
      browser.OpenDialog();
      return stalled.Task;
    };
    using var first = new ViewerSocket();
    using var second = new ViewerSocket();
    using var end = new CancellationTokenSource();
    Task firstWatch = browser.WatchAsync(first, end.Token);
    Task secondWatch = Task.CompletedTask;
    try {
      await browser.ExpectAsync("Page.startScreencast");
      first.SendText("{\"type\":\"resize\",\"width\":640,\"height\":480}");
      await browser.ExpectResizeAsync("640x480");
      first.SendText(openingInput);
      await browser.ExpectAsync(openingMethod);
      Assert.NotNull(browser.Interaction.PendingDialog);
      // The click case also holds the dialog past the ten-second command deadline.
      if (checkTimeout)
        await Assert.ThrowsAsync<TimeoutException>(() => first.ServerClosing.Task.WaitAsync(TimeSpan.FromSeconds(11)));
      first.CloseFromClient();
      await firstWatch.WaitAsync(TestTimeout);
      Assert.Equal(WebSocketCloseStatus.NormalClosure, first.CloseStatus);

      secondWatch = browser.WatchAsync(second, end.Token);
      await browser.ExpectAsync("Page.stopScreencast");
      await browser.ExpectAsync("Page.startScreencast");
      Assert.Equal(1, browser.SessionCount);
      Assert.False(stalled.Task.IsCompleted);

      // More than the input queue can hold: these must be dropped, not retained for replay.
      for (int i = 0; i < 80; i++)
        second.SendText("{\"type\":\"text\",\"text\":\"drop me\"}");
      second.SendText("{\"type\":\"resize\",\"width\":800,\"height\":600}");
      await second.DrainReceivedAsync();
      Assert.True(await browser.Interaction.RespondDialogAsync(accept: true, promptText: null));
      second.SendText("{\"type\":\"text\",\"text\":\"after dialog\"}");
      await second.DrainReceivedAsync();
      await browser.ExpectNoCommandAsync();
      stalled.TrySetResult();
      await browser.ExpectAsync("InsertTextAsync:after dialog");
      await browser.ExpectNoCommandAsync();
      second.CloseFromClient();
      await secondWatch.WaitAsync(TestTimeout);
      Assert.Equal(WebSocketCloseStatus.NormalClosure, second.CloseStatus);
      await browser.ExpectAsync("Page.stopScreencast");
      await browser.ExpectAsync("DetachAsync");
      await browser.ExpectAsync("1280x800");
    } finally {
      stalled.TrySetResult();
      await end.CancelAsync();
      await DrainAsync(firstWatch, secondWatch);
    }
  }

  [Fact]
  public async Task RetirementWaitsForResizeBeforeRestoringAndStartingAgain() {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    var resize = Signal();
    browser.OnInput = command => command == "640x480" ? resize.Task : Task.CompletedTask;
    using var first = new ViewerSocket();
    using var second = new ViewerSocket();
    using var end = new CancellationTokenSource();
    Task firstWatch = browser.WatchAsync(first, end.Token);
    Task secondWatch = Task.CompletedTask;
    try {
      await browser.ExpectAsync("Page.startScreencast");
      first.SendText("{\"type\":\"resize\",\"width\":640,\"height\":480}");
      await browser.ExpectAsync("640x480");
      first.CloseFromClient();
      await firstWatch.WaitAsync(TestTimeout);
      await browser.ExpectAsync("Page.stopScreencast");

      secondWatch = browser.WatchAsync(second, end.Token);
      await browser.ExpectNoCommandAsync();
      resize.TrySetResult();
      await browser.ExpectAsync("DetachAsync");
      await browser.ExpectAsync("1280x800");
      await browser.ExpectAsync("Page.startScreencast");
    } finally {
      resize.TrySetResult();
      await end.CancelAsync();
      await DrainAsync(firstWatch, secondWatch);
    }
  }

  [Theory]
  [InlineData("Page.stopScreencast", false)]
  [InlineData("Page.startScreencast", false)]
  [InlineData("Page.startScreencast", true)]
  public async Task RetirementHandlesAnInFlightRestart(string heldCommand, bool timeOut) {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    var held = Signal();
    using var first = new ViewerSocket();
    using var second = new ViewerSocket();
    using var end = new CancellationTokenSource();
    Task firstWatch = browser.WatchAsync(first, end.Token);
    Task secondWatch = Task.CompletedTask;
    try {
      await browser.ExpectAsync("Page.startScreencast");
      int heldCalls = 0;
      browser.OnCdp = command => command == heldCommand && Interlocked.Increment(ref heldCalls) == 1
          ? held.Task : Task.CompletedTask;
      first.SendText("{\"type\":\"resize\",\"width\":640,\"height\":480}");
      await browser.ExpectAsync("640x480");
      await browser.ExpectAsync("Page.stopScreencast");
      if (heldCommand == "Page.startScreencast")
        await browser.ExpectAsync("Page.startScreencast");
      else
        browser.OpenDialog();
      if (timeOut) {
        await first.ServerClosing.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(WebSocketCloseStatus.InternalServerError, first.CloseStatus);
        first.CloseFromClient();
        await firstWatch.WaitAsync(TestTimeout);
        // The timed-out start can't leave the page waiting to retire or be reused.
        await browser.ExpectAsync("DetachAsync");
        await browser.ExpectAsync("1280x800");
      } else {
        first.CloseFromClient();
        await firstWatch.WaitAsync(TestTimeout);
        await browser.ExpectNoCommandAsync();
        held.TrySetResult();
        await browser.ExpectAsync("Page.stopScreencast");
        if (heldCommand == "Page.startScreencast") {
          await browser.ExpectAsync("DetachAsync");
          await browser.ExpectAsync("1280x800");
        }
      }
      secondWatch = browser.WatchAsync(second, end.Token);
      await browser.ExpectAsync("Page.startScreencast");
      Assert.Equal(heldCommand == "Page.stopScreencast" ? 1 : 2, browser.SessionCount);
      held.TrySetResult();
      second.CloseFromClient();
      await secondWatch.WaitAsync(TestTimeout);
      await browser.ExpectAsync("Page.stopScreencast");
    } finally {
      held.TrySetResult();
      await browser.Interaction.RespondDialogAsync(accept: true, promptText: null);
      await end.CancelAsync();
      await DrainAsync(firstWatch, secondWatch);
    }
  }

  [Fact]
  public async Task ReopenWaitsForAnIssuedDeferredRestore() {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    var click = Signal();
    var restore = Signal();
    browser.OnInput = command => {
      if (command == "1280x800")
        return restore.Task;
      if (command != "ClickAsync")
        return Task.CompletedTask;
      browser.OpenDialog();
      return click.Task;
    };
    using var first = new ViewerSocket();
    using var second = new ViewerSocket();
    using var third = new ViewerSocket();
    using var end = new CancellationTokenSource();
    Task firstWatch = browser.WatchAsync(first, end.Token);
    Task secondWatch = Task.CompletedTask;
    Task thirdWatch = Task.CompletedTask;
    try {
      await browser.ExpectAsync("Page.startScreencast");
      first.SendText("{\"type\":\"resize\",\"width\":640,\"height\":480}");
      await browser.ExpectResizeAsync("640x480");
      first.SendText("{\"type\":\"click\",\"x\":1,\"y\":2}");
      await browser.ExpectAsync("ClickAsync");
      first.CloseFromClient();
      await firstWatch.WaitAsync(TestTimeout);
      await browser.ExpectAsync("Page.stopScreencast");

      // A second generation confirms the first retired without restoring. Close it
      // without resizing so the pending restore is still owed by the page.
      secondWatch = browser.WatchAsync(second, end.Token);
      await browser.ExpectAsync("Page.startScreencast");
      second.CloseFromClient();
      await secondWatch.WaitAsync(TestTimeout);
      await browser.ExpectAsync("Page.stopScreencast");
      Assert.True(await browser.Interaction.RespondDialogAsync(accept: true, promptText: null));
      click.TrySetResult();
      await browser.ExpectAsync("1280x800");

      thirdWatch = browser.WatchAsync(third, end.Token);
      third.SendText("{\"type\":\"resize\",\"width\":800,\"height\":600}");
      await third.DrainReceivedAsync();
      await browser.ExpectNoCommandAsync();
      restore.TrySetResult();
      await browser.ExpectAsync("Page.startScreencast");
      await browser.ExpectResizeAsync("800x600");
    } finally {
      click.TrySetResult();
      restore.TrySetResult();
      await end.CancelAsync();
      await DrainAsync(firstWatch, secondWatch, thirdWatch);
    }
  }

  [Fact]
  public async Task FinalCloseDefersRestoreForADialogWithoutAViewerCommand() {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    using var socket = new ViewerSocket();
    using var end = new CancellationTokenSource();
    Task watching = browser.WatchAsync(socket, end.Token);
    try {
      await browser.ExpectAsync("Page.startScreencast");
      socket.SendText("{\"type\":\"resize\",\"width\":640,\"height\":480}");
      await browser.ExpectResizeAsync("640x480");
      browser.OpenDialog();
      socket.CloseFromClient();
      await watching.WaitAsync(TestTimeout);
      await browser.ExpectAsync("Page.stopScreencast");
      await browser.ExpectNoCommandAsync();
      Assert.True(await browser.Interaction.RespondDialogAsync(accept: true, promptText: null));
      await browser.ExpectAsync("1280x800");
    } finally {
      await browser.Interaction.RespondDialogAsync(accept: true, promptText: null);
      await end.CancelAsync();
      await DrainAsync(watching);
    }
  }

  [Fact]
  public async Task IdleViewerContinuesTouchingItsPageUntilClose() {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    using var socket = new ViewerSocket();
    using var end = new CancellationTokenSource();
    Task watching = browser.WatchAsync(socket, end.Token);
    try {
      await browser.ExpectAsync("Page.startScreencast");
      socket.SendText("{\"type\":\"text\",\"text\":\"ready\"}");
      await browser.ExpectAsync("InsertTextAsync:ready");
      BrowserPage<IPage> other = host.Pages.Register(browser.Page.OwnerId, Proxy<IPage>((_, _) => null)).Page;

      // Register makes the other page most recent. With no more input the active
      // viewer must still move its own page back to most recent.
      using var timeout = new CancellationTokenSource(TestTimeout);
      for (int i = 0; i < 2; i++) {
        Assert.True(host.Pages.Touch(other.OwnerId, other.Id));
        while (host.Pages.List(other.OwnerId)[^1] != browser.Page)
          await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
      }

      socket.CloseFromClient();
      await watching.WaitAsync(TestTimeout);
      Assert.True(host.Pages.Touch(other.OwnerId, other.Id));
      await Task.Delay(TimeSpan.FromMilliseconds(1200), end.Token);
      Assert.Equal(other, host.Pages.List(other.OwnerId)[^1]);
    } finally {
      await end.CancelAsync();
      await DrainAsync(watching);
    }
  }

  [Fact]
  public async Task StalledScreencastAcknowledgementsAreBounded() {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    var stalled = Signal();
    int acknowledgements = 0;
    browser.OnCdp = async command => {
      if (command != "Page.screencastFrameAck")
        return;
      Interlocked.Increment(ref acknowledgements);
      await stalled.Task;
    };
    using var socket = new ViewerSocket();
    using var end = new CancellationTokenSource();
    Task watching = browser.WatchAsync(socket, end.Token);
    try {
      await browser.ExpectAsync("Page.startScreencast");
      for (int i = 0; i < 100; i++)
        browser.EmitFrame(i);
      Assert.InRange(Volatile.Read(ref acknowledgements), 1, 16);
      await socket.ServerClosing.Task.WaitAsync(TestTimeout);
      Assert.Equal(WebSocketCloseStatus.InternalServerError, socket.CloseStatus);
    } finally {
      stalled.TrySetResult();
      await end.CancelAsync();
      await DrainAsync(watching);
    }
  }

  [Fact]
  public async Task QuietPageReceivesExactlyOneInitialFrame() {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    using var socket = new ViewerSocket();
    using var end = new CancellationTokenSource();
    Task watching = browser.WatchAsync(socket, end.Token);
    try {
      await browser.ExpectAsync("Page.startScreencast");
      // No compositor event: the stream stays empty until the bounded initial shot.
      using var beforeGrace = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
      await Assert.ThrowsAnyAsync<OperationCanceledException>(
          () => socket.Frames.Reader.WaitToReadAsync(beforeGrace.Token).AsTask());
      await browser.ExpectAsync("Page.captureScreenshot");
      using var frameArrived = new CancellationTokenSource(TestTimeout);
      Assert.Equal(ScreenshotBytes, await socket.Frames.Reader.ReadAsync(frameArrived.Token));
      // One shot only: a still quiet page is never polled for more.
      await Task.Delay(TimeSpan.FromMilliseconds(500), end.Token);
      await browser.ExpectNoCommandAsync();
      Assert.False(socket.Frames.Reader.TryRead(out _));
    } finally {
      await end.CancelAsync();
      await DrainAsync(watching);
    }
  }

  [Fact]
  public async Task CompositorFrameSuppressesTheInitialShot() {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    using var socket = new ViewerSocket();
    using var end = new CancellationTokenSource();
    Task watching = browser.WatchAsync(socket, end.Token);
    try {
      await browser.ExpectAsync("Page.startScreencast");
      browser.EmitFrame(7, Convert.ToBase64String([4, 5, 6]));
      using var frameArrived = new CancellationTokenSource(TestTimeout);
      Assert.Equal(new byte[] { 4, 5, 6 }, await socket.Frames.Reader.ReadAsync(frameArrived.Token));
      await browser.ExpectAsync("Page.screencastFrameAck");
      // The one-shot window passes: the real frame already seeded the stream.
      await Task.Delay(TimeSpan.FromMilliseconds(500), end.Token);
      await browser.ExpectNoCommandAsync();
      Assert.False(socket.Frames.Reader.TryRead(out _));
    } finally {
      await end.CancelAsync();
      await DrainAsync(watching);
    }
  }

  [Fact]
  public async Task InitialShotDoesNotPublishOverACompositorFrame() {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    var held = Signal();
    browser.OnCdp = command => command == "Page.captureScreenshot" ? held.Task : Task.CompletedTask;
    using var socket = new ViewerSocket();
    using var end = new CancellationTokenSource();
    Task watching = browser.WatchAsync(socket, end.Token);
    try {
      await browser.ExpectAsync("Page.startScreencast");
      await browser.ExpectAsync("Page.captureScreenshot");
      browser.EmitFrame(9, Convert.ToBase64String([5, 6, 7]));
      using var frameArrived = new CancellationTokenSource(TestTimeout);
      Assert.Equal(new byte[] { 5, 6, 7 }, await socket.Frames.Reader.ReadAsync(frameArrived.Token));
      await browser.ExpectAsync("Page.screencastFrameAck");
      // The stale screenshot resolves after the compositor frame and must not replace it.
      held.TrySetResult();
      await browser.ExpectNoCommandAsync();
      Assert.False(socket.Frames.Reader.TryRead(out _));
    } finally {
      held.TrySetResult();
      await end.CancelAsync();
      await DrainAsync(watching);
    }
  }

  [Fact]
  public async Task ModalSuppressesTheInitialShotWithoutPolling() {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    using var socket = new ViewerSocket();
    using var end = new CancellationTokenSource();
    Task watching = browser.WatchAsync(socket, end.Token);
    try {
      await browser.ExpectAsync("Page.startScreencast");
      browser.OpenDialog();
      // The one-shot window passes under the modal: no screenshot is issued and none is
      // queued behind it, nor retried once the dialog is gone.
      await Task.Delay(TimeSpan.FromMilliseconds(500), end.Token);
      await browser.ExpectNoCommandAsync();
      Assert.True(await browser.Interaction.RespondDialogAsync(accept: true, promptText: null));
      await Task.Delay(TimeSpan.FromMilliseconds(300), end.Token);
      await browser.ExpectNoCommandAsync();
      Assert.False(socket.Frames.Reader.TryRead(out _));
    } finally {
      await browser.Interaction.RespondDialogAsync(accept: true, promptText: null);
      await end.CancelAsync();
      await DrainAsync(watching);
    }
  }

  [Fact]
  public async Task RetiredStreamIsNotSeededByItsInitialShot() {
    using var database = new TemporaryDatabase();
    await using var host = new BrowserHost(Path.GetDirectoryName(database.Path)!, _ => Task.FromResult(false));
    var browser = new ViewerPage(host);
    var held = Signal();
    browser.OnCdp = command => command == "Page.captureScreenshot" ? held.Task : Task.CompletedTask;
    using var first = new ViewerSocket();
    using var second = new ViewerSocket();
    using var end = new CancellationTokenSource();
    Task firstWatch = browser.WatchAsync(first, end.Token);
    Task secondWatch = Task.CompletedTask;
    try {
      await browser.ExpectAsync("Page.startScreencast");
      await browser.ExpectAsync("Page.captureScreenshot");
      first.CloseFromClient();
      await firstWatch.WaitAsync(TestTimeout);
      await browser.ExpectAsync("Page.stopScreencast");
      await browser.ExpectAsync("DetachAsync");
      Assert.False(first.Frames.Reader.TryRead(out _));
      // The late screenshot resolves after its stream retired; the reopened generation
      // starts clean and gets its own initial frame.
      held.TrySetResult();
      browser.OnCdp = _ => Task.CompletedTask;
      secondWatch = browser.WatchAsync(second, end.Token);
      await browser.ExpectAsync("Page.startScreencast");
      using var frameArrived = new CancellationTokenSource(TestTimeout);
      Assert.Equal(ScreenshotBytes, await second.Frames.Reader.ReadAsync(frameArrived.Token));
      await browser.ExpectAsync("Page.captureScreenshot");
      second.CloseFromClient();
      await secondWatch.WaitAsync(TestTimeout);
      await browser.ExpectAsync("Page.stopScreencast");
      await browser.ExpectAsync("DetachAsync");
      Assert.Equal(2, browser.SessionCount);
    } finally {
      held.TrySetResult();
      await end.CancelAsync();
      await DrainAsync(firstWatch, secondWatch);
    }
  }

  private static async Task DrainAsync(params Task[] watching) =>
      await Task.WhenAll(watching).WaitAsync(TestTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

  private sealed class ViewerPage {
    private readonly BrowserViewerStreams streams = new();
    private readonly BrowserHost host;
    private readonly BrowserPage<IPage> registered;
    private readonly Channel<string> commands = Channel.CreateUnbounded<string>();
    private readonly List<EventHandler<IDialog>> dialogHandlers = [];
    private event EventHandler<JsonElement?>? FrameReceived;
    private int sessionCount;

    public ViewerPage(BrowserHost host) {
      this.host = host;
      ICDPSessionEvent frameEvent = Proxy<ICDPSessionEvent>((method, args) => {
        var handler = (EventHandler<JsonElement?>)args[0]!;
        if (method.Name == "add_OnEvent")
          FrameReceived += handler;
        else if (method.Name == "remove_OnEvent")
          FrameReceived -= handler;
        return null;
      });
      ICDPSession devTools = Proxy<ICDPSession>((method, args) => method.Name switch {
        "Event" => frameEvent,
        "SendAsync" => RecordCdp((string)args[0]!),
        "DetachAsync" => RecordCdp("DetachAsync"),
        _ => throw new NotSupportedException(method.Name)
      });
      IBrowserContext context = Proxy<IBrowserContext>((method, _) => {
        if (method.Name != "NewCDPSessionAsync")
          throw new NotSupportedException(method.Name);
        Interlocked.Increment(ref sessionCount);
        return Task.FromResult(devTools);
      });
      IMouse mouse = Proxy<IMouse>((method, _) => RecordInput(method.Name));
      IKeyboard keyboard = Proxy<IKeyboard>((method, args) => RecordInput(method.Name, args[0]));
      IPage page = Proxy<IPage>((method, args) => method.Name switch {
        "get_IsClosed" => false,
        "get_Context" => context,
        "get_Mouse" => mouse,
        "get_Keyboard" => keyboard,
        "SetViewportSizeAsync" => RecordInput($"{args[0]}x{args[1]}"),
        "add_Dialog" => AddDialogHandler((EventHandler<IDialog>)args[0]!),
        "remove_Dialog" => dialogHandlers.Remove((EventHandler<IDialog>)args[0]!),
        _ when method.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal) => null,
        _ => throw new NotSupportedException(method.Name)
      });
      ScreenshotData = Convert.ToBase64String(ScreenshotBytes);
      registered = host.Pages.Register(Guid.NewGuid(), page).Page;
      Interaction = BrowserInteraction.For(page);
    }

    public BrowserPage<IPage> Page => registered;
    public BrowserInteraction Interaction { get; }
    public int SessionCount => Volatile.Read(ref sessionCount);
    // Base64 payload returned for Page.captureScreenshot; null answers with nothing.
    public string? ScreenshotData { get; set; }
    public Func<string, Task> OnInput { get; set; } = _ => Task.CompletedTask;
    public Func<string, Task> OnCdp { get; set; } = _ => Task.CompletedTask;
    public void EmitFrame(int sessionId, string? data = null) =>
        FrameReceived?.Invoke(this, JsonSerializer.SerializeToElement(new { sessionId, data }));
    public Task WatchAsync(WebSocket socket, CancellationToken cancellation) =>
        streams.RunAsync(host, registered.OwnerId, registered, socket, cancellation);
    public async Task ExpectResizeAsync(string dimensions) {
      await ExpectAsync(dimensions);
      await ExpectAsync("Page.stopScreencast");
      await ExpectAsync("Page.startScreencast");
    }
    public async Task ExpectAsync(string expected) {
      using var timeout = new CancellationTokenSource(TestTimeout);
      Assert.Equal(expected, await commands.Reader.ReadAsync(timeout.Token));
    }
    public async Task ExpectNoCommandAsync() {
      using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commands.Reader.WaitToReadAsync(timeout.Token).AsTask());
    }

    public void OpenDialog() {
      IDialog dialog = Proxy<IDialog>((method, _) => method.Name switch {
        "get_Type" => "alert",
        "get_Message" => "dialog",
        "get_DefaultValue" => "",
        "AcceptAsync" => Task.CompletedTask,
        _ => throw new NotSupportedException(method.Name)
      });
      foreach (EventHandler<IDialog> handler in dialogHandlers.ToArray())
        handler(registered.Page, dialog);
    }

    private object? AddDialogHandler(EventHandler<IDialog> handler) {
      dialogHandlers.Add(handler);
      return null;
    }

    private async Task<JsonElement?> RecordCdp(string command) {
      commands.Writer.TryWrite(command);
      await OnCdp(command);
      return command == "Page.captureScreenshot" && ScreenshotData is { } encoded
          ? JsonSerializer.SerializeToElement(new { data = encoded })
          : null;
    }

    private Task RecordInput(string command, object? argument = null) {
      Task result = OnInput(command);
      commands.Writer.TryWrite(command == "InsertTextAsync" ? $"{command}:{argument}" : command);
      return result;
    }
  }

  private static T Proxy<T>(Func<MethodInfo, object?[], object?> invoke) where T : class {
    T proxy = DispatchProxy.Create<T, ViewerProxy>();
    ((ViewerProxy)(object)proxy).InvokeMethod = invoke;
    return proxy;
  }

  public class ViewerProxy : DispatchProxy {
    public Func<MethodInfo, object?[], object?> InvokeMethod { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeMethod(targetMethod!, args ?? []);
  }

  private sealed class ViewerSocket : WebSocket {
    private readonly Channel<SocketMessage> incoming = Channel.CreateUnbounded<SocketMessage>();
    private WebSocketState state = WebSocketState.Open;
    private WebSocketCloseStatus? lastCloseStatus;

    public TaskCompletionSource ServerClosing { get; } = Signal();
    public override WebSocketCloseStatus? CloseStatus => lastCloseStatus;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;
    public void SendText(string text) => incoming.Writer.TryWrite(new(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text));
    public void CloseFromClient() => incoming.Writer.TryWrite(new([], WebSocketMessageType.Close));
    public async Task DrainReceivedAsync() {
      var drained = Signal();
      incoming.Writer.TryWrite(new([], WebSocketMessageType.Text, drained));
      await drained.Task.WaitAsync(TestTimeout);
    }
    public override void Abort() => state = WebSocketState.Aborted;
    public override void Dispose() => state = WebSocketState.Closed;
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) {
      lastCloseStatus = closeStatus;
      state = WebSocketState.CloseSent;
      ServerClosing.TrySetResult();
      return Task.CompletedTask;
    }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken) {
      Frames.Writer.TryWrite(buffer.ToArray());
      return Task.CompletedTask;
    }
    public Channel<byte[]> Frames { get; } = Channel.CreateUnbounded<byte[]>();
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
      SocketMessage message;
      do {
        message = await incoming.Reader.ReadAsync(cancellationToken);
        message.Drained?.TrySetResult();
      } while (message.Drained is not null);
      message.Bytes.CopyTo(buffer.AsMemory());
      return new WebSocketReceiveResult(message.Bytes.Length, message.Type, true);
    }

    private sealed record SocketMessage(byte[] Bytes, WebSocketMessageType Type, TaskCompletionSource? Drained = null);
  }
}
