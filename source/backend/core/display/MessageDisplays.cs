using Urd.Core.Sessions;

namespace Urd.Core.Display;

// One instance is intended to be shared by the future session owner. This service
// is deliberately unactivated: no manager, route or session creates it.
// Only lightweight captures are queued. Synchronous Markdig work runs on this
// dedicated below-normal thread, never under a session/database gate.
internal sealed class MessageDisplays : IAsyncDisposable {
  readonly object gate = new();
  readonly MessageDisplayCacheLimits limits;
  readonly MessageDisplayCache cache;
  readonly Func<MessageDisplaySource, MessageDisplayIndex> build;
  readonly LinkedList<Work> pending = [];
  readonly CancellationTokenSource shutdown = new();
  readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

  // The caller supplies the exact extent and completion facts from bounded metadata.
  // Capture/validation is deferred to the worker, including on a cache miss only.
  internal Task<MessageDisplayReady> RequestAsync(MessageDisplayKey key, SessionBase session,
      CancellationToken cancellation = default) {
    if (session.Id != key.SessionId) throw new ArgumentException("The session does not match the display key.", nameof(session));
    return RequestAsync(key, token => session.ReadMessageDisplaySourceAsync(key, token), cancellation);
  }

  internal Task<MessageDisplayReady> RequestAsync(MessageDisplayKey key,
      Func<CancellationToken, Task<MessageDisplaySource>> capture, CancellationToken cancellation = default) {
    ArgumentNullException.ThrowIfNull(key);
    ArgumentNullException.ThrowIfNull(capture);
    cancellation.ThrowIfCancellationRequested();
    if (key.Pipeline != MessageDisplayIndex.PipelineVersion || key.FromSegment < 0 || key.ThroughSegment <= key.FromSegment
        || (key.FinalOutputCommitted && !key.IsClosed))
      throw new ArgumentException("The display key has an unsupported pipeline or invalid extent.", nameof(key));
    lock (gate) {
      ThrowIfDisposed();
      if (cache.Find(key) is { } ready) return Task.FromResult(ready);
      if (active is { Invalidated: false } && !active.Completion.Task.IsCompleted && active.Key == key)
        return Observe(active, cancellation);
      var existing = pending.First;
      while (existing is not null && !Compatible(existing.Value.Key, key)) existing = existing.Next;
      if (existing?.Value.Key == key) return Observe(existing.Value, cancellation);
      if (existing is not null) {
        if (AtLeastAsRecent(existing.Value.Key, key))
          return Task.FromException<MessageDisplayReady>(new DisplaySupersededException());
        // Preserve queue position: repeated extent updates cannot starve other keys.
        var previous = existing.Value;
        previous.Capture = null;
        previous.Completion.TrySetException(new DisplaySupersededException());
        var replacement = new Work(key, capture);
        existing.Value = replacement;
        return Observe(replacement, cancellation);
      }
      if (pending.Count >= limits.PendingKeys) throw new DisplayQueueFullException();
      var work = new Work(key, capture);
      pending.AddLast(work);
      Monitor.Pulse(gate);
      return Observe(work, cancellation);
    }
  }

  internal DisplayPage Read(MessageDisplayReady ready, DisplayCursor cursor, DisplayPageLimits? limits = null) {
    lock (gate) {
      ThrowIfDisposed();
      return MessageDisplayReader.Read(cache.Get(ready), cursor, limits);
    }
  }

  internal DisplayContextPage ReadContext(MessageDisplayReady ready, int context, DisplayCursor cursor,
      DisplayPageLimits? limits = null) {
    lock (gate) {
      ThrowIfDisposed();
      return MessageDisplayReader.ReadContext(cache.Get(ready), context, cursor, limits);
    }
  }

  internal DisplayCursor Locate(MessageDisplayReady ready, int sourceUtf8Offset) {
    lock (gate) {
      ThrowIfDisposed();
      return cache.Get(ready).Locate(sourceUtf8Offset);
    }
  }

  internal MessageContentCursor SourceCursor(MessageDisplayReady ready, int sourceUtf8Offset) {
    lock (gate) {
      ThrowIfDisposed();
      return cache.SourceCursor(ready, sourceUtf8Offset);
    }
  }

  // Future history/deletion owner must call this before serving a changed session.
  // In-flight synchronous work finishes, but cannot publish into the new generation.
  internal void Invalidate(Guid sessionId) {
    lock (gate) {
      ThrowIfDisposed();
      cache.Invalidate(sessionId);
      if (active?.Key.SessionId == sessionId) {
        active.Invalidated = true;
        active.Completion.TrySetException(new DisplayUnavailableException());
      }
      var node = pending.First;
      while (node is not null) {
        var next = node.Next;
        if (node.Value.Key.SessionId == sessionId) {
          pending.Remove(node);
          node.Value.Capture = null;
          node.Value.Completion.TrySetException(new DisplayUnavailableException());
        }
        node = next;
      }
    }
  }

  internal (int Entries, long RetainedBytes, int PendingKeys) Usage {
    get { lock (gate) return (cache.Count, cache.RetainedBytes, pending.Count); }
  }

  public ValueTask DisposeAsync() {
    bool stop;
    lock (gate) {
      stop = !disposed;
      disposed = true;
      if (stop) {
        cache.Clear();
        foreach (var work in pending) {
          work.Capture = null;
          work.Completion.TrySetException(new ObjectDisposedException(nameof(MessageDisplays)));
        }
        pending.Clear();
        active?.Completion.TrySetException(new ObjectDisposedException(nameof(MessageDisplays)));
        Monitor.Pulse(gate);
      }
    }
    if (stop) shutdown.Cancel();
    return new(stopped.Task);
  }

  async Task<MessageDisplayReady> Observe(Work work, CancellationToken cancellation) {
    work.Interests++;
    try {
      return await work.Completion.Task.WaitAsync(cancellation).ConfigureAwait(false);
    } finally {
      lock (gate) {
        work.Interests--;
        if (work.Interests == 0 && !ReferenceEquals(active, work)) {
          pending.Remove(work);
          work.Capture = null;
        }
      }
    }
  }

  void Run() {
    try {
      while (true) {
        Work work;
        lock (gate) {
          while (!disposed && pending.Count == 0) Monitor.Wait(gate);
          if (disposed) return;
          work = pending.First!.Value;
          pending.RemoveFirst();
          active = work;
        }
        Process(work);
        lock (gate) {
          work.Capture = null;
          active = null;
        }
      }
    } finally {
      stopped.TrySetResult();
    }
  }

  void Process(Work work) {
    try {
      var source = work.Capture!(shutdown.Token).GetAwaiter().GetResult();
      if (!work.Key.Matches(source)) throw new DisplayUnavailableException();
      var index = build(source); // Intentionally non-cancellable synchronous parsing.
      if (index.SourceUtf8Bytes != source.Utf8Bytes) throw new DisplayUnavailableException();
      lock (gate) {
        if (disposed || work.Invalidated) return;
        // A newer pending extent never suppresses an already completed result.
        var ready = cache.Add(work.Key, source, index);
        work.Completion.TrySetResult(ready);
      }
    } catch (Exception error) {
      // No negative retention: storage/parse failures are explicit and retryable.
      work.Completion.TrySetException(error);
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
    internal TaskCompletionSource<MessageDisplayReady> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Interests { get; set; }
    internal bool Invalidated { get; set; }
  }
}
