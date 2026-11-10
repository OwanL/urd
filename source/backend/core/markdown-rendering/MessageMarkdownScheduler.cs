using Urd.Core.Sessions;

namespace Urd.Core.MarkdownRendering;

// Unactivated shared service: one large and one small below-normal worker capture
// and derive outside session/database gates. No captured sources queue. Publication and pinning
// share this gate with cancellation, invalidation, eviction and lease release.
internal sealed class MessageMarkdownScheduler : IAsyncDisposable {
  readonly object gate = new();
  readonly MessageMarkdownCacheLimits limits;
  readonly MessageMarkdownCache cache;
  Func<MessageMarkdownSource, MarkdownRenderIndex>? parse;
  readonly LinkedList<Work> pending = [];
  readonly CancellationTokenSource shutdown = new();
  readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
  Task shutdownCallbacks = Task.CompletedTask;
  readonly Work?[] active = new Work?[2];
  int remainingWorkers = 2;
  bool disposed;

  internal MessageMarkdownScheduler(MessageMarkdownCacheLimits limits,
      Func<MessageMarkdownSource, MarkdownRenderIndex>? parse = null) {
    this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
    this.limits.Validate();
    cache = new(this.limits);
    this.parse = parse ?? (source => MarkdownParser.Parse(source.Source));
    // An injected delegate must support concurrent calls, just like MarkdownParser.
    for (int lane = 0; lane < active.Length; lane++) {
      int selected = lane;
      var worker = new Thread(() => Run(selected)) {
        IsBackground = true, Name = $"Message markdown {(lane == 0 ? "large" : "small")}", Priority = ThreadPriority.BelowNormal
      };
      worker.Start();
    }
  }

  // The caller supplies exact committed extents; capture happens only on a miss.
  internal Task<MessageMarkdownLease> RequestAsync(MessageMarkdownKey key, SessionBase session,
      CancellationToken cancellation = default) {
    if (session.Id != key.SessionId) throw new ArgumentException("The session does not match the markdown key.", nameof(session));
    return RequestAsync(key, token => session.ReadMessageMarkdownSourceAsync(key, token), cancellation);
  }

  internal Task<MessageMarkdownLease> RequestAsync(MessageMarkdownKey key,
      Func<CancellationToken, Task<MessageMarkdownSource>> capture, CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(key);
    ArgumentNullException.ThrowIfNull(capture);
    cancellation.ThrowIfCancellationRequested();
    key.Validate();
    lock (gate) {
      ThrowIfDisposed();
      cache.ValidateSize(key);
      foreach (var retained in active.Concat(pending).OfType<Work>()) key.ValidateSize(retained.Key);
      if (cache.Find(key) is { } entry) {
        cache.Reserve(entry);
        return Task.FromResult(Lease(entry));
      }
      var running = active.FirstOrDefault(work => work is { Invalidated: false, Accepting: true } && work.Key == key);
      if (running is not null) return Observe(running, cancellation);
      var existing = pending.First;
      while (existing is not null && !Compatible(existing.Value.Key, key)) existing = existing.Next;
      if (existing?.Value.Key == key) return Observe(existing.Value, cancellation);
      if (existing is not null) {
        if (AtLeastAsRecent(existing.Value.Key, key))
          return Task.FromException<MessageMarkdownLease>(new MarkdownSupersededException());
        // Keep queue position so repeated updates cannot starve other keys.
        var previous = existing.Value;
        var replacement = new Work(key, capture);
        existing.Value = replacement;
        Fail(previous, new MarkdownSupersededException());
        try {
          var observation = Observe(replacement, cancellation);
          Monitor.PulseAll(gate);
          return observation;
        } catch {
          pending.Remove(existing);
          throw;
        }
      }
      if (pending.Count >= limits.PendingKeys) throw new MarkdownQueueFullException();
      var work = new Work(key, capture);
      var result = Observe(work, cancellation);
      if (work.Interests.Count > 0) pending.AddLast(work);
      Monitor.PulseAll(gate);
      return result;
    }
  }

  // Valid leases remain readable through invalidation and shutdown. Shutdown drains
  // work, not consumer-owned views; their accounting ends only on explicit release.
  internal MarkdownPiecePage Read(MessageMarkdownLease lease, MarkdownRenderCursor cursor, MarkdownPageLimits? limits = null) {
    lock (gate) return MarkdownPageReader.Read(cache.Get(lease.Get(this)), cursor, limits);
  }

  internal MarkdownPiecePage ReadBefore(MessageMarkdownLease lease, MarkdownRenderCursor exclusiveEndCursor,
      MarkdownPageLimits? limits = null) {
    lock (gate) return MarkdownPageReader.ReadBefore(cache.Get(lease.Get(this)), exclusiveEndCursor, limits);
  }

