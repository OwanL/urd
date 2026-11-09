using Urd.Core.Sessions;

namespace Urd.Server;

// Owns the process-wide page limit and forwards session-creation hints to live pages.
internal sealed class SessionEventHub : IDisposable {
  internal const int MaxPages = 128;
  private readonly object gate = new();
  private readonly SessionManager sessions;
  private readonly Dictionary<Guid, SessionEventPage> pages = new();
  private bool disposed;

  internal SessionEventHub(SessionManager sessions) {
    this.sessions = sessions;
    sessions.SessionCreated += OnSessionCreated;
  }

  internal bool TryCreate(out SessionEventPage page) {
    lock (gate) {
      if (disposed || pages.Count == MaxPages) {
        page = null!;
        return false;
      }

      page = new SessionEventPage(sessions.Boot, Remove);
      pages.Add(page.Id, page);
      return true;
    }
  }

  internal bool TryGet(Guid streamId, out SessionEventPage page) {
    lock (gate)
      return pages.TryGetValue(streamId, out page!);
  }

  private void OnSessionCreated(Guid sessionId) {
    SessionEventPage[] current;
    lock (gate) {
      if (disposed)
        return;
      current = pages.Values.ToArray();
    }

    var message = new SessionSseEvent("session-created", sessionId, new { session = sessionId });
    foreach (SessionEventPage page in current)
      page.EnqueueCreation(message);
  }

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
    foreach (SessionEventPage page in current)
      page.Close();
  }
}


