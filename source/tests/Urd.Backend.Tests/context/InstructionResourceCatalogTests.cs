using Microsoft.Data.Sqlite;
using Urd.Core.Context;
using Xunit;

namespace Urd.Backend.Tests.Context;

public sealed class InstructionResourceCatalogTests : IDisposable {
  private static readonly TimeSpan deadline = TimeSpan.FromSeconds(10);
  private readonly string temporary;
  private string Project => Path.Combine(temporary, "project");
  private string Database => Path.Combine(temporary, "catalog.sqlite");

  public InstructionResourceCatalogTests() {
    string baseDirectory = Path.GetTempPath();
    if (OperatingSystem.IsMacOS() && baseDirectory.StartsWith("/var/", StringComparison.Ordinal))
      baseDirectory = "/private" + baseDirectory;
    temporary = Path.Combine(baseDirectory, $"urd-catalog-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Project);
  }

  [Fact]
  public async Task WarmStartsWithoutWaitingAndConsumersReuseTheScan() {
    string path = Write("AGENTS.md");
    var entered = Signal();
    var release = Signal();
    int scans = 0;
    var watch = new ManualWatch();
    await using var catalog = new InstructionResourceCatalog(Database, async (directory, _, cancellation) => {
      Interlocked.Increment(ref scans);
      entered.TrySetResult();
      await release.Task.WaitAsync(cancellation);
      return [path];
    }, watch.Open);

    catalog.Warm(Project);
    await entered.Task.WaitAsync(deadline);
    catalog.Warm(Project);
    Task<InstructionResourceCatalogResult> result = catalog.FindPathsAsync(Project, CancellationToken.None);
    Assert.False(result.IsCompleted);
    release.SetResult();
    Assert.Equal([path], (await result.WaitAsync(deadline)).Paths);
    catalog.Warm(Project);
    Assert.Equal(1, scans);
  }

  [Fact]
  public async Task CoveredCwdsShareWorkAndKeepSiblingPathsOut() {
    string child = Write("area/.hidden/AGENTS.md");
    string sibling = Write("area-other/.hidden/AGENTS.md");
    string parent = Write("AGENTS.md");
    string area = Path.Combine(Project, "area");
    var entered = Signal();
    var release = Signal();
    int scans = 0;
    var watch = new ManualWatch();
    await using var catalog = new InstructionResourceCatalog(Database, async (directory, _, cancellation) => {
      Interlocked.Increment(ref scans);
      if (directory == Project) {
        entered.TrySetResult();
        await release.Task.WaitAsync(cancellation);
      }
      return Fresh(directory);
    }, watch.Open);

    using var cancelWaiter = new CancellationTokenSource();
    Task<InstructionResourceCatalogResult> canceled = catalog.FindPathsAsync(Project, cancelWaiter.Token);
    await entered.Task.WaitAsync(deadline);
    Task<InstructionResourceCatalogResult> broad = catalog.FindPathsAsync(Project, CancellationToken.None);
    Task<InstructionResourceCatalogResult> narrow = catalog.FindPathsAsync(area, CancellationToken.None);
    await cancelWaiter.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
    Assert.False(broad.IsCompleted);
    Assert.Equal([child], (await narrow.WaitAsync(deadline)).Paths);
    release.SetResult();

    Assert.Equal(new[] { child, sibling, parent }.Order(StringComparer.OrdinalIgnoreCase),
        (await broad.WaitAsync(deadline)).Paths);
    Assert.Equal([child], (await catalog.FindPathsAsync(area + Path.DirectorySeparatorChar, CancellationToken.None)).Paths);
    Assert.Equal(2, scans);
  }

  [Fact]
  public async Task BroadSnapshotReconcilesChangesAfterNarrowRootPublishes() {
    string stale = Write("area/AGENTS.md");
    string area = Path.GetDirectoryName(stale)!;
    var entered = Signal();
    var release = Signal();
    Action<string, string?>? broadChanged = null;
    await using var catalog = new InstructionResourceCatalog(Database, async (directory, _, cancellation) => {
      string[] snapshot = Fresh(directory);
      if (directory == Project) {
        entered.SetResult();
        await release.Task.WaitAsync(cancellation);
      }
      return snapshot;
    }, (directory, changed, lost) => {
      if (directory == Project)
        broadChanged = changed;
      return new ManualWatch();
    });

    var broad = catalog.FindPathsAsync(Project, CancellationToken.None);
    await entered.Task.WaitAsync(deadline);
    File.Delete(stale);
    string replacement = Write("area/SKILL.md");
    broadChanged!(stale, null);
    broadChanged(replacement, null);
    Assert.Equal([replacement],
        (await catalog.FindPathsAsync(area, CancellationToken.None).WaitAsync(deadline)).Paths);
    release.SetResult();
    Assert.Equal([replacement], (await broad.WaitAsync(deadline)).Paths);
    Assert.Equal([replacement],
        (await catalog.FindPathsAsync(area, CancellationToken.None).WaitAsync(deadline)).Paths);
  }

  [Fact]
  public async Task BroaderRootSupersedesNarrowWorkWithoutPublishingStalePaths() {
    string child = Write("area/AGENTS.md");
    string area = Path.GetDirectoryName(child)!;
    var narrowStarted = Signal();
    var narrowStopped = Signal();
    var watch = new ManualWatch();
    await using var catalog = new InstructionResourceCatalog(Database, async (directory, _, cancellation) => {
      if (directory == area) {
        narrowStarted.TrySetResult();
        try {
          await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        } finally {
          narrowStopped.TrySetResult();
        }
      }
      return Fresh(directory);
    }, watch.Open);

    var narrow = catalog.FindPathsAsync(area, CancellationToken.None);
    await narrowStarted.Task.WaitAsync(deadline);
    File.Delete(child);
    string replacement = Write("area/SKILL.md");
    Assert.Equal([replacement],
        (await catalog.FindPathsAsync(Project, CancellationToken.None).WaitAsync(deadline)).Paths);
    Assert.Equal([replacement], (await narrow.WaitAsync(deadline)).Paths);
    await narrowStopped.Task.WaitAsync(deadline);
  }

  [Fact]
  public async Task ColdScansServePartialMatchesAndCompleteLater() {
    string first = Write("AGENTS.md");
    string second = Write("SKILL.md");
    var release = Signal();
    var watch = new ManualWatch();
    await using var catalog = new InstructionResourceCatalog(Database, async (directory, matched, cancellation) => {
      string[] paths = Fresh(directory);
      matched(paths[0]);
      if (directory == Project)
        await release.Task.WaitAsync(cancellation);
      foreach (string path in paths.Skip(1))
        matched(path);
      return paths;
    }, watch.Open);

    InstructionResourceCatalogResult partial = await catalog
        .FindPathsAsync(Project, CancellationToken.None).WaitAsync(deadline);
    Assert.Equal([first], partial.Paths);
    Assert.Equal(InstructionResourceIndexPhase.Partial, partial.State.Phase);
    Assert.Equal(1, partial.State.MatchedFiles);

    release.SetResult();
    InstructionResourceCatalogResult complete = await MatchesIndexAsync(catalog, [first, second]);
    Assert.Equal(InstructionResourceIndexPhase.Complete, complete.State.Phase);
    Assert.Equal(2, complete.State.MatchedFiles);
  }

  [Fact]
  public async Task NativeNotificationsTrackFilesAndPopulatedDirectoriesWithoutFullScans() {
    string initial = Write("AGENTS.md");
    string staging = Path.Combine(temporary, "staging");
    string moved = Path.Combine(Project, "moved");
    WriteAt(staging, "recipes/SKILL.md");
    var scans = new System.Collections.Concurrent.ConcurrentQueue<string>();
    await using var catalog = new InstructionResourceCatalog(Database, (directory, _, _) => {
      scans.Enqueue(directory);
      return Task.FromResult(Fresh(directory));
    });

    Assert.Equal([initial], (await catalog.FindPathsAsync(Project, CancellationToken.None).WaitAsync(deadline)).Paths);
    string added = Write("APPEND_SYSTEM.md");
    await MatchesDiskAsync(catalog);
    string renamed = Path.Combine(Project, "AGENT-INSTRUCTIONS.md");
    File.Move(added, renamed);
    await MatchesDiskAsync(catalog);
    File.Delete(initial);
    await MatchesDiskAsync(catalog);
    Directory.Move(staging, moved);
    await MatchesDiskAsync(catalog);
    string newNested = Write("moved/recipes/AGENTS.md");
    await MatchesDiskAsync(catalog);
    File.Delete(newNested);
    Directory.Move(moved, staging);
    await MatchesDiskAsync(catalog);

    Assert.Equal(1, scans.Count(directory => directory == Project));
    Assert.Contains(moved, scans);
    Assert.All(scans, directory => Assert.True(directory == Project || directory.StartsWith(moved, StringComparison.Ordinal)));
  }

  [Fact]
  public async Task EventsDuringScanReconcileAndLostCoverageServesLastKnownRows() {
    string stale = Write("AGENTS.md");
    var entered = Signal();
    var release = Signal();
    int scans = 0;
    var watch = new ManualWatch();
    await using var catalog = new InstructionResourceCatalog(Database, async (directory, _, cancellation) => {
      string[] snapshot = Fresh(directory);
      if (Interlocked.Increment(ref scans) == 1) {
        entered.SetResult();
        await release.Task.WaitAsync(cancellation);
      }
      return snapshot;
    }, watch.Open);

    var initial = catalog.FindPathsAsync(Project, CancellationToken.None);
    await entered.Task.WaitAsync(deadline);
    Assert.False(File.Exists(Database));
    File.Delete(stale);
    string newPath = Write("SKILL.md");
    watch.Changed(stale);
    watch.Changed(newPath);
    release.SetResult();
    Assert.Equal([newPath], (await initial.WaitAsync(deadline)).Paths);
    Assert.Equal(1, scans);

    File.Delete(newPath);
    string missed = Write("APPEND-SYSTEM.md");
    watch.Lost();
    InstructionResourceCatalogResult lastKnown = await catalog
        .FindPathsAsync(Project, CancellationToken.None).WaitAsync(deadline);
    Assert.Equal([newPath], lastKnown.Paths);
    Assert.Equal(InstructionResourceIndexPhase.Refreshing, lastKnown.State.Phase);

    InstructionResourceCatalogResult reconciled = await MatchesIndexAsync(catalog, [missed]);
    Assert.Equal(InstructionResourceIndexPhase.Complete, reconciled.State.Phase);
    Assert.Equal(2, scans);
  }

  [Fact]
  public async Task NotificationFloodRecoversOnceAndUnavailableWatcherReconcilesOnLaterRequests() {
    var entered = Signal();
    var release = Signal();
    int scans = 0;
    var watch = new ManualWatch();
    string path = Write("SKILL.md");
    await using (var catalog = new InstructionResourceCatalog(Database, async (directory, _, cancellation) => {
      string[] snapshot = Fresh(directory);
      if (Interlocked.Increment(ref scans) == 1) {
        entered.SetResult();
        await release.Task.WaitAsync(cancellation);
      }
      return snapshot;
    }, watch.Open)) {
      var initial = catalog.FindPathsAsync(Project, CancellationToken.None);
      await entered.Task.WaitAsync(deadline);
      for (int i = 0; i < 5000; i++)
        watch.Changed(Path.Combine(Project, $"change-{i}"));
      release.SetResult();
      Assert.Equal([path], (await initial.WaitAsync(deadline)).Paths);
      Assert.Equal(2, scans);
    }

    var fallbackEntered = Signal();
    var fallbackRelease = Signal();
    var nextReconcile = Signal();
    int fallbackScans = 0;
    await using var fallback = new InstructionResourceCatalog(Database, async (directory, _, cancellation) => {
      int attempt = Interlocked.Increment(ref fallbackScans);
      if (attempt == 1) {
        fallbackEntered.SetResult();
        await fallbackRelease.Task.WaitAsync(cancellation);
      } else if (attempt == 2) {
        nextReconcile.SetResult();
      }
      return Fresh(directory);
    }, (_, _, _) => null);
    string added = Write("APPEND_SYSTEM.md");

    InstructionResourceCatalogResult stale = await fallback.FindPathsAsync(Project, CancellationToken.None)
        .WaitAsync(deadline);
    Assert.Equal([path], stale.Paths);
    Assert.Equal(InstructionResourceIndexPhase.Refreshing, stale.State.Phase);
    await fallbackEntered.Task.WaitAsync(deadline);
    InstructionResourceCatalogResult shared = await fallback.FindPathsAsync(Project, CancellationToken.None)
        .WaitAsync(deadline);
    Assert.Equal([path], shared.Paths);
    Assert.Equal(InstructionResourceIndexPhase.Refreshing, shared.State.Phase);
    Assert.Equal(1, fallbackScans);

    fallbackRelease.SetResult();
    InstructionResourceCatalogResult reconciled = await MatchesIndexAsync(fallback, Fresh(Project));
    Assert.Equal(InstructionResourceIndexPhase.Refreshing, reconciled.State.Phase);
    await nextReconcile.Task.WaitAsync(deadline);
    Assert.Equal(2, fallbackScans);
    Assert.Contains(added, reconciled.Paths);
  }

  [Fact]
  public async Task IrrelevantNotificationsDoNotOverflowCoverageAndMissingTrackedDirectoriesAreRemoved() {
    string tracked = Write("area/AGENTS.md");
    string area = Path.GetDirectoryName(tracked)!;
    int scans = 0;
    var watch = new ManualWatch();
    await using var catalog = new InstructionResourceCatalog(Database, (directory, _, _) => {
      Interlocked.Increment(ref scans);
      return Task.FromResult(Fresh(directory));
    }, watch.Open);
    Assert.Equal([tracked], (await catalog.FindPathsAsync(Project, CancellationToken.None).WaitAsync(deadline)).Paths);

    for (int i = 0; i < 5000; i++)
      watch.Changed(Path.Combine(Project, $"unrelated-{i}.tmp"));
    Assert.Equal([tracked], (await MatchesIndexAsync(catalog, [tracked])).Paths);
    Assert.Equal(1, scans);

    Directory.Delete(area, recursive: true);
    watch.Changed(area);
    Assert.Empty((await MatchesIndexAsync(catalog, [])).Paths);
    string added = Write("new/sub/SKILL.md");
    watch.Changed(Path.Combine(Project, "new"));
    Assert.Equal([added], (await MatchesIndexAsync(catalog, [added])).Paths);
  }

  [Fact]
  public async Task JunkDirectoryNotificationsDoNotUpdateOrOverflowCoverage() {
    string kept = Write("AGENTS.md");
    Write("node_modules/dependency/SKILL.md");
    Write("obj/AGENTS.md");
    int scans = 0;
    var watch = new ManualWatch();
    await using var catalog = new InstructionResourceCatalog(Database, (directory, _, cancellation) => {
      Interlocked.Increment(ref scans);
      return new FdResourceScanner().FindPathsAsync(directory, cancellation);
    }, watch.Open);

    Assert.Equal([kept], (await catalog.FindPathsAsync(Project, CancellationToken.None).WaitAsync(deadline)).Paths);
    Assert.Equal(1, scans);
    // Changes under junk directories are pruned before the pending-event limit.
    for (int i = 0; i < 5000; i++)
      watch.Changed(Path.Combine(Project, "node_modules", $"change-{i}"));
    watch.Changed(Path.Combine(Project, "obj", "edit.tmp"));
    Assert.Equal([kept], (await MatchesIndexAsync(catalog, [kept])).Paths);
    Assert.Equal(1, scans);
  }

  [Fact]
  public async Task RestartServesPersistedRowsAndReconcilesOnlyChangedOnes() {
    string kept = Write("AGENTS.md");
    string removed = Write("SKILL.md");
    Task<string[]> Scan(string directory, Action<string> _, CancellationToken cancellation) =>
        Task.FromResult(Fresh(directory));
    var watch = new ManualWatch();
    await using (var first = new InstructionResourceCatalog(Database, Scan, watch.Open)) {
      InstructionResourceCatalogResult initial = await first.FindPathsAsync(Project, CancellationToken.None)
          .WaitAsync(deadline);
      Assert.Equal([kept, removed], initial.Paths);
      Assert.Equal(InstructionResourceIndexPhase.Complete, initial.State.Phase);
    }

    // A sentinel in the same database records writes to existing rows and detects wholesale replacement.
    var options = new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false };
    using (var connection = new SqliteConnection(options.ToString())) {
      await connection.OpenAsync();
      connection.CreateCollation("path_identity", StringComparer.OrdinalIgnoreCase.Compare);
      using var command = connection.CreateCommand();
      command.CommandText = """
        CREATE TABLE change_log(path TEXT);
        CREATE TRIGGER record_delete AFTER DELETE ON resource_paths BEGIN INSERT INTO change_log VALUES (old.path); END;
        CREATE TRIGGER record_insert AFTER INSERT ON resource_paths BEGIN INSERT INTO change_log VALUES (new.path); END;
        """;
      await command.ExecuteNonQueryAsync();
    }
    File.Delete(removed);
    string added = Write("APPEND_SYSTEM.md");
    await using (var second = new InstructionResourceCatalog(Database, Scan, watch.Open)) {
      // Persisted rows are served immediately after the restart while the reconcile is running.
      InstructionResourceCatalogResult served = await second.FindPathsAsync(Project, CancellationToken.None)
          .WaitAsync(deadline);
      Assert.Equal([kept, removed], served.Paths);
      Assert.Equal(InstructionResourceIndexPhase.Refreshing, served.State.Phase);

      InstructionResourceCatalogResult reconciled = await MatchesIndexAsync(second, [kept, added]);
      Assert.Equal(InstructionResourceIndexPhase.Complete, reconciled.State.Phase);
    }
    using var reopened = new SqliteConnection(options.ToString());
    await reopened.OpenAsync();
    using var read = reopened.CreateCommand();
    read.CommandText = "SELECT path FROM change_log ORDER BY rowid";
    using var rows = await read.ExecuteReaderAsync();
    Assert.True(await rows.ReadAsync());
    Assert.Equal(removed, rows.GetString(0));
    Assert.True(await rows.ReadAsync());
    Assert.Equal(added, rows.GetString(0));
    Assert.False(await rows.ReadAsync());
  }

