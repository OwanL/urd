using Microsoft.Playwright;

namespace Urd.Core.Tools;

// One shared persistent browser for every chat and helper. Owners (sessions, subagents) drive
// their own pages concurrently with no browser-wide lock: each owner keeps at most three pages
// including its popups, and opening past that closes its least recently used page. The browser
// launches on first use, exits five minutes after the last page closes, and an owner's pages
// close thirty minutes after its last agent or viewer activity - never while one of its
// questions to the user is pending. Every exit stages the profile's session cookies, so
// logins survive restarts.
internal sealed partial class BrowserHost : IAsyncDisposable {
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
      owned = RemoveAllPages(owner, entry);
    }
    await CloseRemovedPagesAsync(owner, owned);
    bool cleanup;
    lock (gate) {
      cleanup = entry.Downloads == 0;
      if (!cleanup)
        entry.Orphaned = true; // A running download removes the folder when it finishes.
    }
    if (cleanup)
      DeleteDownloadFolderQuietly(entry.DownloadFolder);
  }

  // Caller holds the gate. Unregisters every page the owner has and returns them for closing.
  private IPage[] RemoveAllPages(Guid owner, OwnerEntry entry) {
    IReadOnlyList<BrowserPage<IPage>> owned = pages.List(owner);
    foreach (BrowserPage<IPage> page in owned)
      pages.Remove(owner, page.Id);
    entry.Pages = 0;
    UpdateEmpty();
    return owned.Select(page => page.Page).ToArray();
  }

  private async Task CloseRemovedPagesAsync(Guid owner, IPage[] removed) {
    if (removed.Length > 0)
      NotifyChanged(owner);
    foreach (IPage page in removed)
      await CloseQuietlyAsync(page);
  }

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
