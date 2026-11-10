using Urd.Core.Sessions;

namespace Urd.Core.Display;

// Unactivated shared service: captures are queued, detached derivation runs on one
// below-normal thread, never under a session/database gate. Publication and pinning
// share this gate with cancellation, invalidation, eviction and lease release.
internal sealed class MessageDisplays : IAsyncDisposable {
  readonly object gate = new();
  readonly MessageDisplayCacheLimits limits;
  readonly MessageDisplayCache cache;
  Func<MessageDisplaySource, MessageDisplayIndex>? build;
  readonly LinkedList<Work> pending = [];
  readonly CancellationTokenSource shutdown = new();
  readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
  Task shutdownCallbacks = Task.CompletedTask;
  Work? active;
  bool disposed;

  internal MessageDisplays(MessageDisplayCacheLimits limits,
      Func<MessageDisplaySource, MessageDisplayIndex>? build = null) {
    this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
    this.limits.Validate();
    cache = new(this.limits);
    this.build = build ?? (source => MessageDisplayBuilder.Build(source.Source));
    var worker = new Thread(Run) { IsBackground = true, Name = "Message displays", Priority = ThreadPriority.BelowNormal };
    worker.Start();
  }

  // The caller supplies exact committed extents; capture happens only on a miss.
  internal Task<MessageDisplayLease> RequestAsync(MessageDisplayKey key, SessionBase session,
      CancellationToken cancellation = default) {
    if (session.Id != key.SessionId) throw new ArgumentException("The session does not match the display key.", nameof(session));
    return RequestAsync(key, token => session.ReadMessageDisplaySourceAsync(key, token), cancellation);
  }

  internal Task<MessageDisplayLease> RequestAsync(MessageDisplayKey key,
      Func<CancellationToken, Task<MessageDisplaySource>> capture, CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(key);
    ArgumentNullException.ThrowIfNull(capture);
    cancellation.ThrowIfCancellationRequested();
    if (key.Pipeline != MessageDisplayIndex.PipelineVersion || key.FromSegment < 0 || key.ThroughSegment <= key.FromSegment
        || (key.FinalOutputCommitted && !key.IsClosed))
      throw new ArgumentException("The display key has an unsupported pipeline or invalid extent.", nameof(key));
    lock (gate) {
      ThrowIfDisposed();
      if (cache.Find(key) is { } entry) {
        cache.Reserve(entry);
        return Task.FromResult(Lease(entry));
      }
      if (active is { Invalidated: false, Accepting: true } && active.Key == key)
        return Observe(active, cancellation);
      var existing = pending.First;
      while (existing is not null && !Compatible(existing.Value.Key, key)) existing = existing.Next;
      if (existing?.Value.Key == key) return Observe(existing.Value, cancellation);
      if (existing is not null) {
        if (AtLeastAsRecent(existing.Value.Key, key))
          return Task.FromException<MessageDisplayLease>(new DisplaySupersededException());
        // Keep queue position so repeated updates cannot starve other keys.
        var previous = existing.Value;
        var replacement = new Work(key, capture);
        existing.Value = replacement;
        Fail(previous, new DisplaySupersededException());
        try {
          return Observe(replacement, cancellation);
        } catch {
          pending.Remove(existing);
          throw;
        }
      }
      if (pending.Count >= limits.PendingKeys) throw new DisplayQueueFullException();
      var work = new Work(key, capture);
      var result = Observe(work, cancellation);
      if (work.Interests.Count > 0) pending.AddLast(work);
      Monitor.Pulse(gate);
      return result;
    }
  }

  // Valid leases remain readable through invalidation and shutdown. Shutdown drains
  // work, not consumer-owned views; their accounting ends only on explicit release.
  internal DisplayPage Read(MessageDisplayLease lease, DisplayCursor cursor, DisplayPageLimits? limits = null) {
    lock (gate) return MessageDisplayReader.Read(cache.Get(lease.Get(this)), cursor, limits);
  }

  internal DisplayContextPage ReadContext(MessageDisplayLease lease, int context, DisplayCursor cursor,
      DisplayPageLimits? limits = null) {
    lock (gate) return MessageDisplayReader.ReadContext(cache.Get(lease.Get(this)), context, cursor, limits);
  }

  internal DisplayCursor Locate(MessageDisplayLease lease, int sourceUtf8Offset) {
    lock (gate) return cache.Get(lease.Get(this)).Locate(sourceUtf8Offset);
  }

  internal MessageContentCursor SourceCursor(MessageDisplayLease lease, int sourceUtf8Offset) {
    lock (gate) return cache.SourceCursor(lease.Get(this), sourceUtf8Offset);
  }

  internal void Release(MessageDisplayLease lease) {
    lock (gate) {
      if (lease.Entry is not { } entry) return;
      lease.Get(this);
      lease.Clear();
      cache.Release(entry);
    }
  }

  // The future history/deletion owner must fence changed sessions before serving.
  internal void Invalidate(Guid sessionId) {
    lock (gate) {
      ThrowIfDisposed();
      cache.Invalidate(sessionId);
      if (active?.Key.SessionId == sessionId) {
        active.Invalidated = true;
        Fail(active, new DisplayUnavailableException());
      }
      foreach (var work in pending.Where(work => work.Key.SessionId == sessionId).ToArray())
        Fail(work, new DisplayUnavailableException());
    }
  }