  [Fact]
  public async Task UnwatchedParentDoesNotPreventNestedCoverage() {
    string child = Write("area/AGENTS.md");
    string area = Path.GetDirectoryName(child)!;
    var scans = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var watch = new ManualWatch();
    await using var catalog = new InstructionResourceCatalog(Database, (directory, _, _) => {
      scans.Enqueue(directory);
      return Task.FromResult(Fresh(directory));
    }, (directory, changed, lost) => directory == Project ? null : watch.Open(directory, changed, lost));

    Assert.Equal([child], (await catalog.FindPathsAsync(Project, CancellationToken.None).WaitAsync(deadline)).Paths);
    Assert.Equal([child], (await catalog.FindPathsAsync(area, CancellationToken.None).WaitAsync(deadline)).Paths);
    Assert.Equal([child], (await SettledAsync(catalog, area)).Paths);
    Assert.Equal([Project, area], scans.ToList());
  }

  [Fact]
  public async Task RebuildsOwnedIncompatibleSchemaButDoesNotEraseForeignFiles() {
    string resource = Write("AGENTS.md");
    Task<string[]> Scan(string directory, Action<string> _, CancellationToken cancellation) =>
        Task.FromResult(Fresh(directory));
    var watch = new ManualWatch();
    await File.WriteAllTextAsync(Database, "Not an index.");
    await using (var foreign = new InstructionResourceCatalog(Database, Scan, watch.Open))
      await Assert.ThrowsAsync<InvalidOperationException>(() => foreign.FindPathsAsync(Project, CancellationToken.None));
    Assert.Equal("Not an index.", await File.ReadAllTextAsync(Database));
    File.Delete(Database);

    await using (var first = new InstructionResourceCatalog(Database, Scan, watch.Open)) {
      InstructionResourceCatalogResult initial = await first.FindPathsAsync(Project, CancellationToken.None)
          .WaitAsync(deadline);
      Assert.Equal([resource], initial.Paths);
      Assert.Equal(InstructionResourceIndexPhase.Complete, initial.State.Phase);
    }
    var options = new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false };
    using (var connection = new SqliteConnection(options.ToString())) {
      await connection.OpenAsync();
      using var command = connection.CreateCommand();
      command.CommandText = "PRAGMA user_version = 200; DROP TABLE resource_paths;";
      await command.ExecuteNonQueryAsync();
    }
    await using var reopened = new InstructionResourceCatalog(Database, Scan, watch.Open);
    Assert.Equal([resource], (await reopened.FindPathsAsync(Project, CancellationToken.None).WaitAsync(deadline)).Paths);
  }

  [Fact]
  public async Task NotificationNeverScansThroughADirectoryLink() {
    string outside = Path.Combine(temporary, "outside");
    WriteAt(outside, "sub/AGENTS.md");
    string link = Path.Combine(Project, "link");
    int scans = 0;
    var watch = new ManualWatch();
    await using var catalog = new InstructionResourceCatalog(Database, (directory, _, _) => {
      Interlocked.Increment(ref scans);
      return Task.FromResult(Fresh(directory));
    }, watch.Open);
    Assert.Empty((await catalog.FindPathsAsync(Project, CancellationToken.None).WaitAsync(deadline)).Paths);
    try {
      Directory.CreateSymbolicLink(link, outside);
    } catch (IOException) when (OperatingSystem.IsWindows()) {
      return; // Native symlink privilege is optional on Windows.
    }
    watch.Changed(link);
    watch.Changed(Path.Combine(link, "sub", "AGENTS.md"));
    Assert.Empty((await MatchesIndexAsync(catalog, [])).Paths);
    Assert.Empty((await catalog.FindPathsAsync(Path.Combine(link, "sub"), CancellationToken.None)).Paths);
    Assert.Equal(1, scans);
  }

  [Fact]
  public async Task DisposalJoinsTheScanAndClosesTheWatcher() {
    var entered = Signal();
    var canceled = Signal();
    var release = Signal();
    var watch = new ManualWatch();
    await using var catalog = new InstructionResourceCatalog(Database, async (_, _, cancellation) => {
      entered.SetResult();
      try {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
      } finally {
        canceled.SetResult();
        await release.Task;
      }
      return [];
    }, watch.Open);

    var waiting = catalog.FindPathsAsync(Project, CancellationToken.None);
    await entered.Task.WaitAsync(deadline);
    Task dispose = catalog.DisposeAsync().AsTask();
    await canceled.Task.WaitAsync(deadline);
    Assert.False(dispose.IsCompleted);
    release.SetResult();
    await dispose.WaitAsync(deadline);
    await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
    Assert.Equal(1, watch.Disposed);
    Assert.False(File.Exists(Database));
  }

  private async Task<InstructionResourceCatalogResult> MatchesDiskAsync(InstructionResourceCatalog catalog) =>
      await MatchesIndexAsync(catalog, Fresh(Project));

  // A background reconcile may still be running when a request returns; a settled answer is
  // observed by waiting for its Complete phase with a bounded deadline.
  private static async Task<InstructionResourceCatalogResult> SettledAsync(
      InstructionResourceCatalog catalog, string directory) {
    using var cancellation = new CancellationTokenSource(deadline);
    while (true) {
      InstructionResourceCatalogResult result = await catalog.FindPathsAsync(directory, cancellation.Token);
      if (result.State.Phase == InstructionResourceIndexPhase.Complete)
        return result;
      await Task.Delay(25, cancellation.Token);
    }
  }

  // Stale rows are served while reconciliation runs in the background, so a settled answer
  // is observed by waiting for it with a bounded deadline.
  private async Task<InstructionResourceCatalogResult> MatchesIndexAsync(
      InstructionResourceCatalog catalog, string[] expected) {
    using var cancellation = new CancellationTokenSource(deadline);
    while (true) {
      InstructionResourceCatalogResult result = await catalog.FindPathsAsync(Project, cancellation.Token);
      if (expected.SequenceEqual(result.Paths))
        return result;
      await Task.Delay(25, cancellation.Token);
    }
  }

  private static string[] Fresh(string directory) => Directory.EnumerateFiles(directory, "*", new EnumerationOptions {
    RecurseSubdirectories = true,
    AttributesToSkip = FileAttributes.ReparsePoint,
  }).Where(FdResourceScanner.IsResourcePath).Order(StringComparer.OrdinalIgnoreCase).ToArray();

  private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

  private string Write(string relativePath) => WriteAt(Project, relativePath);

  private static string WriteAt(string directory, string relativePath) {
    string path = Path.GetFullPath(Path.Combine(directory, relativePath));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "Test instructions.");
    return path;
  }

  public void Dispose() => Directory.Delete(temporary, recursive: true);

  private sealed class ManualWatch : IDisposable {
    private Action<string, string?>? changed;
    private Action? lost;
    private int disposed;
    internal int Disposed => Volatile.Read(ref disposed);

    internal ManualWatch Open(string directory, Action<string, string?> changed, Action lost) {
      Assert.True(Path.IsPathFullyQualified(directory));
      this.changed = changed;
      this.lost = lost;
      return this;
    }

    internal void Changed(string path) => changed!(path, null);
    internal void Lost() => lost!();
    public void Dispose() => Interlocked.Increment(ref disposed);
  }
}
