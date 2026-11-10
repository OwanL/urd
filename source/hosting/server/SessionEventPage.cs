using Urd.Core.Sessions;

namespace Urd.Server;

// One browser page's multiplexed stream and mutable open/watch subscriptions.
internal sealed class SessionEventPage : IDisposable {
  internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
  private readonly object gate = new();
  private readonly SemaphoreSlim updateGate = new(1, 1);
  private readonly Dictionary<Guid, Registration> registrations = new();
  private readonly SessionEventQueue queue = new();
  private readonly Guid boot;
  private readonly Action<SessionEventPage> remove;
  private bool disposed;

  internal SessionEventPage(Guid boot, Action<SessionEventPage> remove) {
    this.boot = boot;
    this.remove = remove;
  }

  internal Guid Id { get; } = Guid.NewGuid();
  internal Guid Boot => boot;
  internal MarkdownPageLeases MarkdownLeases { get; } = new();
  internal bool IsClosed {
    get {
      lock (gate)
        return disposed || queue.IsClosed;
    }
  }

  internal void EnqueueCreation(SessionSseEvent message) => queue.Enqueue(message);

  internal async Task UpdateAsync(SessionManager sessions, Guid[] open, Guid[] watched, CancellationToken cancellation) {
    await updateGate.WaitAsync(cancellation);
    try {
      if (IsClosed)
        throw new KeyNotFoundException("The event stream is closed.");

      var opened = new Dictionary<Guid, PrimarySession>(open.Length);
      foreach (Guid sessionId in open)
        opened.Add(sessionId, await sessions.GetPrimaryAsync(sessionId));

      var replaced = new List<Registration>();
      var added = new List<(Guid Id, PrimarySession Session, Registration Registration)>();
      var retained = new List<(PrimarySession Session, Registration Registration)>();
      var watchedIds = watched.ToHashSet();
      lock (gate) {
        if (disposed || queue.IsClosed)
          throw new KeyNotFoundException("The event stream is closed.");

        foreach ((Guid sessionId, Registration registration) in registrations.ToArray()) {
          if (!opened.ContainsKey(sessionId) || registration.Watched != watchedIds.Contains(sessionId)) {
            registrations.Remove(sessionId);
            registration.Deactivate();
            queue.RemoveSessionUpdates(sessionId);
            replaced.Add(registration);
          }
        }

        foreach ((Guid sessionId, PrimarySession session) in opened) {
          if (registrations.TryGetValue(sessionId, out Registration? existing)) {
            retained.Add((session, existing));
            continue;
          }
          var registration = new Registration(this, sessionId, watchedIds.Contains(sessionId));
          registrations.Add(sessionId, registration);
          added.Add((sessionId, session, registration));
        }
      }

      foreach (Registration registration in replaced)
        registration.Deactivate();

      try {
        foreach (var (_, session, registration) in added) {
          IDisposable subscription = await session.SubscribeAsync(registration.Notify, registration.Watched, cancellation);
          registration.Attach(subscription);
        }

        foreach (var (session, registration) in retained) {
          using IDisposable handshake = await session.SubscribeAsync(
              registration.Notify, registration.Watched, cancellation);
        }
      } catch {
        Dispose();
        throw;
      }
    } finally {
      updateGate.Release();
    }
  }

  internal Task<SessionSseRead> ReadNextAsync(CancellationToken cancellation) =>
      queue.ReadNextAsync(HeartbeatInterval, cancellation);

  public void Dispose() {
    Close();
    remove(this);
  }

  internal void Close() {
    Registration[] current;
    lock (gate) {
      if (disposed)
        return;
      disposed = true;
      current = registrations.Values.ToArray();
      registrations.Clear();
      queue.Close();
    }

    MarkdownLeases.Close();
    foreach (Registration registration in current)
      registration.Deactivate();
  }

  private void OnSessionChange(Registration registration, SessionChange change) {
    lock (gate) {
      if (disposed || queue.IsClosed
          || !registrations.TryGetValue(registration.Session, out Registration? current)
          || !ReferenceEquals(current, registration))
        return;

      if (change.Status is { } status) {
        queue.Enqueue(new SessionSseEvent("status", change.Session,
            new { session = change.Session, boot, version = change.Version, status }));
      } else if (registration.Watched) {
        queue.Enqueue(new SessionSseEvent("change", change.Session,
            new { session = change.Session, boot, version = change.Version }));
      }
    }
  }

  private sealed class Registration(SessionEventPage page, Guid session, bool watched) {
    private IDisposable? subscription;
    private int active = 1;

    internal Guid Session { get; } = session;
    internal bool Watched { get; } = watched;
    internal void Notify(SessionChange change) {
      if (Volatile.Read(ref active) != 0)
        page.OnSessionChange(this, change);
    }

    internal void Attach(IDisposable value) {
      IDisposable? prior = Interlocked.Exchange(ref subscription, value);
      prior?.Dispose();
      if (Volatile.Read(ref active) == 0)
        Interlocked.Exchange(ref subscription, null)?.Dispose();
    }

    internal void Deactivate() {
      if (Interlocked.Exchange(ref active, 0) == 0)
        return;
      Interlocked.Exchange(ref subscription, null)?.Dispose();
    }
  }
}
