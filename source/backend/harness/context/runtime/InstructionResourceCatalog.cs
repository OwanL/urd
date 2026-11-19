namespace Urd.Core.Context;

// Roots are served from last-known rows while background scans reconcile them; a request only
// waits when a cold root has no persisted rows and its in-flight scan has matched nothing yet.
internal sealed class InstructionResourceCatalog : IAsyncDisposable {
  private const int maxPendingPaths = 4096;
  private static readonly StringComparer pathNames = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
  private static readonly StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

  private readonly string databasePath;
  private readonly Func<string, Action<string>, CancellationToken, Task<string[]>> scan;
  private readonly Func<string, Action<string, string?>, Action, IDisposable?> watch;
  private readonly object gate = new();
  private readonly Dictionary<string, Root> roots = new(pathNames);
  private readonly HashSet<Task> jobs = [];
  private InstructionResourceCatalogIndex? index;
  private Task? shutdown;

  internal InstructionResourceCatalog(string databasePath, FdResourceScanner scanner)
      : this(databasePath, (directory, matched, cancellation) => scanner.FindPathsAsync(directory, cancellation, matched)) { }

  internal InstructionResourceCatalog(string databasePath, Func<string, Action<string>, CancellationToken, Task<string[]>> scan,
      Func<string, Action<string, string?>, Action, IDisposable?>? watch = null) {
    this.databasePath = Path.GetFullPath(databasePath);
    this.scan = scan;
    this.watch = watch ?? InstructionResourceCatalogWatch.TryOpen;
  }

  internal async Task<InstructionResourceCatalogResult> FindPathsAsync(string directory, CancellationToken cancellation) {
    directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    Root? completed = null;
    while (true) {
      cancellation.ThrowIfCancellationRequested();
      Root root;
      Task work;
      Task firstBatch;
      lock (gate) {
        ObjectDisposedException.ThrowIf(shutdown is not null, this);
        if (InstructionResourceCatalogWatch.Entry(directory) != FileAttributes.Directory)
          return new([], new(InstructionResourceIndexPhase.Complete, 0));

        root = CoveringRoot(directory) ?? AddRoot(directory);
        // This caller may use the scan it just awaited; later requests need live watcher coverage.
        if (root.Ready && !root.Lost && (root.Watch is not null || root == completed) &&
            root.Work is null && root.Pending.Count == 0) {
          string[] indexed = Index().Read(directory);
          return new(indexed, new(InstructionResourceIndexPhase.Complete, indexed.Length));
        }

        // Last-known rows and the matches an in-flight scan has already produced are served
        // without waiting; a cold root joins the scan until it publishes its first matches.
        (string[] served, bool staleRows) = CoveringPaths(root, directory);
        if (served.Length > 0) {
          if (root.Work is null)
            StartReconcile(root);
          return new(served, new(staleRows ? InstructionResourceIndexPhase.Refreshing : InstructionResourceIndexPhase.Partial, served.Length));
        }
        work = root.Work is null ? StartReconcile(root) : root.Work;
        firstBatch = root.FirstBatch!.Task;
      }

      // A caller cancels only its wait, not a scan another cwd may be sharing.
      Task finished = await Task.WhenAny(work, firstBatch, root.Retired.Task).WaitAsync(cancellation);
      if (finished == work && !root.Retired.Task.IsCompleted) {
        if (!work.IsCompletedSuccessfully)
          await work; // A cold caller waits for the scan, so its failure or cancellation is observable.
        completed = root;
      }
    }
  }

  private Root? CoveringRoot(string directory) {
    for (string? path = directory; path is not null; path = Path.GetDirectoryName(path)) {
      // A nested cwd can establish its own watcher when the broader root has no live coverage.
      if (roots.TryGetValue(path, out Root? root) &&
          (pathNames.Equals(path, directory) || root.Ready && root.Watch is not null))
        return root;
    }
    return null;
  }

