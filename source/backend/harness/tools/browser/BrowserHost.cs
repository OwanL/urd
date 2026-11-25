using Microsoft.Playwright;

namespace Urd.Core.Tools;

// One shared persistent browser for every chat and helper. Owners (sessions, subagents) drive
// their own pages concurrently with no browser-wide lock: each owner keeps at most three pages
// including its popups, and opening past that closes its least recently used page. The browser
// launches on first use, exits five minutes after the last page closes, and an owner's pages
// close thirty minutes after its last agent or viewer activity - never while one of its
// questions to the user is pending. Every exit stages the profile's session cookies, so
// logins survive restarts.
internal sealed class BrowserHost : IAsyncDisposable {
  private static readonly TimeSpan OwnerIdleAfter = TimeSpan.FromMinutes(30);
  private static readonly TimeSpan BrowserIdleAfter = TimeSpan.FromMinutes(5);
  private static readonly TimeSpan PendingRecheck = TimeSpan.FromMinutes(1);
  private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
  private const int NoticeDetailLimit = 120;

  private readonly Lock gate = new();
  private readonly Func<Guid, Task<bool>> hasPendingQuestion;
  private readonly BrowserPages<IPage> pages = new();
  private readonly Dictionary<Guid, OwnerEntry> owners = [];
  private readonly string profilePath;
  private readonly string cookiePath;
  private readonly string downloadRoot;
  private readonly Task sweeper;
  private readonly CancellationTokenSource shutdown = new();
  private TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private Task<PersistentContext>? launch;
  private Task? exiting;
  private Task? disposal;
  private DateTimeOffset? emptySince;
  private bool disposed;

  internal BrowserHost(string dataRoot, Func<Guid, Task<bool>> hasPendingQuestion) {
    ArgumentNullException.ThrowIfNull(hasPendingQuestion);
    string root = Path.GetFullPath(dataRoot);
    profilePath = Path.Combine(root, "playwright-profile");
    cookiePath = Path.Combine(root, "playwright-profile.cookies.json");
    downloadRoot = Path.Combine(Path.GetTempPath(), "urd-browser-downloads");
    this.hasPendingQuestion = hasPendingQuestion;
    sweeper = SweepAsync();
  }

  // The registry behind OpenAsync; selection and listing belong to the tool and viewer.
  internal BrowserPages<IPage> Pages => pages;
  internal event Action<Guid>? Changed;

  private void NotifyChanged(Guid owner) {
    try {
      Changed?.Invoke(owner);
    } catch (Exception) {
      // A viewer notification cannot disrupt browser operations.
    }
  }

