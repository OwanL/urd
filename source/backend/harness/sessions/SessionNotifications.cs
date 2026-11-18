using System.Collections.Concurrent;

namespace Urd.Core.Sessions;

// Subscribe and Publish run under the session gate; subscriptions may be disposed anywhere.
// Observers are expected to enqueue into bounded server queues, not block delivery.
internal sealed class SessionNotifications(Guid sessionId) {
  private readonly ConcurrentDictionary<long, SessionObserver> observers = new();
  private long version;
  private long nextObserverId;
  private string? currentStatus;
  private string? currentTitle;

  internal IDisposable Subscribe(Action<SessionChange> observer, bool watched, string status, string? title) {
    currentStatus = status;
    currentTitle = title;
    long observerId = Interlocked.Increment(ref nextObserverId);
    var subscription = new SessionObserver(observer, watched, () => observers.TryRemove(observerId, out _));
    observers.TryAdd(observerId, subscription);
    subscription.Notify(new SessionChange(sessionId, version, status, currentTitle));
    if (watched)
      subscription.Notify(new SessionChange(sessionId, version));
    return subscription;
  }

  internal void Publish(string status, string? publishedTitle) {
    version = checked(version + 1);
    if (observers.IsEmpty)
      return;

    bool metadataChanged = currentStatus != status || publishedTitle is not null;
    currentStatus = status;
    currentTitle = publishedTitle ?? currentTitle;
    foreach (SessionObserver observer in observers.Values) {
      if (metadataChanged)
        observer.Notify(new SessionChange(sessionId, version, status, currentTitle));
      if (observer.Watched)
        observer.Notify(new SessionChange(sessionId, version));
    }
  }
}
