using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Per-page interaction state attached once when a page registers with BrowserHost. It records
// dialogs but never dismisses them: an open dialog stays pending for the tool's dialog action,
// while other actions on the page are refused rather than dismissing silently. It also collects
// console errors and failed requests for the tool to report, and owns the page's viewport: every
// resize advances an epoch, and a coordinate click may only use a screenshot from the current
// epoch, so a resized page cannot redirect click_at. A dialog appearance is raised as a signal
// too, so the tool can abandon in-flight commands that would otherwise wait on the scripting a
// dialog blocks.
internal sealed class BrowserInteraction : IAsyncDisposable {
  private const int EventLimit = 40;
  private const int MessageLimit = 500;
  // Bounding for the dedupe sets; cleared when reached, accepting an occasional re-report.
  private const int DedupeLimit = 500;
  private const int MaxViewportSide = 10000;
  private static readonly ConditionalWeakTable<IPage, BrowserInteraction> interactions = new();

  private readonly IPage page;
  private readonly Lock gate = new();
  private readonly SemaphoreSlim viewportGate = new(1, 1);
  private readonly Queue<string> pageErrors = new();
  private readonly Queue<string> failedRequests = new();
  private readonly HashSet<string> seenErrors = new();
  private readonly HashSet<string> seenRequests = new();
  private PendingDialog? openDialog;
  private ScreenshotRecord? screenshot;
  private TaskCompletionSource dialogAppeared = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private int epoch;
  private int document;

  internal int Document => Volatile.Read(ref document);

  private BrowserInteraction(IPage page) {
    this.page = page;
    page.Dialog += OnDialog;
    page.Console += OnConsole;
    page.RequestFailed += OnRequestFailed;
    page.FrameNavigated += (_, frame) => {
      // Keep invalidating snapshot captures on URL changes within the document too.
      if (frame == page.MainFrame)
        Interlocked.Increment(ref document);
    };
  }

  // Playwright's FrameNavigated conflates document commits with history/fragment changes.
  // Chromium reports those separately. The page-target session ends when the page closes.
  internal async Task WatchDocumentCommitsAsync() {
    ICDPSession session = await page.Context.NewCDPSessionAsync(page).ConfigureAwait(false);
    session.Event("Page.frameNavigated").OnEvent += (sender, data) => {
      if (data is not JsonElement navigation || navigation.GetProperty("frame").TryGetProperty("parentId", out _))
        return;
      lock (gate) {
        seenErrors.Clear();
        seenRequests.Clear();
      }
    };
    await session.SendAsync("Page.enable").ConfigureAwait(false);
  }

  // Creates the state on first use and attaches its handlers; later calls return the same state.
  internal static BrowserInteraction For(IPage page) =>
      interactions.GetValue(page, static attached => new BrowserInteraction(attached));

  internal PendingDialog? PendingDialog {
    get {
      lock (gate)
        return openDialog;
    }
  }

  internal ScreenshotRecord? LatestScreenshot {
    get {
      lock (gate)
        return screenshot;
    }
  }

  internal int Epoch {
    get {
      lock (gate)
        return epoch;
    }
  }