  // Opens the owner's next page on the shared context, evicting the owner's least recently
  // used page past the limit. A shared launch is not canceled per caller, because it outlives
  // any single owner; page creation itself is short and stays shared too.
  internal async Task<BrowserPage<IPage>> OpenAsync(Guid owner, CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    BeginOwnerUse(owner);
    IPage? opening = null;
    try {
      PersistentContext browser = await BeginLaunchAsync(cancellation);
      opening = await browser.Context.NewPageAsync();
      await BrowserInteraction.For(opening).WatchNavigationAsync();
      BrowserPage<IPage> registered;
      BrowserPage<IPage>? evicted;
      lock (gate) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!owners.ContainsKey(owner))
          throw new InvalidOperationException("The chat closed while its page was opening.");
        registered = RegisterCore(owner, opening, out evicted);
      }
      Attach(owner, registered.Id, opening);
      NotifyChanged(owner);
      opening = null;
      if (evicted is not null)
        await CloseEvictedAsync(owner, evicted);
      return registered;
    } catch {
      if (opening is not null)
        await CloseQuietlyAsync(opening);
      throw;
    } finally {
      EndOwnerUse(owner);
      lock (gate)
        UpdateEmpty();
    }
  }

  // Agent or viewer interaction: moves the page to most recent and renews the owner's idle timer.
  internal void Touch(Guid owner, Guid page) {
    lock (gate) {
      ObjectDisposedException.ThrowIf(disposed, this);
      if (!pages.Touch(owner, page))
        return;
      if (owners.TryGetValue(owner, out OwnerEntry? entry)) {
        entry.LastActivity = DateTimeOffset.UtcNow;
        entry.DeferredUntil = null;
      }
    }
  }

  internal async Task ClosePageAsync(Guid owner, Guid page) {
    ObjectDisposedException.ThrowIf(disposed, this);
    IPage? closing;
    lock (gate) {
      closing = pages.Remove(owner, page)?.Page;
      if (closing is null)
        return;
      if (owners.TryGetValue(owner, out OwnerEntry? entry)) {
        entry.Pages--;
        entry.LastActivity = DateTimeOffset.UtcNow;
        entry.DeferredUntil = null;
      }
      UpdateEmpty();
    }
    NotifyChanged(owner);
    await CloseQuietlyAsync(closing);
  }

  // Closes the owner's pages and removes its downloads when the chat closes.
  internal Task CloseOwnerAsync(Guid owner) {
    ObjectDisposedException.ThrowIf(disposed, this);
    return CloseOwnerCoreAsync(owner);
  }

  // Returns and clears the owner's eviction and download notices; "" when there are none.
  internal string DrainNotices(Guid owner) {
    lock (gate) {
      ObjectDisposedException.ThrowIf(disposed, this);
      if (!owners.TryGetValue(owner, out OwnerEntry? entry) || entry.Notices.Count == 0)
        return "";
      string text = string.Join(Environment.NewLine, entry.Notices);
      entry.Notices.Clear();
      return text;
    }
  }

  // Public: closes every owner's pages and download folders, then exits the browser with its
  // cookie save. Concurrent callers share one cleanup.
  public async ValueTask DisposeAsync() {
    Task cleanup;
    lock (gate) {
      if (disposal is null) {
        disposed = true;
        Signal();
        disposal = DisposeCoreAsync();
      }
      cleanup = disposal;
    }
    await cleanup;
  }

  private async Task DisposeCoreAsync() {
    await Task.Yield();
    await shutdown.CancelAsync();
    await sweeper;
    Guid[] remaining;
    lock (gate)
      remaining = [.. owners.Keys];
    foreach (Guid owner in remaining)
      await CloseOwnerCoreAsync(owner);
    Task? exit;
    lock (gate) {
      Retire();
      exit = exiting;
    }
    if (exit is not null)
      await exit;
    shutdown.Dispose();
  }

  private void BeginOwnerUse(Guid owner) {
    lock (gate) {
      ObjectDisposedException.ThrowIf(disposed, this);
      if (!owners.TryGetValue(owner, out OwnerEntry? entry)) {
        entry = new OwnerEntry(Path.Combine(downloadRoot, $"{owner:N}-{Guid.NewGuid():N}"));
        owners.Add(owner, entry);
      }
      entry.LastActivity = DateTimeOffset.UtcNow;
      entry.DeferredUntil = null;
      entry.Active++;
    }
  }

  private void EndOwnerUse(Guid owner) {
    lock (gate) {
      if (owners.TryGetValue(owner, out OwnerEntry? entry))
        entry.Active--;
    }
  }

  // Launches lazily, once; callers waiting on a shared launch each cancel only their own wait.
  private Task<PersistentContext> BeginLaunchAsync(CancellationToken cancellation) {
    Task<PersistentContext> shared;
    lock (gate) {
      ObjectDisposedException.ThrowIf(disposed, this);
      // A failed or closed launch is handed off to an exit so the next use relaunches cleanly.
      if (launch is { IsCompleted: true } completed && (!completed.IsCompletedSuccessfully || completed.Result.IsClosed)) {
        launch = null;
        exiting = ExitBrowserAsync(completed);
      }
      emptySince = null;
      if (launch is null) {
        launch = RunLaunchAsync(exiting ?? Task.CompletedTask);
        ObserveFailure(launch);
      }
      shared = launch;
    }
    return shared.WaitAsync(cancellation);
  }

  private async Task<PersistentContext> RunLaunchAsync(Task predecessor) {
    await Task.Yield();
    await predecessor;
    return await PersistentContext.StartAsync(profilePath, cookiePath, shutdown.Token);
  }

  private static async Task ExitBrowserAsync(Task<PersistentContext> pending) {
    await Task.Yield();
    PersistentContext? browser;
    try {
      browser = await pending;
    } catch (Exception) {
      return; // Nothing launched; nothing to save or close.
    }
    try {
      await browser.ExitAsync();
    } catch (Exception) {
      // Exiting is best effort; the next launch restores the last staged cookies.
    }
  }

  // Caller holds the gate. Stops the browser; the next open relaunches with restored cookies.
  private void Retire() {
    if (launch is null)
      return;
    Task<PersistentContext> pending = launch;
    launch = null;
    emptySince = null;
    exiting = ExitBrowserAsync(pending);
  }

  // Caller holds the gate. Registers the page as its owner's most recent one, evicting the
  // owner's least recently used page past the limit. Throws when the owner was already closed
  // by a concurrent CloseOwnerAsync or disposal.
  private BrowserPage<IPage> RegisterCore(Guid owner, IPage page, out BrowserPage<IPage>? evicted, Guid? opener = null) {
    OwnerEntry entry = owners[owner];
    entry.LastActivity = DateTimeOffset.UtcNow;
    entry.DeferredUntil = null;
    BrowserPageRegistration<IPage> registration = pages.Register(owner, page, opener);
    entry.Pages++;
    evicted = registration.Evicted;
    if (evicted is not null)
      entry.Pages--;
    UpdateEmpty();
    Signal(); // A page may have appeared for an idle owner; the sweep recomputes.
    return registration.Page;
  }

  private void Attach(Guid owner, Guid page, IPage live) {
    // Popups and downloads belong to the opener's owner. A page closed outside the host, by a
    // site or by the browser, removes itself so listings stay truthful.
    live.FrameNavigated += (_, frame) => {
      if (frame == live.MainFrame)
        NotifyChanged(owner);
    };
    live.Load += (_, _) => NotifyChanged(owner);
    live.Popup += (_, popup) => OnPopup(owner, page, popup);
    live.Download += (_, download) => OnDownload(owner, download);
    live.Close += (_, _) => OnPageSelfClosed(owner, page, live);
  }

  private void OnPopup(Guid owner, Guid opener, IPage popup) {
    _ = OnPopupCoreAsync(owner, opener, popup);
  }

  // A popup belongs to the opener's owner and becomes its most recent page. If the opener
  // was already removed when the popup arrives, close it; registered popups remain independent.
  private async Task OnPopupCoreAsync(Guid owner, Guid opener, IPage popup) {
    BrowserPage<IPage>? registered = null;
    BrowserPage<IPage>? evicted = null;
    lock (gate) {
      if (!disposed && owners.ContainsKey(owner) && pages.Get(owner, opener) is not null)
        registered = RegisterCore(owner, popup, out evicted, opener);
    }
    if (registered is null) {
      await CloseQuietlyAsync(popup);
      return;
    }
    BrowserInteraction interaction = BrowserInteraction.For(popup);
    Attach(owner, registered.Id, popup);
    try {
      await interaction.WatchNavigationAsync();
    } catch (PlaywrightException) {
      // A popup may close while the browser is attaching its DevTools session.
    }
    NotifyChanged(owner);
    if (evicted is not null)
      await CloseEvictedAsync(owner, evicted);
  }

  private void OnPageSelfClosed(Guid owner, Guid page, IPage live) {
    bool removed = false;
    lock (gate) {
      if (pages.Get(owner, page)?.Page == live) {
        pages.Remove(owner, page);
        removed = true;
        if (owners.TryGetValue(owner, out OwnerEntry? entry))
          entry.Pages--;
        UpdateEmpty();
      }
    }
    if (removed)
      NotifyChanged(owner);
  }

  private void OnDownload(Guid owner, IDownload download) {
    OwnerEntry? entry;
    lock (gate) {
      if (!owners.TryGetValue(owner, out entry))
        entry = null; // The owner is closed; cancel the download instead of saving it.
      else
        entry.Downloads++;
    }
    if (entry is null)
      _ = CancelQuietlyAsync(download);
    else
      _ = SaveDownloadAsync(owner, entry, download);
  }

  private async Task SaveDownloadAsync(Guid owner, OwnerEntry entry, IDownload download) {
    string name = download.SuggestedFilename;
    try {
      string target = UniqueTarget(entry.DownloadFolder, name);
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      await download.SaveAsAsync(target);
      DownloadNotice(owner, entry, $"Saved download \"{name}\" to \"{target}\".");
    } catch (Exception error) {
      DownloadNotice(owner, entry, $"Download failed: {name} ({error.Message}).");
    } finally {
      bool cleanup;
      lock (gate)
        cleanup = --entry.Downloads == 0 && entry.Orphaned;
      if (cleanup)
        DeleteDownloadFolderQuietly(entry.DownloadFolder);
    }
  }

  // Each download gets its own directory, so concurrent saves cannot claim the same path.
  private static string UniqueTarget(string folder, string suggested) {
    string name = Path.GetFileName(suggested.Replace('\\', '/'));
    name = new string(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
    if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
      name = "download";
    return Path.Combine(folder, Guid.NewGuid().ToString("N"), name);
  }

  private async Task CloseEvictedAsync(Guid owner, BrowserPage<IPage> evicted) {
    Notice(owner, $"Closed the least recently used page \"{await DescribeAsync(evicted.Page)}\" ({evicted.Id}).");
    await CloseQuietlyAsync(evicted.Page);
  }

  private static async Task<string> DescribeAsync(IPage page) {
    try {
      string title = await page.TitleAsync().WaitAsync(CloseTimeout);
      if (!string.IsNullOrWhiteSpace(title))
        return TrimDetail(title);
    } catch (Exception error) when (error is PlaywrightException or TimeoutException) {
      // The page may be gone; fall back to its URL.
    }
    return TrimDetail(string.IsNullOrWhiteSpace(page.Url) ? "(untitled)" : page.Url);
  }

  private static string TrimDetail(string text) => text.Length <= NoticeDetailLimit ? text : text[..NoticeDetailLimit] + "...";

  private static async Task CloseQuietlyAsync(IPage page) {
    try {
      await page.CloseAsync().WaitAsync(CloseTimeout);
    } catch (Exception error) when (error is PlaywrightException or TimeoutException) {
      // The page or its browser may already be gone.
    }
  }

  private static async Task CancelQuietlyAsync(IDownload download) {
    try {
      await download.CancelAsync().WaitAsync(CloseTimeout);
    } catch (Exception error) when (error is PlaywrightException or TimeoutException) {
      // The download may already be gone.
    }
  }

  private static void DeleteDownloadFolderQuietly(string folder) {
    try {
      Directory.Delete(folder, recursive: true);
    } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
      // A just-finished download may still hold the folder briefly.
    }
  }

  // Caller holds the gate. Starts the browser exit countdown once the last page is closed.
  private void UpdateEmpty() {
    if (owners.Values.Any(entry => entry.Pages > 0) || launch is null) {
      emptySince = null;
      return;
    }
    if (emptySince is null) {
      emptySince = DateTimeOffset.UtcNow;
      Signal();
    }
  }

  private void DownloadNotice(Guid owner, OwnerEntry expected, string text) {
    lock (gate) {
      if (owners.TryGetValue(owner, out OwnerEntry? entry) && entry == expected)
        entry.Notices.Add(text);
    }
  }

  // Records an eviction notice for the owner to drain later. Missing owners are
  // gone; their notices are silently dropped.
  private void Notice(Guid owner, string text) {
    lock (gate) {
      if (owners.TryGetValue(owner, out OwnerEntry? entry))
        entry.Notices.Add(text);
    }
  }

  // Caller holds the gate. Wakes the sweep when a nearer deadline appeared.
  private void Signal() => wake.TrySetResult();

  private async Task CloseOwnerCoreAsync(Guid owner) {
    IPage[] owned;
    OwnerEntry entry;
    lock (gate) {
      if (!owners.Remove(owner, out OwnerEntry? removed))
        return;
      entry = removed;
      IReadOnlyList<BrowserPage<IPage>> snapshot = pages.List(owner);
      foreach (BrowserPage<IPage> page in snapshot)
        pages.Remove(owner, page.Id);
      entry.Pages = 0;
      owned = snapshot.Select(page => page.Page).ToArray();
      UpdateEmpty();
    }
    if (owned.Length > 0)
      NotifyChanged(owner);
    foreach (IPage page in owned)
      await CloseQuietlyAsync(page);
    bool cleanup;
    lock (gate) {
      cleanup = entry.Downloads == 0;
      if (!cleanup)
        entry.Orphaned = true; // A running download removes the folder when it finishes.
    }
    if (cleanup)
      DeleteDownloadFolderQuietly(entry.DownloadFolder);
  }

  // One background sweep decides idle evictions and the browser exit. It never holds the gate
  // across an await, so agent interactions and page operations are never blocked by it.
  private async Task SweepAsync() {
    while (true) {
      Task? countdown;
      Task wakeTask;
      lock (gate) {
        if (disposed)
          return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset? deadline = NextDeadline();
        wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wakeTask = wake.Task;
        if (deadline is { } due) {
          TimeSpan wait = due - now;
          countdown = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, CancellationToken.None);
        } else {
          countdown = null;
        }
      }
      if (countdown is null) {
        await wakeTask;
        continue;
      }
      if (await Task.WhenAny(countdown, wakeTask) != wakeTask)
        await SweepOnce();
    }
  }

  // Caller holds the gate. The nearest owner idle eviction or the browser exit countdown.
  private DateTimeOffset? NextDeadline() {
    DateTimeOffset? deadline = null;
    foreach (OwnerEntry entry in owners.Values) {
      if (entry.Pages == 0)
        continue;
      DateTimeOffset due = entry.DeferredUntil ?? entry.LastActivity + OwnerIdleAfter;
      if (deadline is not { } best || due < best)
        deadline = due;
    }
    if (launch is not null && emptySince is { } empty) {
      DateTimeOffset due = empty + BrowserIdleAfter;
      if (deadline is not { } best || due < best)
        deadline = due;
    }
    return deadline;
  }

  private static bool IsIdleDue(OwnerEntry entry, DateTimeOffset now) =>
      (entry.DeferredUntil ?? entry.LastActivity + OwnerIdleAfter) <= now;

  private async Task SweepOnce() {
    Dictionary<Guid, OwnerEntry> expired = [];
    lock (gate) {
      if (disposed)
        return;
      DateTimeOffset now = DateTimeOffset.UtcNow;
      foreach ((Guid owner, OwnerEntry entry) in owners) {
        if (entry.Pages == 0 || !IsIdleDue(entry, now))
          continue;
        if (entry.Active > 0) {
          // Never close under an in-flight agent interaction; recheck soon.
          entry.DeferredUntil = now + PendingRecheck;
          Signal();
          continue;
        }
        expired.Add(owner, entry);
      }
    }
    foreach ((Guid owner, OwnerEntry entry) in expired) {
      bool pending;
      try {
        pending = await hasPendingQuestion(owner);
      } catch (Exception) {
        // Fail safe: treat an unknown answer as a pending question.
        pending = true;
      }
      IPage[] idlePages;
      lock (gate) {
        if (disposed)
          return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!owners.TryGetValue(owner, out OwnerEntry? current) || current != entry || !IsIdleDue(current, now))
          continue;
        if (pending || current.Active > 0) {
          // A pending question or active operation keeps the owner's pages; recheck soon.
          current.DeferredUntil = now + PendingRecheck;
          Signal();
          continue;
        }
        IReadOnlyList<BrowserPage<IPage>> snapshot = pages.List(owner);
        foreach (BrowserPage<IPage> page in snapshot)
          pages.Remove(owner, page.Id);
        current.Pages = 0;
        idlePages = snapshot.Select(page => page.Page).ToArray();
        UpdateEmpty();
      }
      if (idlePages.Length > 0)
        NotifyChanged(owner);
      foreach (IPage page in idlePages)
        await CloseQuietlyAsync(page);
    }
    lock (gate) {
      if (disposed)
        return;
      DateTimeOffset now = DateTimeOffset.UtcNow;
      if (launch is not null && emptySince is { } empty && now - empty >= BrowserIdleAfter)
        Retire();
    }
  }

  private static void ObserveFailure(Task pending) => _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

  private sealed class OwnerEntry(string downloadFolder) {
    public string DownloadFolder { get; } = downloadFolder;
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeferredUntil { get; set; }
    public int Active { get; set; }
    public int Pages { get; set; }
    public int Downloads { get; set; }
    public bool Orphaned { get; set; }
    public List<string> Notices { get; } = [];
  }
}
