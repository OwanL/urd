using System.Text.Json;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Track the main document's loader, not the page's URL or network activity. The old document
// can report lifecycle events while the new navigation is still waiting for its response.
internal sealed class BrowserNavigation {
  private static readonly TimeSpan StartGrace = TimeSpan.FromMilliseconds(40);
  private static readonly TimeSpan AbortGrace = TimeSpan.FromMilliseconds(40);
  private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);

  private readonly Lock gate = new();
  private string? mainFrame;
  private string? loader;
  private string? requestedUrl;
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
    session.Event("Network.loadingFailed").OnEvent += (_, data) => RequestFailed(data!.Value);
    await session.SendAsync("Network.enable").ConfigureAwait(false);
    await session.SendAsync("Page.enable").ConfigureAwait(false);
    await session.SendAsync("Page.setLifecycleEventsEnabled", new Dictionary<string, object> { ["enabled"] = true })
        .ConfigureAwait(false);
    JsonElement? tree = await session.SendAsync("Page.getFrameTree").ConfigureAwait(false);
    lock (gate)
      mainFrame ??= tree!.Value.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
  }

  // Input can return just before Chromium emits its navigation event. Only give direct
  // click/key input a short grace, and stop it immediately when an event is received.
  internal async Task<NavigationWait> WaitAfterAsync(int before, bool allowGrace, CancellationToken cancellation) {
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
        return NavigationWait.Ready;
      loading = (completion ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }
    try {
      await loading.WaitAsync(LoadTimeout, cancellation).ConfigureAwait(false);
      return NavigationWait.Ready;
    } catch (TimeoutException) {
      lock (gate) {
        if (finished)
          return NavigationWait.Ready;
        return committed ? NavigationWait.TimedOutAfterCommit : NavigationWait.TimedOutBeforeCommit;
      }
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
          && data.GetProperty("url").GetString() == requestedUrl)
        Finish();
    }
  }

  private void Started(JsonElement data) {
    if (data.GetProperty("navigationType").GetString() is "sameDocument" or "historySameDocument")
      return;
    lock (gate) {
      string? id = data.GetProperty("loaderId").GetString();
      if (data.GetProperty("frameId").GetString() == mainFrame && id != loader)
        Start(id, null);
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

  private void RequestFailed(JsonElement data) {
    if (data.GetProperty("type").GetString() != "Document" || data.GetProperty("errorText").GetString() != "net::ERR_ABORTED")
      return;
    // Chromium uses the loader ID as the document request ID, including across redirects.
    // Downloads also abort their document request; an older abort cannot end a newer wait.
    lock (gate) {
      if (!committed && !finished && loader is not null && data.GetProperty("requestId").GetString() == loader)
        _ = FinishAbortedAsync(version);
    }
  }

  private async Task FinishAbortedAsync(int abortedVersion) {
    // History traversal can abort the old request before Chromium reports the replacement start.
    await Task.Delay(AbortGrace).ConfigureAwait(false);
    lock (gate) {
      if (version == abortedVersion && !committed && !finished)
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
    requestedUrl = url;
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

internal enum NavigationWait { Ready, TimedOutBeforeCommit, TimedOutAfterCommit }
