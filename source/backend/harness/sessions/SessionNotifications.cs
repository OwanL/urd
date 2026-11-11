using System.Collections.Concurrent;

namespace Urd.Core.Sessions;

// Subscribe and Publish run under the session gate; subscriptions may be disposed anywhere.
// Observers are expected to enqueue into bounded server queues, not block delivery.
internal sealed class SessionNotifications(Guid sessionId) {
  private readonly ConcurrentDictionary<long, SessionObserver> observers = new();
  private long version;
  private long nextObserverId;
  private string? currentStatus;

  internal IDisposable Subscribe(Action<SessionChange> observer, bool watched, string status) {
    currentStatus = status;
    long observerId = Interlocked.Increment(ref nextObserverId);
    var subscription = new SessionObserver(observer, watched, () => observers.TryRemove(observerId, out _));
    observers.TryAdd(observerId, subscription);
    subscription.Notify(new SessionChange(sessionId, version, status));
    if (watched)
      subscription.Notify(new SessionChange(sessionId, version));
    return subscription;
  }

  internal void Publish(string status) {
    version = checked(version + 1);
    if (observers.IsEmpty)
      return;

    bool statusChanged = currentStatus is not null && currentStatus != status;
    currentStatus = status;
    foreach (SessionObserver observer in observers.Values) {
      if (statusChanged)
        observer.Notify(new SessionChange(sessionId, version, status));
      if (observer.Watched)
        observer.Notify(new SessionChange(sessionId, version));
    }
  }
}