  private Root AddRoot(string directory) {
    var root = new Root(directory);
    roots.Add(directory, root);
    return root;
  }

  private void RetireDescendants(string directory) {
    // Keep active writers disjoint: an old narrow scan must not overwrite a newer broad one.
    foreach (Root child in roots.Values.Where(root => IsBelow(root.Path, directory)).ToArray()) {
      roots.Remove(child.Path);
      Retire(child);
    }
  }

  private Task StartReconcile(Root root) {
    if (root.Watch is null)
      RetireDescendants(root.Path);
    return Start(root, debounce: false);
  }

  private Task Start(Root root, bool debounce) {
    root.Buffer.Clear();
    root.FirstBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task work = Task.Run(() => UpdateAsync(root, debounce));
    root.Work = work;
    Track(work);
    return work;
  }

  private void Track(Task task) {
    jobs.Add(task);
    _ = task.ContinueWith(finished => {
      _ = finished.Exception;
      lock (gate)
        jobs.Remove(finished);
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
  }

  private async Task UpdateAsync(Root root, bool debounce) {
    CancellationToken cancellation = root.Stop.Token;
    try {
      if (debounce)
        await Task.Delay(TimeSpan.FromMilliseconds(25), cancellation);

      HashSet<string>? baseline = null;
      int attempts = 0;
      bool reconcile;
      lock (gate)
        reconcile = !root.Ready || root.Lost || root.Watch is null;

      while (true) {
        cancellation.ThrowIfCancellationRequested();
        if (reconcile) {
          // Recovery is coalesced. A watcher which keeps failing leaves the root to settle
          // on its last reconcile instead of rescanning for every request.
          ResetWatch(root, tryWatching: attempts++ < 2);
          try {
            baseline = new(await ScanAsync(root.Path, path => BufferMatch(root, path), cancellation), pathNames);
            lock (gate) {
              root.Buffer.Clear();
              if (!root.Lost && !root.Retired.Task.IsCompleted) {
                RefreshTracked(root, baseline);
                root.SnapshotKnown = true;
              }
            }
          } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            lock (gate) {
              root.Buffer.Clear();
              if (root.Lost)
                continue;
            }
            throw;
          }
          reconcile = false;
        }

        string[] pending;
        lock (gate) {
          if (root.Retired.Task.IsCompleted)
            return;
          if (root.Lost) {
            reconcile = true;
            continue;
          }
          if (root.Pending.Count == 0) {
            var storage = Index();
            if (baseline is not null) {
              // A narrower root may have completed while this scan was running. Retire its
              // writer before publishing the overlapping snapshot.
              RetireDescendants(root.Path);
              storage.Apply(root.Path, [new(root.Path, baseline)]);
            }
            RefreshTracked(root, storage.Read(root.Path));
            root.Ready = true;
            root.Work = null;
            return;
          }
          pending = root.Pending.ToArray();
          root.Pending.Clear();
        }

        List<InstructionResourcePathUpdate> updates;
        try {
          updates = await ReadUpdatesAsync(root.Path, pending, cancellation);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
          lock (gate) {
            if (root.Lost) {
              reconcile = true;
              continue;
            }
          }
          throw;
        }
        lock (gate) {
          if (root.Retired.Task.IsCompleted)
            return;
          if (root.Lost) {
            reconcile = true;
            continue;
          }
          if (baseline is null) {
            var storage = Index();
            storage.Apply(null, updates);
            RefreshTracked(root, storage.Read(root.Path));
          } else {
            foreach (var update in updates) {
              baseline.RemoveWhere(path => pathNames.Equals(path, update.Path) || IsBelow(path, update.Path));
              baseline.UnionWith(update.Paths);
            }
            RefreshTracked(root, baseline);
          }
        }
      }
    } catch {
      IDisposable? previous;
      lock (gate) {
        previous = DetachWatch(root);
        root.Ready = false;
        root.Work = null;
      }
      previous?.Dispose();
      throw;
    }
  }

  private async Task<string[]> ScanAsync(string directory, Action<string>? matched, CancellationToken cancellation) {
    if (InstructionResourceCatalogWatch.Entry(directory) != FileAttributes.Directory)
      return [];
    try {
      string[] paths = await scan(directory, path => {
        if (IsBelow(path, directory) && FdResourceScanner.IsResourcePath(path))
          matched?.Invoke(path);
      }, cancellation);
      cancellation.ThrowIfCancellationRequested();
      return paths.Where(path => IsBelow(path, directory) && FdResourceScanner.IsResourcePath(path))
          .Distinct(pathNames).ToArray();
    } catch (IOException) when (InstructionResourceCatalogWatch.Entry(directory) != FileAttributes.Directory) {
      // A notified directory may have moved again before fd starts. The new path has its own event.
      return [];
    }
  }

  // A cold scan publishes each match as fd reports it, so early callers serve partial results.
  private void BufferMatch(Root root, string path) {
    lock (gate) {
      if (root.Retired.Task.IsCompleted)
        return;
      if (root.Buffer.Add(path))
        root.FirstBatch?.TrySetResult();
    }
  }

  private (string[] Paths, bool StaleRows) CoveringPaths(Root root, string directory) {
    string[] rows = index is not null || File.Exists(databasePath) ? Index().Read(directory) : [];
    var paths = new HashSet<string>(rows, pathNames);
    paths.UnionWith(root.Buffer.Where(path => IsBelow(path, directory)));
    return (paths.Order(pathNames).ToArray(), rows.Length > 0);
  }

  private async Task<List<InstructionResourcePathUpdate>> ReadUpdatesAsync(string directory, string[] pending,
      CancellationToken cancellation) {
    var covered = new HashSet<string>(pathNames);
    var updates = new List<InstructionResourcePathUpdate>();
    foreach (string path in pending.OrderBy(path => path.Length)) {
      cancellation.ThrowIfCancellationRequested();
      if (HasUpdatedAncestor(path, directory, covered))
        continue;

      FileAttributes? entry = InstructionResourceCatalogWatch.Entry(path);
      string[] paths = entry switch {
        FileAttributes.Directory => await ScanAsync(path, null, cancellation),
        FileAttributes.Normal when FdResourceScanner.IsResourcePath(path) => [path],
        _ => [],
      };
      updates.Add(new(path, paths));
      covered.Add(path);
    }
    return updates;
  }

  private static bool HasUpdatedAncestor(string path, string directory, HashSet<string> updated) {
    for (string? parent = Path.GetDirectoryName(path); parent is not null && !pathNames.Equals(parent, directory);
        parent = Path.GetDirectoryName(parent)) {
      if (updated.Contains(parent))
        return true;
    }
    return false;
  }

  private void ResetWatch(Root root, bool tryWatching) {
    IDisposable? previous;
    long generation;
    lock (gate) {
      if (root.Retired.Task.IsCompleted)
        return;
      previous = DetachWatch(root);
      generation = root.Generation;
    }
    previous?.Dispose();

    if (!tryWatching)
      return;
    IDisposable? current = watch(root.Path, (path, renamedTo) => Notify(root, generation, path, renamedTo),
        () => Notify(root, generation, null, null));
    lock (gate) {
      if (!root.Retired.Task.IsCompleted) {
        root.Watch = current;
        if (current is null)
          DetachWatch(root);
        return;
      }
    }
    current?.Dispose();
  }

  private static IDisposable? DetachWatch(Root root) {
    IDisposable? previous = root.Watch;
    root.Watch = null;
    root.Generation++;
    root.Lost = false;
    root.SnapshotKnown = false;
    root.Pending.Clear();
    return previous;
  }

  private void Notify(Root root, long generation, string? path, string? renamedTo) {
    lock (gate) {
      if (root.Retired.Task.IsCompleted || root.Generation != generation || root.Lost)
        return;
      if (path is not null)
        QueuePath(root, path);
      if (renamedTo is not null)
        QueuePath(root, renamedTo);
      if (path is null || root.Pending.Count > maxPendingPaths) {
        root.Lost = true;
        root.Pending.Clear();
      }
      if (root.Work is null && (root.Lost || root.Pending.Count != 0))
        Start(root, debounce: true);
    }
  }

  private void QueuePath(Root root, string path) {
    // Junk directories below the root are pruned by the scanner too, so their notifications
    // neither update the index nor count toward the pending-event overflow limit.
    if (!IsBelow(path, root.Path) || IsIndexFile(path) || FdResourceScanner.IsExcluded(path, root.Path))
      return;
    if (!FdResourceScanner.IsResourcePath(path) && !root.Tracked.Contains(path)) {
      // One non-following check distinguishes a new directory from an ordinary file.
      // The full lineage check remains in ReadUpdatesAsync before scanning it.
      try {
        if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
          return;
      } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
        // Before the initial snapshot is known, a vanished directory may contain
        // paths returned by the in-flight scan.
        if (root.SnapshotKnown)
          return;
      }
    }
    // Keep the newest spelling of a Windows path, including a case-only rename.
    root.Pending.Remove(path);
    root.Pending.Add(path);
  }