  // A page shows one dialog at a time; a stray new one is dismissed so this record always names
  // the dialog blocking the page. Nothing is answered here - the dialog stays open by design.
  private void OnDialog(object? sender, IDialog dialog) {
    PendingDialog record = new(dialog, dialog.Type, dialog.Message, dialog.DefaultValue);
    PendingDialog? stuck;
    lock (gate) {
      stuck = openDialog;
      openDialog = record;
      dialogAppeared.TrySetResult();
      dialogAppeared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    if (stuck is not null)
      _ = DismissQuietlyAsync(stuck.Dialog);
  }

  private static async Task DismissQuietlyAsync(IDialog dialog) {
    try {
      await dialog.DismissAsync().ConfigureAwait(false);
    } catch (PlaywrightException) {
      // The page may already be gone; the newer dialog stays pending.
    }
  }

  // False when this page has no dialog pending; the dialog is removed from the record first, so
  // a failed answer (the page died with it open) still unblocks the page record.
  internal async Task<bool> RespondDialogAsync(bool accept, string? promptText) {
    PendingDialog? responding;
    lock (gate) {
      responding = openDialog;
      openDialog = null;
    }
    if (responding is null)
      return false;
    try {
      if (accept)
        await responding.Dialog.AcceptAsync(promptText).ConfigureAwait(false);
      else
        await responding.Dialog.DismissAsync().ConfigureAwait(false);
    } catch (PlaywrightException) {
      // The page may have closed while the dialog was open; it is not pending either way.
    }
    return true;
  }

  // Completes when the next dialog becomes pending on this page, or the token cancels. Callers
  // race in-flight page commands against this instead of hanging on the scripting a dialog
  // blocks. Renewed after every appearance, so a settled signal never fires twice.
  internal Task WaitDialogPendingAsync(CancellationToken cancellation) {
    lock (gate) {
      if (openDialog is not null)
        return Task.CompletedTask;
      if (dialogAppeared.Task.IsCompleted)
        dialogAppeared = new(TaskCreationOptions.RunContinuationsAsynchronously);
      return dialogAppeared.Task.WaitAsync(cancellation);
    }
  }

  internal (string[] Errors, string[] FailedRequests) DrainEvents() {
    lock (gate) {
      (string[] Errors, string[] FailedRequests) events = ([.. pageErrors], [.. failedRequests]);
      pageErrors.Clear();
      failedRequests.Clear();
      return events;
    }
  }

  // Puts drained events back when the result that drained them fails: the events were never
  // reported, and deduplication never records them again, so without this they would be lost.
  internal void RestoreEvents(string[] errors, string[] failed) {
    if (errors.Length is 0 && failed.Length is 0)
      return;
    lock (gate) {
      Restore(pageErrors, errors);
      Restore(failedRequests, failed);
    }
  }

  // Caller holds the gate. Restored events are older than anything recorded after the drain,
  // and the newest are kept when the queue overflows, as Record does.
  private static void Restore(Queue<string> queue, string[] events) {
    List<string> merged = [.. events, .. queue];
    queue.Clear();
    if (merged.Count > EventLimit)
      merged.RemoveRange(0, merged.Count - EventLimit);
    foreach (string line in merged)
      queue.Enqueue(line);
  }

  // Serialized against screenshots on the same page, so a viewer resize and an agent screenshot
  // never observe each other half-applied.
  internal async Task ResizeAsync(int width, int height) {
    if (width is < 1 or > MaxViewportSide || height is < 1 or > MaxViewportSide)
      throw new ArgumentException($"Viewport size must be between 1 and {MaxViewportSide}.");
    await viewportGate.WaitAsync().ConfigureAwait(false);
    try {
      await page.SetViewportSizeAsync(width, height).ConfigureAwait(false);
      lock (gate)
        epoch++;
    } finally {
      viewportGate.Release();
    }
  }

  internal async Task<ScreenshotRecord> CaptureAsync(CancellationToken cancellation) {
    await viewportGate.WaitAsync(cancellation).ConfigureAwait(false);
    try {
      byte[] image = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png })
          .WaitAsync(cancellation).ConfigureAwait(false);
      if (page.ViewportSize is not { } viewport)
        throw new InvalidOperationException("The page has no fixed viewport.");
      lock (gate) {
        epoch++;
        ScreenshotRecord record = new(epoch, viewport.Width, viewport.Height, image);
        screenshot = record;
        return record;
      }
    } finally {
      viewportGate.Release();
    }
  }

  // Clicks one screenshot's coordinates atomically against resizes and captures: the epoch check
  // and the click run inside the viewport gate, so a concurrent resize can never redirect the
  // click onto a resized viewport and no capture can invalidate the verified basis mid-flight.
  internal async Task ClickScreenshotAsync(ScreenshotRecord basis, int x, int y, CancellationToken cancellation) {
    await viewportGate.WaitAsync(cancellation).ConfigureAwait(false);
    try {
      lock (gate) {
        if (basis.Epoch != epoch)
          throw new InvalidOperationException(
              "The viewport changed since the screenshot; take a new screenshot and use its coordinates.");
      }
      await page.Mouse.ClickAsync(x, y).WaitAsync(cancellation).ConfigureAwait(false);
    } finally {
      viewportGate.Release();
    }
  }

  // Current only while no resize happened since the capture, so clicks land where the screenshot
  // showed them. Each capture advances the epoch, so an older screenshot of the same size is
  // stale too.
  internal bool IsCurrent(ScreenshotRecord record) => record.Epoch == Epoch;

  private void OnConsole(object? sender, IConsoleMessage message) {
    if (!string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
      return;
    string text = Trim(message.Text);
    lock (gate) {
      if (IsSeenOnce(seenErrors, text))
        Record(pageErrors, text, message.Location);
    }
  }

  private void OnRequestFailed(object? sender, IRequest request) {
    string detail = string.IsNullOrWhiteSpace(request.Failure) ? "" : $" - {request.Failure}";
    string text = Trim($"{request.Method} {request.Url}{detail}");
    lock (gate) {
      if (IsSeenOnce(seenRequests, text))
        Record(failedRequests, text, null);
    }
  }

  // Caller holds the gate. Dedupe keys on trimmed text and the sets are bounded, so a
  // long-lived document cannot grow them unbounded.
  private static bool IsSeenOnce(HashSet<string> seen, string text) {
    if (seen.Count >= DedupeLimit)
      seen.Clear();
    return seen.Add(text);
  }

  // Caller holds the gate. Keeps one burst from flooding the action result.
  private static void Record(Queue<string> lines, string text, string? location) {
    string line = string.IsNullOrEmpty(location) ? Trim(text) : $"{Trim(text)} ({location})";
    if (lines.Count >= EventLimit)
      lines.Dequeue();
    lines.Enqueue(line);
  }

  private static string Trim(string text) => text.Length <= MessageLimit ? text : text[..MessageLimit] + "...";

  public ValueTask DisposeAsync() {
    viewportGate.Dispose();
    return ValueTask.CompletedTask;
  }
}

// One dialog a page is waiting on; it stays open until the tool's dialog action answers it.
internal sealed record PendingDialog(IDialog Dialog, string Type, string Message, string DefaultValue);

// The most recent screenshot of one page; click_at coordinates are relative to it and valid only
// while no later capture or resize happened (a larger epoch never matches).
internal sealed record ScreenshotRecord(int Epoch, int Width, int Height, byte[] Image);
