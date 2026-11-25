using System.Text.Json;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Track the main document's loader, not the page's URL or network activity. The old document
// can report lifecycle events while the new navigation is still waiting for its response.
internal sealed class BrowserNavigation {
  private static readonly TimeSpan StartGrace = TimeSpan.FromMilliseconds(40);
  private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);

  private readonly Lock gate = new();
  private string? mainFrame;
  private string? loader;
  private string? requestUrl;
  private bool committed;
  private bool contentLoaded;
  private bool finished = true;
  private int version;
  private TaskCompletionSource? nextStart;
  private TaskCompletionSource? completion;

  internal int Version => Volatile.Read(ref version);

  internal async Task WatchAsync(ICDPSession session) {
    session.Event("Page.frameRequestedNavigation").OnEvent += (_, data) => Requested(data!.Value);
    session.Event("Page.navigatedWithinDocument").OnEvent += (_, data) => SameDocument(data!.Value);
    session.Event("Page.frameStartedNavigating").OnEvent += (_, data) => Started(data!.Value);
    session.Event("Page.frameNavigated").OnEvent += (_, data) => Committed(data!.Value);
    session.Event("Page.lifecycleEvent").OnEvent += (_, data) => Lifecycle(data!.Value);
    session.Event("Page.downloadWillBegin").OnEvent += (_, data) => DownloadStarted(data!.Value);
    await session.SendAsync("Page.enable").ConfigureAwait(false);
    await session.SendAsync("Page.setLifecycleEventsEnabled", new Dictionary<string, object> { ["enabled"] = true })
        .ConfigureAwait(false);
    JsonElement? tree = await session.SendAsync("Page.getFrameTree").ConfigureAwait(false);
    lock (gate)
      mainFrame ??= tree!.Value.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
  }

  // Input can return just before Chromium emits its navigation event. Only give direct
  // click/key input a short grace, and stop it immediately when an event is received.
  internal async Task<bool> WaitAfterAsync(int before, bool allowGrace, CancellationToken cancellation) {
    Task? start;
    lock (gate)
      start = before == version && allowGrace
          ? (nextStart ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task
          : null;
    if (start is not null)
      await Task.WhenAny(start, Task.Delay(StartGrace, cancellation)).ConfigureAwait(false);
    cancellation.ThrowIfCancellationRequested();
    Task loading;
    lock (gate) {
      if (before == version || finished)
        return true;
      loading = (completion ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }
    try {
      await loading.WaitAsync(LoadTimeout, cancellation).ConfigureAwait(false);
      return true;
    } catch (TimeoutException) {
      return false;
    }
  }

  private void Requested(JsonElement data) {
    if (data.GetProperty("disposition").GetString() != "currentTab")
      return;
    lock (gate) {
      if (data.GetProperty("frameId").GetString() == mainFrame)
        Start(null, data.GetProperty("url").GetString());
    }
  }

  private void SameDocument(JsonElement data) {
    lock (gate) {
      if (loader is null && data.GetProperty("frameId").GetString() == mainFrame
          && data.GetProperty("url").GetString() == requestUrl)
        Finish();
    }
  }

  private void Started(JsonElement data) {
    if (data.GetProperty("navigationType").GetString() is "sameDocument" or "historySameDocument")
      return;
    lock (gate) {
      string? id = data.GetProperty("loaderId").GetString();
      if (data.GetProperty("frameId").GetString() == mainFrame && id != loader)
        Start(id, data.GetProperty("url").GetString());
    }
  }

  private void Committed(JsonElement data) {
    JsonElement frame = data.GetProperty("frame");
    if (frame.TryGetProperty("parentId", out _))
      return;
    lock (gate) {
      mainFrame = frame.GetProperty("id").GetString();
      string? id = frame.GetProperty("loaderId").GetString();
      if (id != loader)
        Start(id, null);
      committed = true;
      if (contentLoaded || data.GetProperty("type").GetString() == "BackForwardCacheRestore")
        Finish();
    }
  }

  private void Lifecycle(JsonElement data) {
    if (data.GetProperty("name").GetString() != "DOMContentLoaded")
      return;
    lock (gate) {
      if (data.GetProperty("frameId").GetString() != mainFrame || data.GetProperty("loaderId").GetString() != loader)
        return;
      contentLoaded = true;
      if (committed)
        Finish();
    }
  }

  internal void RequestFailed(IRequest request, IFrame main) {
    if (!request.IsNavigationRequest || request.Failure != "net::ERR_ABORTED")
      return;
    // Redirects keep the loader but Playwright reports the failure on the last request.
    while (request.RedirectedFrom is { } previous)
      request = previous;
    lock (gate) {
      if (!committed && !finished && requestUrl is not null && IsFrameRequest(request, main, requestUrl))
        Finish();
    }
  }

  private static bool IsFrameRequest(IRequest request, IFrame frame, string url) {
    if (request.Url.Split('#')[0] != url.Split('#')[0])
      return false;
    try {
      return request.Frame == frame;
    } catch (PlaywrightException) {
      // A request can fail before Playwright has attached its frame.
      return false;
    }
  }

  private void DownloadStarted(JsonElement data) {
    lock (gate) {
      if (!committed && data.GetProperty("frameId").GetString() == mainFrame)
        Finish();
    }
  }

  internal void Closed() {
    lock (gate)
      Finish();
  }

  // Caller holds the gate. If another navigation supersedes this one, keep the same wait budget.
  private void Start(string? id, string? url) {
    loader = id;
    requestUrl = url;
    committed = contentLoaded = finished = false;
    Interlocked.Increment(ref version);
    nextStart?.TrySetResult();
    nextStart = null;
  }

  private void Finish() {
    finished = true;
    completion?.TrySetResult();
    completion = null;
  }
}