  internal MarkdownContextPage ReadContext(MessageMarkdownLease lease, int context, MarkdownRenderCursor cursor,
      MarkdownPageLimits? limits = null) {
    lock (gate) return MarkdownPageReader.ReadContext(cache.Get(lease.Get(this)), context, cursor, limits);
  }

  internal MarkdownRenderCursor Locate(MessageMarkdownLease lease, int sourceUtf8Offset) {
    lock (gate) return cache.Get(lease.Get(this)).Locate(sourceUtf8Offset);
  }

  internal MessageContentCursor SourceCursor(MessageMarkdownLease lease, int sourceUtf8Offset) {
    lock (gate) return cache.SourceCursor(lease.Get(this), sourceUtf8Offset);
  }

  internal void Release(MessageMarkdownLease lease) {
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
      foreach (var work in active.OfType<Work>().Where(work => work.Key.SessionId == sessionId)) {
        work.Invalidated = true;
        Fail(work, new MarkdownUnavailableException());
      }
      foreach (var work in pending.Where(work => work.Key.SessionId == sessionId).ToArray())
        Fail(work, new MarkdownUnavailableException());
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
        foreach (var work in pending.ToArray()) Fail(work, new ObjectDisposedException(nameof(MessageMarkdownScheduler)));
        foreach (var work in active.OfType<Work>()) Fail(work, new ObjectDisposedException(nameof(MessageMarkdownScheduler)));
        // Only the last worker disposes this source, after both synchronous parses return.
        shutdownCallbacks = shutdown.CancelAsync();
        Monitor.PulseAll(gate);
      }
    }
    return new(stopped.Task);
  }

  Task<MessageMarkdownLease> Observe(Work work, CancellationToken cancellation) {
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
    if (interest.Work.Interests.Count == 0 && !active.Contains(interest.Work)) {
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

  MessageMarkdownLease Lease(MessageMarkdownCache.Entry entry) {
    entry.Pins++;
    return new(this, entry);
  }

  Work? TakeNext(int lane) {
    lock (gate) {
      while (!disposed) {
        var next = pending.First;
        while (next is not null && Lane(next.Value.Key) != lane) next = next.Next;
        if (next is not null) {
          active[lane] = next.Value;
          pending.Remove(next);
          return active[lane];
        }
        Monitor.Wait(gate);
      }
      return null;
    }
  }

  int Lane(MessageMarkdownKey key) => key.SourceUtf8Bytes <= limits.SmallMessageUtf8Bytes ? 1 : 0;

  void Run(int lane) {
    try {
      while (TakeNext(lane) is { } work) {
        Process(work);
        lock (gate) {
          work.Capture = null;
          active[lane] = null;
        }
      }
    } finally {
      WorkerStopped();
    }
  }

  void WorkerStopped() {
    if (Interlocked.Decrement(ref remainingWorkers) == 0) {
      parse = null;
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
      if (!work.Key.Matches(source)) throw new MarkdownUnavailableException();
      var index = parse!(source); // Synchronous parsing is deliberately non-cancellable.
      if (index.SourceUtf8Bytes != source.Utf8Bytes) throw new MarkdownUnavailableException();
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

  static bool Compatible(MessageMarkdownKey left, MessageMarkdownKey right) =>
      left.SessionId == right.SessionId && left.Message == right.Message
      && left.FromSegment == right.FromSegment && left.Pipeline == right.Pipeline
      && (AtLeastAsRecent(left, right) || AtLeastAsRecent(right, left));

  static bool AtLeastAsRecent(MessageMarkdownKey candidate, MessageMarkdownKey previous) =>
      candidate.ThroughSegment >= previous.ThroughSegment && (candidate.IsClosed || !previous.IsClosed)
      && (candidate.FinalOutputCommitted || !previous.FinalOutputCommitted);

  void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

  sealed class Work(MessageMarkdownKey key, Func<CancellationToken, Task<MessageMarkdownSource>> capture) {
    internal MessageMarkdownKey Key { get; } = key;
    internal Func<CancellationToken, Task<MessageMarkdownSource>>? Capture { get; set; } = capture;
    internal List<Interest> Interests { get; } = [];
    internal bool Invalidated { get; set; }
    internal bool Accepting { get; set; } = true;
  }

  sealed class Interest(Work work, CancellationToken cancellation) {
    internal Work Work { get; } = work;
    internal CancellationToken Cancellation { get; } = cancellation;
    internal CancellationTokenRegistration Registration { get; set; }
    internal TaskCompletionSource<MessageMarkdownLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
  }
}
