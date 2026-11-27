using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Playwright;
using Urd.Core.Tools;

namespace Urd.Server;

// One Chromium screencast per viewed page. The server sends binary JPEGs; each viewer keeps
// only its newest unsent frame. Clients send JSON text: resize {width,height}, click {x,y},
// wheel {dx,dy,x,y}, key {key}, or text {text}. Coordinates are viewport CSS pixels.
internal sealed class BrowserViewerStreams {
  private const int MaxViewers = 16;
  private const int MaxViewersPerPage = 4;
  private const int MaxMessageBytes = 16 * 1024;
  private const int MaxEncodedFrameLength = 4 * 1024 * 1024;
  private const int MaxPendingInputs = 64;
  private const int MaxPendingFrameAcks = 16;
  private const int MaxCoordinate = 8192;
  private const int MaxWheelDelta = 10_000;
  private const int MinViewport = 240;
  private const int MaxViewport = 3840;
  private const int DefaultWidth = 1280;
  private const int DefaultHeight = 800;
  private const int FrameBound = MaxViewport;
  private const int FrameQuality = 70;
  private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);
  private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
  private static readonly TimeSpan PlaywrightTimeout = TimeSpan.FromSeconds(10);
  private static readonly TimeSpan ActivityInterval = TimeSpan.FromSeconds(1);
  private static readonly TimeSpan InitialFrameGrace = TimeSpan.FromMilliseconds(300);
  private static readonly CloseReason PageClosed = new(WebSocketCloseStatus.NormalClosure, "The page closed.");
  private static readonly CloseReason BrowserTimedOut = new(WebSocketCloseStatus.InternalServerError, "The browser did not respond.");

  private readonly Lock gate = new();
  private readonly Dictionary<Guid, PageStream> streams = [];
  // An issued command keeps the input lane across screencast generations.
  private readonly ConditionalWeakTable<IPage, PageInput> pageInputs = new();
  private int viewerCount;

  internal static BrowserViewerStreams Shared { get; } = new();

  internal async Task RunAsync(BrowserHost host, Guid ownerId, BrowserPage<IPage> page, WebSocket socket, CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(host);
    ArgumentNullException.ThrowIfNull(page);
    ArgumentNullException.ThrowIfNull(socket);
    if (page.OwnerId != ownerId)
      throw new ArgumentException("The page belongs to another owner.", nameof(page));

    if (!TryReserveViewer()) {
      await CloseQuietlyAsync(socket, new(WebSocketCloseStatus.PolicyViolation, "Too many browser viewers are open."));
      return;
    }
    try {
      await WatchAsync(host, ownerId, page, socket, cancellation);
    } finally {
      lock (gate)
        viewerCount--;
    }
  }

  private bool TryReserveViewer() {
    lock (gate) {
      if (viewerCount >= MaxViewers)
        return false;
      viewerCount++;
      return true;
    }
  }

  // Read the socket even while waiting for the page or its input lane. A close or receive
  // failure cancels that wait rather than waiting for the input queue to drain.
  private async Task WatchAsync(BrowserHost host, Guid ownerId, BrowserPage<IPage> page, WebSocket socket,
      CancellationToken cancellation) {
    var viewer = new Viewer();
    Channel<QueuedInput> inputs = Channel.CreateBounded<QueuedInput>(new BoundedChannelOptions(MaxPendingInputs) {
      SingleReader = true,
      SingleWriter = true
    });
    using var receiveEnd = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    using var watchEnd = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    PageInput pageInput;
    lock (gate)
      pageInput = pageInputs.GetValue(page.Page, static live => new PageInput(live));
    Task receiving = ReceiveAsync(socket, viewer, pageInput, inputs.Writer, receiveEnd.Token);
    Task applying = ApplyAsync(host, ownerId, page, viewer, pageInput, inputs.Reader, watchEnd.Token);
    Task sending = SendAsync(socket, viewer, watchEnd.Token);

    await Task.WhenAny(receiving, applying, sending, viewer.Ended.Task);
    await watchEnd.CancelAsync();
    await Task.WhenAll(sending, applying).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    CloseReason close = page.Page.IsClosed ? PageClosed : new(WebSocketCloseStatus.NormalClosure, "");
    if (viewer.Ended.Task.IsCompletedSuccessfully)
      close = viewer.Ended.Task.Result;
    await CloseQuietlyAsync(socket, close);
    receiveEnd.CancelAfter(CloseTimeout);
    await receiving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    foreach (Task task in new[] { receiving, applying, sending }) {
      if (task.Exception?.InnerException is { } failure)
        ExceptionDispatchInfo.Throw(failure);
    }
  }

  private async Task<PageStream?> JoinAsync(BrowserPage<IPage> page, Viewer viewer, PageInput pageInput,
      CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    // A claimed viewport restore or in-flight retirement can block admission; wait for that
    // specific blocker and check the page again before joining.
    PageStream? stream;
    Task? blocker;
    lock (gate)
      (stream, blocker) = TryAdmit(page, viewer, pageInput);
    while (blocker is not null) {
      // A restore only blocks admission once it claims the viewport. An unclaimed restore
      // can wait for the next last viewer to leave instead.
      await blocker.WaitAsync(PlaywrightTimeout, cancellation);
      cancellation.ThrowIfCancellationRequested();
      lock (gate)
        (stream, blocker) = TryAdmit(page, viewer, pageInput);
    }
    return stream;
  }

  // Caller holds the gate. Returns the joined stream, or the admission blocker to await;
  // both null means the viewer ended without joining.
  private (PageStream? Stream, Task? Blocker) TryAdmit(BrowserPage<IPage> page, Viewer viewer, PageInput pageInput) {
    if (page.Page.IsClosed) {
      viewer.End(PageClosed);
      return (null, null);
    }
    if (!pageInput.RestoringViewport.IsCompleted)
      return (null, pageInput.RestoringViewport);
    if (!streams.TryGetValue(page.Id, out PageStream? stream))
      stream = CreateStream(page, pageInput);
    else if (stream.Retiring is { } retiring)
      return (null, retiring.Task);
    if (stream is null) {
      viewer.End(PageClosed);
      return (null, null);
    }
    if (stream.Viewers.Count >= MaxViewersPerPage) {
      viewer.End(new(WebSocketCloseStatus.PolicyViolation, "This page has too many viewers."));
      return (null, null);
    }
    stream.Viewers.Add(viewer);
    if (stream.Latest is { } latest)
      viewer.Outgoing.Writer.TryWrite(latest);
    return (stream, null);
  }

  // Caller holds the gate. Subscribe before checking again so a concurrent close isn't lost.
  private PageStream? CreateStream(BrowserPage<IPage> page, PageInput input) {
    var stream = new PageStream(page, input);
    stream.CloseHandler = (_, _) => OnPageClosed(stream);
    page.Page.Close += stream.CloseHandler;
    if (page.Page.IsClosed) {
      page.Page.Close -= stream.CloseHandler;
      return null;
    }
    streams.Add(page.Id, stream);
    stream.Starting = StartAsync(stream);
    return stream;
  }

  private async Task<ICDPSession?> StartAsync(PageStream stream) {
    // This task is installed under the gate. Only run browser work after releasing it.
    await Task.Yield();
    lock (gate) {
      if (stream.Closed || stream.Retiring is not null)
        return null;
    }
    ICDPSession? devTools = stream.Input.ParkedSession;
    stream.Input.ParkedSession = null;
    try {
      IPage page = stream.Page.Page;
      devTools ??= await page.Context.NewCDPSessionAsync(page);
      ICDPSession frames = devTools;
      // A headless tab can have a smaller compositor surface than its layout viewport.
      // Align capture on this target only, before subscribing; never resize the page to the viewer.
      if (page.ViewportSize is { } viewport)
        await frames.SendAsync("Emulation.setVisibleSize", new Dictionary<string, object> {
          ["width"] = viewport.Width, ["height"] = viewport.Height
        }).WaitAsync(PlaywrightTimeout);
      ICDPSessionEvent frameEvent = devTools.Event("Page.screencastFrame");
      EventHandler<JsonElement?> handler = (_, frame) => OnFrame(stream, frames, frame);
      frameEvent.OnEvent += handler;
      stream.UnsubscribeFrames = () => frameEvent.OnEvent -= handler;
      if (await TryCdpAsync(() => StartScreencastAsync(frames))) {
        // Chromium only streams frames on compositor damage, so a quiet page would otherwise
        // stream nothing. One bounded screenshot after a grace covers that; no polling.
        stream.FirstFrameEnd = new();
        ObserveFailure(FirstFrameAsync(stream, frames, stream.FirstFrameEnd.Token));
        return frames;
      }
    } catch (Exception error) when (error is TimeoutException || IsBrowserFailure(error)) {
      // The page or browser may have closed before the session was ready.
    }
    stream.UnsubscribeFrames?.Invoke();
    stream.UnsubscribeFrames = null;
    if (devTools is not null)
      await TryCdpAsync(devTools.DetachAsync);
    return null;
  }

  private static Task<JsonElement?> StartScreencastAsync(ICDPSession devTools) =>
      devTools.SendAsync("Page.startScreencast", new Dictionary<string, object> {
        ["format"] = "jpeg",
        ["quality"] = FrameQuality,
        ["maxWidth"] = FrameBound,
        ["maxHeight"] = FrameBound
      });

  private void OnFrame(PageStream stream, ICDPSession devTools, JsonElement? frame) {
    if (frame is not { ValueKind: JsonValueKind.Object } payload)
      return;
    if (payload.TryGetProperty("sessionId", out JsonElement session) && session.TryGetInt32(out int sessionId)) {
      lock (gate) {
        if (stream.Closed || stream.Retiring is not null)
          return;
        // A stopped session may be reused on reopen. Bound acknowledgements across generations.
        if (stream.Input.PendingFrameAcks >= MaxPendingFrameAcks) {
          foreach (Viewer viewer in stream.Viewers)
            viewer.End(BrowserTimedOut);
          return;
        }
        stream.Input.PendingFrameAcks++;
      }
      ObserveFailure(AckFrameAsync(stream.Input, devTools, sessionId));
    }
    if (DecodeFrame(payload) is not { } jpeg)
      return;
    lock (gate) {
      if (stream.Closed || stream.Retiring is not null)
        return;
      stream.Latest = jpeg;
      stream.FirstFrameEnd?.Cancel();
      foreach (Viewer viewer in stream.Viewers)
        viewer.Outgoing.Writer.TryWrite(jpeg);
    }
  }

  // Chromium streams screencast frames only on compositor damage, so a page that stays
  // quiet leaves its viewers blank. After a short grace with no frame, take one screenshot
  // and publish it if the stream still has none. This is one attempt, never polled: the
  // next compositor damage restarts the real stream of frames.
  private async Task FirstFrameAsync(PageStream stream, ICDPSession devTools, CancellationToken cancellation) {
    try {
      await Task.Delay(InitialFrameGrace, cancellation);
    } catch (OperationCanceledException) {
      return;
    }
    lock (gate) {
      // A modal may have stalled the browser work; issuing a screenshot there would queue
      // behind it for the input lane. The one-shot skips rather than waits.
      if (stream.Closed || stream.Retiring is not null || stream.Latest is not null || IsInputBlocked(stream.Input))
        return;
    }
    JsonElement? screenshot;
    try {
      screenshot = await devTools.SendAsync("Page.captureScreenshot", new Dictionary<string, object> {
        ["format"] = "jpeg",
        ["quality"] = FrameQuality
      });
    } catch (Exception error) when (IsBrowserFailure(error)) {
      return;
    }
    if (screenshot is not { } response || DecodeFrame(response) is not { } jpeg)
      return;
    lock (gate) {
      // A compositor frame or retirement during the screenshot wins; never publish over it.
      if (stream.Closed || stream.Retiring is not null || stream.Latest is not null || IsInputBlocked(stream.Input))
        return;
      stream.Latest = jpeg;
      foreach (Viewer viewer in stream.Viewers)
        viewer.Outgoing.Writer.TryWrite(jpeg);
    }
  }

  private async Task AckFrameAsync(PageInput input, ICDPSession devTools, int sessionId) {
    try {
      await QuietlyAsync(() => devTools.SendAsync("Page.screencastFrameAck", new Dictionary<string, object> { ["sessionId"] = sessionId }));
    } finally {
      lock (gate)
        input.PendingFrameAcks--;
    }
  }

  private static byte[]? DecodeFrame(JsonElement frame) {
    if (frame.ValueKind != JsonValueKind.Object
        || !frame.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.String
        || data.GetString() is not { Length: > 0 and <= MaxEncodedFrameLength } encoded)
      return null;
    try {
      return Convert.FromBase64String(encoded);
    } catch (FormatException) {
      return null;
    }
  }

  private void OnPageClosed(PageStream stream) {
    lock (gate) {
      if (stream.Closed)
        return;
      stream.Closed = true;
      stream.Latest = null;
      Forget(stream);
      foreach (Viewer viewer in stream.Viewers)
        viewer.End(PageClosed);
      stream.Retiring?.TrySetResult();
    }
  }

  private void Leave(PageStream stream, Viewer viewer) {
    TaskCompletionSource retiring;
    lock (gate) {
      stream.Viewers.Remove(viewer);
      if (stream.Viewers.Count > 0 || stream.Closed)
        return;
      stream.Latest = null;
      retiring = new(TaskCreationOptions.RunContinuationsAsynchronously);
      stream.Retiring = retiring;
    }
    ObserveFailure(RetireAsync(stream, retiring));
  }

  private async Task RetireAsync(PageStream stream, TaskCompletionSource retired) {
    try {
      ICDPSession? devTools = await stream.Starting;
      stream.UnsubscribeFrames?.Invoke();
      // Retirement prevents a pending viewport change from issuing a restart. If a
      // restart was already issued, finish that transition before sending the final stop.
      bool canStop = await stream.Restarting;
      bool stopped = canStop && devTools is not null && !stream.Page.Page.IsClosed
          && await TryCdpAsync(() => devTools.SendAsync("Page.stopScreencast"));
      // A viewport change still applying must finish before admitting another generation
      // or restoring the viewport. Don't wait for unrelated input, which may open a modal.
      await stream.LastResize.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      if (devTools is not null && !stream.Page.Page.IsClosed)
        await DetachOrParkAsync(stream.Input, devTools, stopped);
    } finally {
      lock (gate) {
        if (Forget(stream)) {
          stream.Input.NeedsViewportRestore |= stream.Resized;
          RestoreIfNeeded(stream.Page, stream.Input);
        }
        retired.TrySetResult();
      }
    }
  }

  private static bool IsInputBlocked(PageInput input) =>
      !input.Command.IsCompleted || input.Interaction.PendingDialog is not null;

  private static async Task DetachOrParkAsync(PageInput input, ICDPSession devTools, bool stopped) {
    // Chromium can stop and start during a modal, but detach can stall. Only reuse a
    // cleanly stopped session: a timed-out CDP command can still complete on the old one.
    if (stopped && IsInputBlocked(input))
      input.ParkedSession = devTools;
    else
      await TryCdpAsync(devTools.DetachAsync);
  }

  // Caller holds the gate after removing the last stream. Keep one deferred restore per page,
  // separate from the input command so repeated reopens cannot build a chain of restores.
  private void RestoreIfNeeded(BrowserPage<IPage> page, PageInput input) {
    if (!input.NeedsViewportRestore || page.Page.IsClosed)
      return;
    if (!IsInputBlocked(input)) {
      BeginRestore(page.Page, input);
    } else if (!input.WaitingToRestore) {
      input.WaitingToRestore = true;
      ObserveFailure(RestoreWhenUnblockedAsync(page, input));
    }
  }

  private async Task RestoreWhenUnblockedAsync(BrowserPage<IPage> page, PageInput input) {
    await Task.Yield();
    // Keep rechecking the block until the restore has begun or the need has ended. A dialog
    // can have come from outside the viewer's input lane, with no pending command to await:
    // recheck it and page closure without issuing work to a blocked browser.
    while (true) {
      (bool Blocked, Task Command) pending;
      lock (gate)
        pending = PendingRestoreCommand(page, input);
      if (!pending.Blocked)
        return;
      if (pending.Command.IsCompleted)
        await Task.Delay(ActivityInterval);
      else
        await pending.Command.WaitAsync(ActivityInterval).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
  }

  // Caller holds the gate. Returns the command currently blocking the input lane; not
  // blocked means the restore need has ended or the restore has begun.
  private (bool Blocked, Task Command) PendingRestoreCommand(BrowserPage<IPage> page, PageInput input) {
    if (!input.NeedsViewportRestore || page.Page.IsClosed || streams.ContainsKey(page.Id)) {
      input.WaitingToRestore = false;
      return (false, Task.CompletedTask);
    }
    if (!IsInputBlocked(input)) {
      input.WaitingToRestore = false;
      BeginRestore(page.Page, input);
      return (false, Task.CompletedTask);
    }
    return (true, input.Command);
  }

  // Caller holds the same gate as JoinAsync: once claimed, the restore must finish before
  // another viewer joins, even if BrowserInteraction queues the resize behind a screenshot.
  private static void BeginRestore(IPage page, PageInput input) {
    input.NeedsViewportRestore = false;
    input.RestoringViewport = RestoreViewportAsync(page, input.Interaction);
    ObserveFailure(input.RestoringViewport);
  }

  private static async Task RestoreViewportAsync(IPage page, BrowserInteraction interaction) {
    await Task.Yield();
    if (!page.IsClosed)
      await QuietlyAsync(() => interaction.ResizeAsync(DefaultWidth, DefaultHeight));
  }

  // Caller holds the gate. An old close callback or teardown can't remove a newer stream.
  private bool Forget(PageStream stream) {
    if (streams.GetValueOrDefault(stream.Page.Id) != stream)
      return false;
    streams.Remove(stream.Page.Id);
    stream.Page.Page.Close -= stream.CloseHandler;
    stream.FirstFrameEnd?.Cancel();
    return true;
  }

  private static async Task SendAsync(WebSocket socket, Viewer viewer, CancellationToken cancellation) {
    using var timeout = new CancellationTokenSource();
    try {
      await foreach (byte[] jpeg in viewer.Outgoing.Reader.ReadAllAsync(cancellation)) {
        cancellation.ThrowIfCancellationRequested();
        // Let an in-flight send finish before closing. Canceling it on page close could abort
        // the WebSocket instead of letting CloseOutputAsync send a close frame.
        timeout.CancelAfter(SendTimeout);
        await socket.SendAsync(jpeg, WebSocketMessageType.Binary, endOfMessage: true, timeout.Token);
        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
      }
    } catch (WebSocketException) {
      // The connection is gone; nothing more can be sent.
    } catch (OperationCanceledException) when (timeout.IsCancellationRequested) {
      viewer.End(new(WebSocketCloseStatus.PolicyViolation, "The viewer stopped receiving frames."));
    }
  }

  private static async Task ReceiveAsync(WebSocket socket, Viewer viewer, PageInput pageInput,
      ChannelWriter<QueuedInput> inputs, CancellationToken cancellation) {
    byte[] buffer = new byte[MaxMessageBytes];
    try {
      while (await ReceiveMessageAsync(socket, viewer, buffer, cancellation) is int length) {
        Task dialog = pageInput.DialogOpened;
        ViewerInput? input = ViewerInput.Parse(buffer.AsMemory(0, length));
        if (input is null) {
          viewer.End(new(WebSocketCloseStatus.InvalidPayloadData, "Viewer messages are JSON objects with a type."));
          return;
        }
        // Don't replay queued input across a modal, even if it opens during admission.
        if (input.Kind != InputKind.Unknown && pageInput.Interaction.PendingDialog is null && dialog == pageInput.DialogOpened
            && !inputs.TryWrite(new(input, dialog))) {
          viewer.End(new(WebSocketCloseStatus.PolicyViolation, "Too many browser inputs are pending."));
          return;
        }
      }
    } catch (WebSocketException) {
      // The connection is gone; the viewer ends with it.
    } finally {
      inputs.TryComplete();
    }
  }

  private static async Task<int?> ReceiveMessageAsync(WebSocket socket, Viewer viewer, byte[] buffer, CancellationToken cancellation) {
    int length = 0;
    ValueWebSocketReceiveResult received;
    do {
      if (length == buffer.Length) {
        viewer.End(new(WebSocketCloseStatus.MessageTooBig, "Viewer messages are limited to 16 KiB."));
        return null;
      }
      received = await socket.ReceiveAsync(buffer.AsMemory(length), cancellation);
      if (received.MessageType == WebSocketMessageType.Close)
        return null;
      if (received.MessageType != WebSocketMessageType.Text) {
        viewer.End(new(WebSocketCloseStatus.InvalidMessageType, "Viewer messages are JSON text."));
        return null;
      }
      length += received.Count;
    } while (!received.EndOfMessage);
    return length;
  }

  private async Task ApplyAsync(BrowserHost host, Guid ownerId, BrowserPage<IPage> page, Viewer viewer,
      PageInput pageInput, ChannelReader<QueuedInput> inputs, CancellationToken cancellation) {
    PageStream? stream = null;
    using var renewalEnd = new CancellationTokenSource();
    Task renewing = Task.CompletedTask;
    try {
      stream = await JoinAsync(page, viewer, pageInput, cancellation);
      if (stream is null)
        return;
      renewing = RenewWhileWatchingAsync(host, ownerId, page.Id, renewalEnd.Token);
      if (await stream.Starting.WaitAsync(PlaywrightTimeout, cancellation) is null) {
        viewer.End(stream.Page.Page.IsClosed ? PageClosed : new(WebSocketCloseStatus.InternalServerError, "The page could not be streamed."));
        return;
      }
      await foreach (QueuedInput queued in inputs.ReadAllAsync(cancellation)) {
        if (queued.Input.Kind == InputKind.Resize && inputs.TryPeek(out QueuedInput next) && next.Input.Kind == InputKind.Resize)
          continue;
        await ApplyInputAsync(stream, queued, cancellation);
      }
    } catch (TimeoutException) {
      viewer.End(BrowserTimedOut);
    } finally {
      if (stream is not null) {
        await renewalEnd.CancelAsync();
        await renewing;
        Leave(stream, viewer);
      }
    }
  }

  private async Task ApplyInputAsync(PageStream stream, QueuedInput queued, CancellationToken cancellation) {
    (ViewerInput input, Task dialog) = queued;
    BrowserInteraction interaction = stream.Input.Interaction;
    using var waitEnd = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    Task deadline = Task.Delay(PlaywrightTimeout, waitEnd.Token);
    Task command;
    bool issued;
    try {
      // Another input can own the lane; keep waiting for its command before issuing ours.
      do {
        lock (gate) {
          cancellation.ThrowIfCancellationRequested();
          if (stream.Closed || interaction.PendingDialog is not null || dialog != stream.Input.DialogOpened)
            return;
          command = stream.Input.Command;
          issued = command.IsCompleted;
          if (issued) {
            command = RunInputAsync(stream, input, interaction, dialog, cancellation);
            stream.Input.Command = command;
            if (input.Kind == InputKind.Resize)
              stream.LastResize = command;
            ObserveFailure(command);
          }
        }

        // A modal ends the viewer's wait, not the Playwright command. Keep its task in
        // the page lane so even a reopened viewer cannot overlap it once input resumes.
        Task finished = await Task.WhenAny(command, dialog, deadline);
        cancellation.ThrowIfCancellationRequested();
        if (dialog != stream.Input.DialogOpened)
          return;
        if (finished == deadline)
          throw new TimeoutException();
      } while (!issued);
      await command;
    } finally {
      await waitEnd.CancelAsync();
    }
  }

  private async Task RunInputAsync(PageStream stream, ViewerInput input, BrowserInteraction interaction,
      Task dialog, CancellationToken cancellation) {
    // Install the real command in the lane before issuing browser work.
    await Task.Yield();
    try {
      cancellation.ThrowIfCancellationRequested();
      IPage page = stream.Page.Page;
      if (page.IsClosed || interaction.PendingDialog is not null || dialog != stream.Input.DialogOpened)
        return;
      switch (input.Kind) {
        case InputKind.Click:
          await page.Mouse.ClickAsync(input.X, input.Y);
          break;
        case InputKind.Wheel:
          await page.Mouse.MoveAsync(input.X, input.Y);
          cancellation.ThrowIfCancellationRequested();
          if (interaction.PendingDialog is null && dialog == stream.Input.DialogOpened)
            await page.Mouse.WheelAsync(input.Dx, input.Dy);
          break;
        case InputKind.Key:
          await page.Keyboard.PressAsync(input.Text);
          break;
        case InputKind.Text:
          await page.Keyboard.InsertTextAsync(input.Text);
          break;
        case InputKind.Resize:
          stream.Resized = true;
          await interaction.ResizeAsync(input.Width, input.Height);
          await RestartAfterResizeAsync(stream);
          break;
      }
    } catch (Exception error) when (IsBrowserFailure(error)) {
      // An invalid key or a closing page needn't end the stream.
    }
  }

  private async Task RestartAfterResizeAsync(PageStream stream) {
    if (await stream.Starting is not { } devTools)
      return;
    Task<bool> restarting;
    lock (gate) {
      if (stream.Closed || stream.Retiring is not null || stream.RestartFailed)
        return;
      // Chromium can keep scaling damage to the old capture surface after a viewport
      // change. Restart on the same session, in the lane that orders viewer inputs.
      restarting = stream.Restarting = RestartScreencastAsync(stream, devTools);
    }
    if (!await restarting) {
      lock (gate) {
        stream.RestartFailed = true;
        foreach (Viewer viewer in stream.Viewers)
          viewer.End(BrowserTimedOut);
      }
    }
  }

  private async Task<bool> RestartScreencastAsync(PageStream stream, ICDPSession devTools) {
    await Task.Yield();
    if (!await TryCdpAsync(() => devTools.SendAsync("Page.stopScreencast")))
      return false;
    lock (gate) {
      if (stream.Closed || stream.Retiring is not null)
        return true;
    }
    return await TryCdpAsync(() => StartScreencastAsync(devTools));
  }

  private static async Task<bool> TryCdpAsync(Func<Task> issue) {
    try {
      Task command = issue();
      ObserveFailure(command);
      await command.WaitAsync(PlaywrightTimeout);
      return true;
    } catch (Exception error) when (error is TimeoutException || IsBrowserFailure(error)) {
      return false;
    }
  }

  private static async Task RenewWhileWatchingAsync(BrowserHost host, Guid ownerId, Guid pageId, CancellationToken cancellation) {
    using var timer = new PeriodicTimer(ActivityInterval);
    try {
      do {
        host.Touch(ownerId, pageId);
      } while (await timer.WaitForNextTickAsync(cancellation));
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      // The viewer is gone.
    } catch (ObjectDisposedException) {
      // The host is shutting down and closing every page.
    }
  }

  private static async Task CloseQuietlyAsync(WebSocket socket, CloseReason reason) {
    if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
      return;
    using var timeout = new CancellationTokenSource(CloseTimeout);
    try {
      await socket.CloseOutputAsync(reason.Status, reason.Description, timeout.Token);
    } catch (Exception error) when (error is WebSocketException or OperationCanceledException) {
      // The viewer is already gone.
    }
  }

  private static async Task QuietlyAsync(Func<Task> action) {
    try {
      await action();
    } catch (Exception error) when (IsBrowserFailure(error)) {
      // The page or its browser may already be gone.
    }
  }

  private static bool IsBrowserFailure(Exception error) => error is PlaywrightException or InvalidOperationException;

  private static void ObserveFailure(Task work) => _ = work.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

  private sealed record CloseReason(WebSocketCloseStatus Status, string Description);

  private sealed class PageInput {
    private TaskCompletionSource dialogOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PageInput(IPage page) {
      Interaction = BrowserInteraction.For(page);
      page.Dialog += OnDialog;
    }

    public BrowserInteraction Interaction { get; }
    public ICDPSession? ParkedSession { get; set; }
    public Task Command { get; set; } = Task.CompletedTask;
    public Task RestoringViewport { get; set; } = Task.CompletedTask;
    public bool NeedsViewportRestore { get; set; }
    public bool WaitingToRestore { get; set; }
    public int PendingFrameAcks { get; set; }
    public Task DialogOpened => Volatile.Read(ref dialogOpened).Task;

    private void OnDialog(object? sender, IDialog dialog) =>
        Interlocked.Exchange(ref dialogOpened, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
  }

  private sealed class PageStream(BrowserPage<IPage> page, PageInput input) {
    public BrowserPage<IPage> Page { get; } = page;
    public List<Viewer> Viewers { get; } = [];
    public PageInput Input { get; } = input;
    public EventHandler<IPage>? CloseHandler { get; set; }
    public Action? UnsubscribeFrames { get; set; }
    public Task LastResize { get; set; } = Task.CompletedTask;
    public Task<bool> Restarting { get; set; } = Task.FromResult(true);
    public bool RestartFailed { get; set; }
    public Task<ICDPSession?> Starting { get; set; } = null!;
    public TaskCompletionSource? Retiring { get; set; }
    public CancellationTokenSource? FirstFrameEnd { get; set; }
    public byte[]? Latest { get; set; }
    public bool Closed { get; set; }
    public bool Resized { get; set; }
  }

  private sealed class Viewer {
    public Channel<byte[]> Outgoing { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true
    });
    public TaskCompletionSource<CloseReason> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void End(CloseReason reason) => Ended.TrySetResult(reason);
  }

  private readonly record struct QueuedInput(ViewerInput Input, Task DialogOpened);

  private enum InputKind { Unknown, Resize, Click, Wheel, Key, Text }

  // Null means malformed JSON/envelope; unsupported or incomplete commands are ignored.
  private sealed record ViewerInput(InputKind Kind, float X = 0, float Y = 0, float Dx = 0, float Dy = 0,
      int Width = 0, int Height = 0, string Text = "") {
    public static ViewerInput? Parse(ReadOnlyMemory<byte> json) {
      try {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String)
          return null;
        return type.GetString() switch {
          "resize" when ReadNumber(root, "width", MinViewport, MaxViewport, out float width)
              && ReadNumber(root, "height", MinViewport, MaxViewport, out float height) =>
              new(InputKind.Resize, Width: (int)width, Height: (int)height),
          "click" when ReadPoint(root, out float x, out float y) => new(InputKind.Click, x, y),
          "wheel" when ReadPoint(root, out float x, out float y)
              && ReadNumber(root, "dx", -MaxWheelDelta, MaxWheelDelta, out float dx)
              && ReadNumber(root, "dy", -MaxWheelDelta, MaxWheelDelta, out float dy) => new(InputKind.Wheel, x, y, dx, dy),
          "key" when ReadString(root, "key") is { Length: > 0 } key => new(InputKind.Key, Text: key),
          "text" when ReadString(root, "text") is { Length: > 0 } text => new(InputKind.Text, Text: text),
          _ => new(InputKind.Unknown)
        };
      } catch (Exception error) when (error is JsonException or InvalidOperationException) {
        return null;
      }
    }

    private static bool ReadPoint(JsonElement root, out float x, out float y) {
      bool hasX = ReadNumber(root, "x", 0, MaxCoordinate, out x);
      bool hasY = ReadNumber(root, "y", 0, MaxCoordinate, out y);
      return hasX && hasY;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ReadNumber(JsonElement root, string name, float min, float max, out float result) {
      result = 0;
      if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number
          || !value.TryGetDouble(out double number) || !double.IsFinite(number))
        return false;
      result = (float)Math.Clamp(number, min, max);
      return true;
    }
  }
}
