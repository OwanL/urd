using System.Collections.Concurrent;
using System.Text;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Subscription and committed change publication run under the session gate; progress is transient.
// Subscriptions may be disposed anywhere. Observers enqueue into bounded server queues, not block delivery.
internal sealed class SessionNotifications(Guid sessionId) {
  private readonly ConcurrentDictionary<long, SessionObserver> observers = new();
  private readonly Dictionary<string, (ToolDraftUpdate Published, Func<ToolDraftUpdate>? ReadLatest)> drafts =
      new(StringComparer.Ordinal);
  private (Guid Execution, Guid Request) draftRequest;
  // The buffer and replay share this gate so replay can read text that has not been broadcast yet.
  internal Lock ToolDraftGate { get; } = new();
  private long version;
  private long nextObserverId;
  private string? currentStatus;
  private string? currentTitle;
  private bool hasPendingUserQuestion;

  internal IDisposable Subscribe(Action<SessionChange> observer, bool watched, string status, string? title,
      bool pendingUserQuestion) {
    currentStatus = status;
    currentTitle = title;
    hasPendingUserQuestion = pendingUserQuestion;
    long observerId = Interlocked.Increment(ref nextObserverId);
    var subscription = new SessionObserver(observer, watched, () => observers.TryRemove(observerId, out _));
    lock (ToolDraftGate) {
      observers.TryAdd(observerId, subscription);
      subscription.Notify(new SessionChange(sessionId, version, status, title, HasPendingUserQuestion: pendingUserQuestion));
      if (!watched)
        return subscription;

      subscription.Notify(new SessionChange(sessionId, version));
      foreach (var draft in drafts.Values.Select(value => value.ReadLatest?.Invoke() ?? value.Published)
          .OrderBy(draft => draft.Order))
        subscription.Notify(new SessionChange(sessionId, version, ToolDraft: draft));
    }
    return subscription;
  }

  internal IDisposable SubscribeClosed(Action<SessionChange> observer) {
    var subscription = new SessionObserver(observer, watched: false, () => { });
    subscription.Notify(new SessionChange(sessionId, version, "unavailable"));
    subscription.Dispose();
    return subscription;
  }

  internal void PublishToolProgress(ToolProgressUpdate progress) {
    SessionChange? change = null;
    foreach (SessionObserver observer in observers.Values) {
      if (!observer.Watched)
        continue;
      change ??= new SessionChange(sessionId, Volatile.Read(ref version), ToolProgress:
          progress with { Text = BoundedProgressText(progress.Text) });
      observer.Notify(change);
    }
  }

  internal void PublishToolDraft(ToolDraftUpdate draft, Func<ToolDraftUpdate>? readLatest) {
    lock (ToolDraftGate) {
      // A retry can share the request ID, but its first live draft replaces the old tombstones.
      if (draftRequest != (draft.ExecutionId, draft.RequestId)
          || (draft.State is "drafting" or "drafted" && drafts.Values.All(value => value.Published.State == "discarded"))) {
        drafts.Clear();
        draftRequest = (draft.ExecutionId, draft.RequestId);
      }
      drafts[draft.CallId] = (draft, readLatest);
      if (observers.IsEmpty)
        return;
      SessionChange? change = null;
      foreach (SessionObserver observer in observers.Values) {
        if (!observer.Watched)
          continue;
        change ??= new SessionChange(sessionId, Volatile.Read(ref version), ToolDraft: readLatest?.Invoke() ?? draft);
        observer.Notify(change);
      }
    }
  }

  internal void ClearToolDrafts() {
    lock (ToolDraftGate)
      drafts.Clear();
  }

  internal void Close() {
    ClearToolDrafts();
    version = checked(version + 1);
    var unavailable = new SessionChange(sessionId, version, "unavailable");
    foreach (SessionObserver observer in observers.Values) {
      observer.Notify(unavailable);
      observer.Dispose();
    }
  }

  private static string? BoundedProgressText(string? text) {
    if (text is null)
      return null;

    const string omitted = "[Earlier output omitted. Latest output follows.]\n";
    int remainingBytes = ToolOutputLimits.MaxBytes - Encoding.UTF8.GetByteCount(omitted);
    int start = text.Length;
    int suffixBytes = 0;
    while (start > 0) {
      int characterCount = char.IsLowSurrogate(text[start - 1]) && start > 1
          && char.IsHighSurrogate(text[start - 2]) ? 2 : 1;
      int characterBytes = Encoding.UTF8.GetByteCount(text.AsSpan(start - characterCount, characterCount));
      if (suffixBytes + characterBytes > remainingBytes)
        break;
      start -= characterCount;
      suffixBytes += characterBytes;
    }
    return start == 0 ? text : omitted + text[start..];
  }

  internal void Publish(string status, string? publishedTitle, bool pendingUserQuestion) {
    version = checked(version + 1);
    if (observers.IsEmpty)
      return;

    bool metadataChanged = currentStatus != status || publishedTitle is not null
        || hasPendingUserQuestion != pendingUserQuestion;
    currentStatus = status;
    currentTitle = publishedTitle ?? currentTitle;
    hasPendingUserQuestion = pendingUserQuestion;
    foreach (SessionObserver observer in observers.Values) {
      if (metadataChanged)
        observer.Notify(new SessionChange(sessionId, version, status, currentTitle, HasPendingUserQuestion: pendingUserQuestion));
      if (observer.Watched)
        observer.Notify(new SessionChange(sessionId, version));
    }
  }
}
