namespace Urd.Core.Sessions;

internal sealed class SessionObserver(Action<SessionChange> callback, bool watched, Action unsubscribe) : IDisposable {
  private int disposed;

  internal bool Watched { get; } = watched;

  internal void Notify(SessionChange change) {
    if (Volatile.Read(ref disposed) != 0)
      return;
    try {
      callback(change);
    } catch (Exception) {
      // Subscriber failure must not turn a saved session change into an execution failure.
    }
  }

  public void Dispose() {
    if (Interlocked.Exchange(ref disposed, 1) == 0)
      unsubscribe();
  }
}
