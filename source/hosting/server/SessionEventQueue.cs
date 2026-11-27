namespace Urd.Server;

internal sealed record SessionSseEvent(string Name, Guid? Session, object Data, string? CoalesceKey = null);

internal readonly record struct SessionSseRead(SessionSseEvent? Event, bool Closed, bool Heartbeat);

// Coalesces live session updates, bounds transient progress, and caps session-creation hints per page.
internal sealed class SessionEventQueue {
  internal const int MaxOpenSessions = 128;
  internal const int MaxCreations = 128;
  internal const int MaxProgressEvents = MaxOpenSessions;
  private const int MaxPendingEvents = MaxOpenSessions * 3 + MaxCreations + MaxProgressEvents;
  private readonly object gate = new();
  private readonly LinkedList<SessionSseEvent> pending = new();
  private readonly Dictionary<EventKey, LinkedListNode<SessionSseEvent>> byKey = new();
  private TaskCompletionSource changed = NewSignal();
  private int pendingCreations;
  private int pendingProgressEvents;
  private bool closed;

  internal bool IsClosed {
    get {
      lock (gate)
        return closed;
    }
  }

  internal bool Enqueue(SessionSseEvent message) {
    lock (gate) {
      if (closed)
        return false;

      var key = new EventKey(message.Name, message.Session, message.CoalesceKey);
      if (byKey.TryGetValue(key, out LinkedListNode<SessionSseEvent>? existing)) {
        existing.Value = message;
        return true;
      }

      bool creation = message.Name == "session-created";
      bool progress = message.Name == "tool-progress";
      if (progress && (pendingProgressEvents == MaxProgressEvents || pending.Count == MaxPendingEvents)
          && !RemoveOldestProgressLocked())
        return true;
      if ((creation && pendingCreations == MaxCreations) || pending.Count == MaxPendingEvents) {
        CloseLocked();
        return false;
      }

      byKey.Add(key, pending.AddLast(message));
      if (creation)
        pendingCreations++;
      if (progress)
        pendingProgressEvents++;
      changed.TrySetResult();
      changed = NewSignal();
      return true;
    }
  }

  internal void RemoveSessionUpdates(Guid sessionId) {
    lock (gate) {
      bool removed = false;
      LinkedListNode<SessionSseEvent>? node = pending.First;
      while (node is not null) {
        LinkedListNode<SessionSseEvent>? next = node.Next;
        if (node.Value.Session == sessionId && node.Value.Name is "status" or "change" or "browser" or "tool-progress") {
          byKey.Remove(new EventKey(node.Value.Name, sessionId, node.Value.CoalesceKey));
          if (node.Value.Name == "tool-progress")
            pendingProgressEvents--;
          pending.Remove(node);
          removed = true;
        }
        node = next;
      }
      if (removed) {
        changed.TrySetResult();
        changed = NewSignal();
      }
    }
  }

  internal async Task<SessionSseRead> ReadNextAsync(TimeSpan heartbeat, CancellationToken cancellation) {
    while (true) {
      Task signal;
      lock (gate) {
        if (closed)
          return new(null, true, false);
        if (pending.First is { } first) {
          SessionSseEvent message = first.Value;
          byKey.Remove(new EventKey(message.Name, message.Session, message.CoalesceKey));
          pending.RemoveFirst();
          if (message.Name == "session-created")
            pendingCreations--;
          if (message.Name == "tool-progress")
            pendingProgressEvents--;
          return new(message, false, false);
        }
        signal = changed.Task;
      }

      try {
        await signal.WaitAsync(heartbeat, cancellation);
      } catch (TimeoutException) {
        return new(null, false, true);
      }
    }
  }

  internal void Close() {
    lock (gate)
      CloseLocked();
  }

  private void CloseLocked() {
    if (closed)
      return;
    closed = true;
    pending.Clear();
    byKey.Clear();
    pendingCreations = 0;
    pendingProgressEvents = 0;
    changed.TrySetResult();
  }

  private bool RemoveOldestProgressLocked() {
    for (LinkedListNode<SessionSseEvent>? node = pending.First; node is not null; node = node.Next) {
      if (node.Value.Name != "tool-progress")
        continue;
      byKey.Remove(new EventKey(node.Value.Name, node.Value.Session, node.Value.CoalesceKey));
      pending.Remove(node);
      pendingProgressEvents--;
      return true;
    }
    return false;
  }

  private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

  private readonly record struct EventKey(string Name, Guid? Session, string? CoalesceKey);
}
