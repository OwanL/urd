namespace Urd.Server;

internal sealed record SessionSseEvent(string Name, Guid? Session, object Data);

internal readonly record struct SessionSseRead(SessionSseEvent? Event, bool Closed, bool Heartbeat);

// Coalesces live session updates and bounds unique session-creation hints per page.
internal sealed class SessionEventQueue {
  internal const int MaxOpenSessions = 128;
  internal const int MaxCreations = 128;
  private const int MaxPendingEvents = MaxOpenSessions * 2 + MaxCreations;
  private readonly object gate = new();
  private readonly LinkedList<SessionSseEvent> pending = new();
  private readonly Dictionary<EventKey, LinkedListNode<SessionSseEvent>> byKey = new();
  private TaskCompletionSource changed = NewSignal();
  private int pendingCreations;
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

      var key = new EventKey(message.Name, message.Session);
      if (byKey.TryGetValue(key, out LinkedListNode<SessionSseEvent>? existing)) {
        existing.Value = message;
        return true;
      }

      bool creation = message.Name == "session-created";
      if ((creation && pendingCreations == MaxCreations) || pending.Count == MaxPendingEvents) {
        CloseLocked();
        return false;
      }

      byKey.Add(key, pending.AddLast(message));
      if (creation)
        pendingCreations++;
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
        if (node.Value.Session == sessionId && node.Value.Name is "status" or "change") {
          byKey.Remove(new EventKey(node.Value.Name, sessionId));
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
          byKey.Remove(new EventKey(message.Name, message.Session));
          pending.RemoveFirst();
          if (message.Name == "session-created")
            pendingCreations--;
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
    changed.TrySetResult();
  }

  private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

  private readonly record struct EventKey(string Name, Guid? Session);
}