  private static void RefreshTracked(Root root, IEnumerable<string> paths) {
    root.Tracked.Clear();
    foreach (string path in paths) {
      for (string? current = path; current is not null && IsBelow(current, root.Path);
          current = Path.GetDirectoryName(current))
        root.Tracked.Add(current);
    }
  }

  private bool IsIndexFile(string path) => pathNames.Equals(path, databasePath) ||
      pathNames.Equals(path, databasePath + "-journal") || pathNames.Equals(path, databasePath + "-wal") ||
      pathNames.Equals(path, databasePath + "-shm");

  private static bool IsBelow(string path, string directory) => path.Length > directory.Length &&
      path.StartsWith(directory, pathComparison) &&
      (Path.EndsInDirectorySeparator(directory) || path[directory.Length] == Path.DirectorySeparatorChar);

  private InstructionResourceCatalogIndex Index() => index ??= InstructionResourceCatalogIndex.Open(databasePath, pathNames);

  private void Retire(Root root) {
    root.Retired.TrySetResult();
    IDisposable? previous = DetachWatch(root);
    Task? work = root.Work;
    Track(Task.Run(async () => {
      previous?.Dispose();
      await root.Stop.CancelAsync();
      if (work is not null)
        await Task.WhenAny(work);
      root.Stop.Dispose();
    }));
  }

  public ValueTask DisposeAsync() {
    lock (gate) {
      if (shutdown is not null)
        return new(shutdown);
      foreach (Root root in roots.Values)
        Retire(root);
      roots.Clear();
      shutdown = CloseAsync(jobs.ToArray());
      return new(shutdown);
    }
  }

  private async Task CloseAsync(Task[] running) {
    try {
      await Task.WhenAll(running);
    } catch {
      // Waiters see operation errors; shutdown still releases the cache.
    } finally {
      index?.Dispose();
    }
  }

  private sealed class Root(string path) {
    internal string Path { get; } = path;
    internal CancellationTokenSource Stop { get; } = new();
    internal TaskCompletionSource Retired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal HashSet<string> Pending { get; } = new(pathNames);
    internal HashSet<string> Tracked { get; } = new(pathNames);
    // Matches buffered by the in-flight reconcile scan before its snapshot publishes.
    internal HashSet<string> Buffer { get; } = new(pathNames);
    internal TaskCompletionSource? FirstBatch;
    internal IDisposable? Watch;
    internal Task? Work;
    internal long Generation;
    internal bool Ready;
    internal bool SnapshotKnown;
    internal bool Lost;
  }
}
