using Urd.Core.Sessions;

namespace Urd.Server;

// Owns the process-wide page limit, forwards session-creation hints and browser
// provisioning notices to live pages.
internal sealed class SessionEventHub : IDisposable {
  internal const int MaxPages = 128;
  private readonly object gate = new();
  private readonly SessionManager sessions;
  private SessionSseEvent? downloading;
  private readonly Dictionary<Guid, SessionEventPage> pages = new();
  private bool disposed;

  internal SessionEventHub(SessionManager sessions) {
    this.sessions = sessions;
    sessions.SessionCreated += OnSessionCreated;
    sessions.SessionSnoozeChanged += OnSessionSnoozeChanged;
    sessions.BrowserProvisioningChanged += OnBrowserProvisioningChanged;
  }

  internal bool TryCreate(out SessionEventPage page) {
    lock (gate) {
      if (disposed || pages.Count == MaxPages) {
        page = null!;
        return false;
      }

      page = new SessionEventPage(sessions.BootId, Remove);
      pages.Add(page.Id, page);
      // Pages opened mid-download learn the still-active provisioning notice.
      if (downloading is { } replay)
        page.EnqueueBroadcast(replay);
      return true;
    }
  }

  internal bool TryGet(Guid streamId, out SessionEventPage page) {
    lock (gate)
      return pages.TryGetValue(streamId, out page!);
  }

  private void OnSessionCreated(Guid sessionId) =>
      Broadcast(new SessionSseEvent("session-created", sessionId, new { session = sessionId }));

  // An invalidation, not a replayable state transition. One queued notice covers all chats.
  private void OnSessionSnoozeChanged(Guid _) => Broadcast(new SessionSseEvent("snooze-changed", null, new { }));

  private void Broadcast(SessionSseEvent message) {
    SessionEventPage[] current;
    lock (gate) {
      if (disposed)
        return;
      current = pages.Values.ToArray();
    }

    foreach (SessionEventPage page in current)
      page.EnqueueBroadcast(message);
  }

  // Only an in-flight download is replayed: terminal notices are never stale state to recreate.
  private void OnBrowserProvisioningChanged(BrowserProvisioningNotice notice) {
    SessionEventPage[] current;
    SessionSseEvent message = BrowserProvisioningMessage(notice);
    lock (gate) {
      if (disposed)
        return;
      downloading = notice.Stage == BrowserProvisioningStage.Downloading ? message : null;
      current = pages.Values.ToArray();
    }

    foreach (SessionEventPage page in current)
      page.EnqueueBroadcast(message);
  }

  private static SessionSseEvent BrowserProvisioningMessage(BrowserProvisioningNotice notice) =>
      new("browser-provisioning", null,
          new { state = notice.Stage.ToString().ToLowerInvariant(), detail = notice.Detail });

  private void Remove(SessionEventPage page) {
    lock (gate) {
      if (pages.TryGetValue(page.Id, out SessionEventPage? current) && ReferenceEquals(current, page))
        pages.Remove(page.Id);
    }
    page.Close();
  }

  public void Dispose() {
    SessionEventPage[] current;
    lock (gate) {
      if (disposed)
        return;
      disposed = true;
      current = pages.Values.ToArray();
      pages.Clear();
    }

    sessions.SessionCreated -= OnSessionCreated;
    sessions.SessionSnoozeChanged -= OnSessionSnoozeChanged;
    sessions.BrowserProvisioningChanged -= OnBrowserProvisioningChanged;
    foreach (SessionEventPage page in current)
      page.Close();
  }
}