  internal (int Entries, long RetainedBytes, int PendingKeys) Usage {
    get { lock (gate) return (cache.Count, cache.RetainedBytes, pending.Count); }
  }

  public ValueTask DisposeAsync() {
    lock (gate) {
      if (!disposed) {
        disposed = true;
        cache.Clear();
        foreach (var work in pending.ToArray()) Fail(work, new ObjectDisposedException(nameof(MessageDisplays)));
        if (active is not null) Fail(active, new ObjectDisposedException(nameof(MessageDisplays)));
        // Only the worker disposes this source, after synchronous work returns.
        shutdownCallbacks = shutdown.CancelAsync();
        Monitor.Pulse(gate);
      }
    }
    return new(stopped.Task);
  }

  Task<MessageDisplayLease> Observe(Work work, CancellationToken cancellation) {
    cache.Reserve();
    var interest = new Interest(work, cancellation);
    work.Interests.Add(interest);
    interest.Registration = cancellation.UnsafeRegister(_ => Cancel(interest), null);
    if (interest.Completion.Task.IsCompleted) interest.Registration.Unregister();
    return interest.Completion.Task;
  }

  void Cancel(Interest interest) {
    lock (gate) {
      if (!Detach(interest)) return;
      cache.ReleaseReservation();
      interest.Completion.TrySetCanceled(interest.Cancellation);
    }
  }

  bool Detach(Interest interest) {
    if (!interest.Work.Interests.Remove(interest)) return false;
    interest.Registration.Unregister();
    if (interest.Work.Interests.Count == 0 && !ReferenceEquals(active, interest.Work)) {
      pending.Remove(interest.Work);
      interest.Work.Capture = null;
    }
    return true;
  }

  void Fail(Work work, Exception error) {
    work.Accepting = false;
    foreach (var interest in work.Interests.ToArray()) {
      Detach(interest);
      cache.ReleaseReservation();
      interest.Completion.TrySetException(error);
    }
  }

  MessageDisplayLease Lease(MessageDisplayCache.Entry entry) {
    entry.Pins++;
    return new(this, entry);
  }

  Work? TakeNext() {
    lock (gate) {
      while (!disposed && pending.Count == 0) Monitor.Wait(gate);
      // Shutdown must publish its cancellation task before the worker can exit.
      if (disposed) return null;
      active = pending.First!.Value;
      pending.RemoveFirst();
      return active;
    }
  }

  void Run() {
    try {
      while (TakeNext() is { } work) {
        Process(work);
        lock (gate) {
          work.Capture = null;
          active = null;
        }
      }
    } finally {
      build = null;
      Exception? failure = null;
      try {
        shutdownCallbacks.GetAwaiter().GetResult();
      } catch (Exception error) {
        failure = error;
      }
      shutdown.Dispose();
      if (failure is null) stopped.TrySetResult();
      else stopped.TrySetException(failure);
    }
  }

  void Process(Work work) {
    try {
      var source = work.Capture!(shutdown.Token).GetAwaiter().GetResult();
      if (!work.Key.Matches(source)) throw new DisplayUnavailableException();
      var index = build!(source); // Synchronous parsing is deliberately non-cancellable.
      if (index.SourceUtf8Bytes != source.Utf8Bytes) throw new DisplayUnavailableException();
      lock (gate) {
        if (disposed || work.Invalidated) return;
        work.Accepting = false;
        foreach (var interest in work.Interests.ToArray())
          if (interest.Cancellation.IsCancellationRequested) Cancel(interest);
        if (work.Interests.Count == 0) return;
        var entry = cache.Add(work.Key, source, index);
        foreach (var interest in work.Interests.ToArray()) {
          var lease = Lease(entry); // Pin before completing any observer's task.
          Detach(interest);
          interest.Completion.TrySetResult(lease);
        }
      }
    } catch (Exception error) {
      lock (gate) Fail(work, error);
    }
  }

  static bool Compatible(MessageDisplayKey left, MessageDisplayKey right) =>
      left.SessionId == right.SessionId && left.Message == right.Message
      && left.FromSegment == right.FromSegment && left.Pipeline == right.Pipeline
      && (AtLeastAsRecent(left, right) || AtLeastAsRecent(right, left));

  static bool AtLeastAsRecent(MessageDisplayKey candidate, MessageDisplayKey previous) =>
      candidate.ThroughSegment >= previous.ThroughSegment && (candidate.IsClosed || !previous.IsClosed)
      && (candidate.FinalOutputCommitted || !previous.FinalOutputCommitted);

  void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

  sealed class Work(MessageDisplayKey key, Func<CancellationToken, Task<MessageDisplaySource>> capture) {
    internal MessageDisplayKey Key { get; } = key;
    internal Func<CancellationToken, Task<MessageDisplaySource>>? Capture { get; set; } = capture;
    internal List<Interest> Interests { get; } = [];
    internal bool Invalidated { get; set; }
    internal bool Accepting { get; set; } = true;
  }

  sealed class Interest(Work work, CancellationToken cancellation) {
    internal Work Work { get; } = work;
    internal CancellationToken Cancellation { get; } = cancellation;
    internal CancellationTokenRegistration Registration { get; set; }
    internal TaskCompletionSource<MessageDisplayLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
  }
}
