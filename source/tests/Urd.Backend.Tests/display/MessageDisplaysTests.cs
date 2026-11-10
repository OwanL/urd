using System.Runtime.CompilerServices;
using System.Text;
using Urd.Core.Display;
using Urd.Core.Sessions;
using Xunit;

namespace Urd.Backend.Tests.Display;

public sealed class MessageDisplaysTests {
  [Fact]
  public async Task PublishesIndependentPinsForObserversAndCacheHitsBeforeAnyAwait() {
    await using var worker = new BlockedBuild();
    var displays = worker.Displays;
    var key = CreateKey();
    int captures = 0;
    Task<MessageDisplaySource> CaptureOnce(CancellationToken _) {
      Interlocked.Increment(ref captures);
      return Task.FromResult(CreateSource(key, "shared"));
    }
    var first = displays.RequestAsync(key, CaptureOnce);
    var thread = await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var duplicate = displays.RequestAsync(key, CaptureOnce);
    worker.Release.Set();
    // Completion itself must own the pin, even while its consumer hasn't resumed.
    await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(10));
    using var hit = await displays.RequestAsync(key, _ => throw new InvalidOperationException("cache miss"));
    displays.Invalidate(key.SessionId);
    using var second = await duplicate;
    var original = await first;
    Assert.NotSame(original, second);
    Assert.NotSame(second, hit);
    Assert.Equal(original.Generation, second.Generation);
    Assert.Equal(original.Generation, hit.Generation);
    Assert.Equal(1, captures);
    Assert.Equal(1, worker.Builds);
    Assert.Equal("Message displays", thread.Name);
    Assert.Equal(ThreadPriority.BelowNormal, thread.Priority);
    long pinnedBytes = displays.Usage.RetainedBytes;
    original.Dispose();
    original.Dispose();
    Assert.Equal(pinnedBytes - MessageDisplayCache.LeaseBytes, displays.Usage.RetainedBytes);
    Assert.Equal("shared", DisplayText(displays, second));
    second.Dispose();
    Assert.Equal("shared", DisplayText(displays, hit));
    hit.Dispose();
    Assert.Equal((0, 0L, 0), displays.Usage);
  }

  [Theory]
  [InlineData(false, 0)]
  [InlineData(true, 0)]
  [InlineData(false, 1)]
  [InlineData(true, 1)]
  [InlineData(false, 2)]
  [InlineData(true, 2)]
  public async Task BoundsQueueCoalescesProgressAndPublishesRunningExtent(bool reverse, int progress) {
    await using var worker = new BlockedBuild(pendingKeys: 1);
    var displays = worker.Displays;
    var key = CreateKey() with { IsClosed = false };
    var older = key with { ThroughSegment = 2, IsClosed = progress == 2 };
    var newer = older with {
      ThroughSegment = progress == 0 ? 3 : 2,
      IsClosed = progress != 0,
      FinalOutputCommitted = progress == 2
    };
    var running = Ready(displays, key, "running");
    await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    int obsoleteCaptures = 0;
    Task<MessageDisplayLease> Older() => displays.RequestAsync(older, _ => {
      obsoleteCaptures++;
      return Task.FromResult(CreateSource(older, "obsolete"));
    });
    Task<MessageDisplayLease> Newer() => Ready(displays, newer, "newest");
    var first = reverse ? Newer() : Older();
    var second = reverse ? Older() : Newer();
    var duplicate = Newer();
    await Assert.ThrowsAsync<DisplaySupersededException>(() => reverse ? second : first);
    Assert.Equal(1, displays.Usage.PendingKeys);
    await Assert.ThrowsAsync<DisplayQueueFullException>(() => Ready(displays, CreateKey(), "full"));
    worker.Release.Set();
    using var completed = await running;
    using var newest = await (reverse ? first : second);
    using var same = await duplicate;
    Assert.Equal("running", DisplayText(displays, completed));
    Assert.Equal("newest", DisplayText(displays, newest));
    Assert.NotSame(newest, same);
    Assert.Equal(newest.Generation, same.Generation);
    Assert.Equal(0, obsoleteCaptures);
    Assert.Equal(2, worker.Builds);
    Assert.Same(worker.Threads[0], worker.Threads[1]);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task DifferentRevisionsAndStartsRemainIndependent(bool revision) {
    await using var worker = new BlockedBuild();
    var displays = worker.Displays;
    var key = CreateKey();
    var independent = revision ? key with { Message = key.Message with { HistoryRevision = 1 } }
        : key with { FromSegment = 1, ThroughSegment = 2 };
    var running = Ready(displays, CreateKey(), "blocker");
    await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var first = Ready(displays, key, "first");
    var second = Ready(displays, independent, "independent");
    Assert.Equal(2, displays.Usage.PendingKeys);
    await Assert.ThrowsAsync<ArgumentException>(() => Ready(displays, key with { Pipeline = "other" }, "bad"));
    worker.Release.Set();
    using var blocker = await running;
    using var one = await first;
    using var two = await second;
    Assert.Equal("first", DisplayText(displays, one));
    Assert.Equal("independent", DisplayText(displays, two));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task EvictsOnlyUnpinnedLeastRecentlyUsedGenerations(bool byBytes) {
    var key = CreateKey();
    var source = CreateSource(key, "same");
    long bytes = AccountedBytes(key, source, Build(source));
    await using var displays = CreateDisplays(entries: byBytes ? 8 : 2,
        retainedBytes: byBytes ? bytes * 2 + MessageDisplayCache.LeaseBytes * 2 : 1_000_000);
    using var first = await Ready(displays, key, "same");
    var secondKey = CreateKey();
    var second = await Ready(displays, secondKey, "same");
    var staleCursor = new DisplayCursor(second.Generation, 0);
    second.Dispose();
    using var third = await Ready(displays, CreateKey(), "same");
    Assert.Equal(2, displays.Usage.Entries);
    Assert.Equal(bytes * 2 + MessageDisplayCache.LeaseBytes * 2, displays.Usage.RetainedBytes);
    Assert.Equal("same", DisplayText(displays, first));
    Assert.Equal("same", DisplayText(displays, third));
    Assert.Throws<DisplayUnavailableException>(() => DisplayText(displays, second));
    first.Dispose();
    third.Dispose();
    // Touch first through a fresh cache lease; the unpinned third is now LRU.
    using (var touched = await Ready(displays, key, "must not be captured"))
      Assert.Equal(first.Generation, touched.Generation);
    using var rebuilt = await Ready(displays, secondKey, "same");
    using var recent = await Ready(displays, key, "same");
    Assert.Equal(first.Generation, recent.Generation);
    Assert.NotEqual(second.Generation, rebuilt.Generation);
    Assert.Throws<ArgumentException>(() => displays.Read(rebuilt, staleCursor));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task FullPinnedCachePreservesOldViewAndAllowsRetryAfterRelease(bool byBytes) {
    var key = CreateKey();
    var source = CreateSource(key, "same");
    long bytes = AccountedBytes(key, source, Build(source));
    await using var displays = CreateDisplays(entries: byBytes ? 8 : 1,
        retainedBytes: byBytes ? bytes * 2 - 1 : 1_000_000);
    var old = await Ready(displays, key, "same");
    long retained = displays.Usage.RetainedBytes;
    var next = key with { ThroughSegment = 2 };
    await Assert.ThrowsAsync<DisplayCacheFullException>(() => Ready(displays, next, "same"));
    Assert.Equal(retained, displays.Usage.RetainedBytes);
    Assert.Equal("same", DisplayText(displays, old));
    displays.Invalidate(key.SessionId);
    await Assert.ThrowsAsync<DisplayCacheFullException>(() => Ready(displays, next, "same"));
    Assert.Equal(retained, displays.Usage.RetainedBytes);
    Assert.Equal("same", DisplayText(displays, old));
    Assert.Equal(new(key.Message, 0, 2), displays.SourceCursor(old, 2));
    Assert.Equal(new(key.Message, 1, 0), displays.SourceCursor(old, old.SourceUtf8Bytes));
    old.Dispose();
    Assert.Equal((0, 0L, 0), displays.Usage);
    using var fresh = await Ready(displays, next, "same");
    Assert.Equal("same", DisplayText(displays, fresh));
  }

  [Fact]
  public async Task StaleLeaseKeepsContextAndSourceMappingAcrossInvalidationAndNewGeneration() {
    await using var displays = CreateDisplays();
    var key = CreateKey(2);
    const string text = "**café** tail";
    var source = CreateSource(key, text) with { Segments = [new(0, 0), new(9, 1)] };
    using var old = await displays.RequestAsync(key, _ => Task.FromResult(source));
    var anchor = displays.Locate(old, 3);
    var page = displays.Read(old, new(old.Generation, 0));
    displays.Invalidate(key.SessionId);
    using var fresh = await Ready(displays, key, "replacement");
    Assert.Equal(2, displays.Usage.Entries);
    Assert.NotEqual(old.Generation, fresh.Generation);
    Assert.Equal(anchor, displays.Locate(old, 3));
    Assert.Equal("café tail", DisplayText(displays, old));
    var strong = Assert.Single(page.Pieces, piece => piece.Text == "café");
    Assert.Equal(DisplayKind.Strong, displays.ReadContext(old, strong.Context, new(old.Generation, 0)).Context.Kind);
    Assert.Equal(new(key.Message, 1, 2), displays.SourceCursor(old, 11));
    Assert.Throws<ArgumentOutOfRangeException>(() => displays.SourceCursor(old, -1));
    Assert.Throws<ArgumentException>(() => displays.Read(fresh, anchor));
    long retained = displays.Usage.RetainedBytes;
    old.Dispose();
    Assert.Equal(1, displays.Usage.Entries);
    Assert.True(displays.Usage.RetainedBytes < retained);
    Assert.Equal("replacement", DisplayText(displays, fresh));
    await using var other = CreateDisplays();
    Assert.Throws<DisplayUnavailableException>(() => DisplayText(other, fresh));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CancellationReleasesOneInterestBeforePublicationButNotDeliveredLeases(bool queued) {
    await using var worker = new BlockedBuild();
    var displays = worker.Displays;
    var key = CreateKey();
    var blocker = queued ? Ready(displays, CreateKey(), "blocker") : null;
    if (queued) await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    using var cancellation = new CancellationTokenSource();
    var cancelled = displays.RequestAsync(key, _ => Task.FromResult(CreateSource(key, "shared")), cancellation.Token);
    if (!queued) await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    using var deliveredCancellation = new CancellationTokenSource();
    var interested = displays.RequestAsync(key, _ => throw new InvalidOperationException("duplicate capture"), deliveredCancellation.Token);
    long before = displays.Usage.RetainedBytes;
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    Assert.Equal(before - MessageDisplayCache.LeaseBytes, displays.Usage.RetainedBytes);
    worker.Release.Set();
    using var ready = await interested;
    using var blockingLease = blocker is null ? null : await blocker;
    await deliveredCancellation.CancelAsync();
    Assert.Equal("shared", DisplayText(displays, ready));
    Assert.Equal(queued ? 2 : 1, worker.Builds);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CancellingLastInterestReleasesQueueSlotAndAllowsRetry(bool queued) {
    await using var worker = new BlockedBuild(pendingKeys: 1);
    var displays = worker.Displays;
    var key = CreateKey();
    var blocker = queued ? Ready(displays, CreateKey(), "blocker") : null;
    if (queued) await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    using var cancellation = new CancellationTokenSource();
    var abandoned = displays.RequestAsync(key, _ => Task.FromResult(CreateSource(key, "retry")), cancellation.Token);
    if (!queued) await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
    Assert.Equal(0, displays.Usage.PendingKeys);
    var retry = Ready(displays, key, "retry");
    worker.Release.Set();
    using var ready = await retry;
    using var blockingLease = blocker is null ? null : await blocker;
    Assert.Equal("retry", DisplayText(displays, ready));
    Assert.Equal(queued ? 2 : 1, worker.Builds);
  }

  [Fact]
  public async Task BoundsObserverAndHitLeasesAndRejectsAlreadyCancelledRequests() {
    await using var worker = new BlockedBuild(leases: 2);
    var displays = worker.Displays;
    var key = CreateKey();
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => displays.RequestAsync(key,
        _ => throw new InvalidOperationException("cancelled capture"), cancellation.Token));
    Assert.Equal((0, 0L, 0), displays.Usage);
    var first = Ready(displays, key, "shared");
    await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var second = Ready(displays, key, "shared");
    await Assert.ThrowsAsync<DisplayCacheFullException>(() => Ready(displays, key, "full"));
    worker.Release.Set();
    using var one = await first;
    using var two = await second;
    await Assert.ThrowsAsync<DisplayCacheFullException>(() => Ready(displays, key, "full"));
    displays.Invalidate(key.SessionId);
    await Assert.ThrowsAsync<DisplayCacheFullException>(() => Ready(displays, key, "full"));
    two.Dispose();
    using var hit = await Ready(displays, key, "fresh");
    Assert.NotEqual(one.Generation, hit.Generation);
    Assert.Equal("shared", DisplayText(displays, one));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  [InlineData(2)]
  [InlineData(3)]
  [InlineData(4)]
  [InlineData(5)]
  public async Task RejectsMismatchedCapturesBeforeBuild(int mismatch) {
    var key = CreateKey();
    var source = CreateSource(key, "wrong");
    var wrong = mismatch switch {
      0 => source with { SessionId = Guid.NewGuid() },
      1 => source with { Message = key.Message with { HistoryRevision = 1 } },
      2 => source with { IsClosed = !key.IsClosed },
      3 => source with { FinalOutputCommitted = !key.FinalOutputCommitted },
      4 => source with { From = source.From with { Message = key.Message with { MessageId = Guid.NewGuid() } } },
      _ => source with { AvailableThrough = source.AvailableThrough with { SegmentIndex = 2 } }
    };
    await using var displays = CreateDisplays(build: _ => throw new InvalidOperationException("unexpected build"));
    await Assert.ThrowsAsync<DisplayUnavailableException>(() => displays.RequestAsync(key, _ => Task.FromResult(wrong)));
    Assert.Equal((0, 0L, 0), displays.Usage);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task FailureReleasesInterestsWithoutNegativeRetention(bool captureFailure) {
    var key = CreateKey();
    int builds = 0;
    await using var displays = CreateDisplays(build: source => {
      if (!captureFailure && Interlocked.Increment(ref builds) == 1) throw new IOException("build failure");
      return Build(source);
    });
    await Assert.ThrowsAsync<IOException>(() => displays.RequestAsync(key, _ => captureFailure
        ? Task.FromException<MessageDisplaySource>(new IOException("capture failure"))
        : Task.FromResult(CreateSource(key, "retry"))));
    Assert.Equal((0, 0L, 0), displays.Usage);
    using var retry = await Ready(displays, key, "retry");
    Assert.Equal("retry", DisplayText(displays, retry));
  }

  [Fact]
  public async Task AccountsVariableMetadataAndRejectsOversizeWithoutRetention() {
    var key = CreateKey();
    var source = CreateSource(key, "[label](https://example.test/" + new string('x', 8192) + ")");
    var index = Build(source);
    Assert.True(index.AccountedBytes > 8192 * 2);
    await using var displays = CreateDisplays(retainedBytes: AccountedBytes(key, source, index) + MessageDisplayCache.LeaseBytes - 1);
    await Assert.ThrowsAsync<DisplayTooLargeException>(() => displays.RequestAsync(key, _ => Task.FromResult(source)));
    Assert.Equal((0, 0L, 0), displays.Usage);
    using var retry = await Ready(displays, key, "retry");
    Assert.Equal("retry", DisplayText(displays, retry));
  }

  [Fact]
  public async Task InvalidationFencesRunningAndQueuedWorkButAllowsNewGeneration() {
    await using var worker = new BlockedBuild();
    var displays = worker.Displays;
    var key = CreateKey();
    var running = Ready(displays, key, "invalidated");
    await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var queued = Ready(displays, key with { ThroughSegment = 2 }, "queued");
    displays.Invalidate(key.SessionId);
    await Assert.ThrowsAsync<DisplayUnavailableException>(() => running);
    await Assert.ThrowsAsync<DisplayUnavailableException>(() => queued);
    Assert.Equal((0, 0L, 0), displays.Usage);
    var retry = Ready(displays, key, "fresh");
    worker.Release.Set();
    using var fresh = await retry;
    Assert.Equal("fresh", DisplayText(displays, fresh));
    Assert.Equal(2, worker.Builds);
  }

  [Fact]
  public async Task ShutdownDrainsBuildRejectsInterestsAndKeepsDeliveredViewsUntilRelease() {
    var cold = CreateDisplays();
    Task coldShutdown = cold.DisposeAsync().AsTask();
    await cold.DisposeAsync();
    await coldShutdown.WaitAsync(TimeSpan.FromSeconds(10));
    await using var worker = new BlockedBuild();
    var displays = worker.Displays;
    var running = Ready(displays, CreateKey(), "shutdown");
    await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var queued = Ready(displays, CreateKey(), "queued");
    Task shutdown = displays.DisposeAsync().AsTask();
    Assert.False(shutdown.IsCompleted);
    await Assert.ThrowsAsync<ObjectDisposedException>(() => Ready(displays, CreateKey(), "denied"));
    await Assert.ThrowsAsync<ObjectDisposedException>(() => running);
    await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
    Assert.Equal((0, 0L, 0), displays.Usage);
    worker.Release.Set();
    await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
    await displays.DisposeAsync();
    await using var completed = CreateDisplays();
    var lease = await Ready(completed, CreateKey(), "retained");
    long bytes = completed.Usage.RetainedBytes;
    await completed.DisposeAsync();
    Assert.Equal(bytes, completed.Usage.RetainedBytes);
    Assert.Equal("retained", DisplayText(completed, lease));
    lease.Dispose();
    lease.Dispose();
    Assert.Equal((0, 0L, 0), completed.Usage);
  }

  [Fact]
  public async Task ShutdownJoinsCaptureCancellationCallbacksAndReportsFailure() {
    var displays = CreateDisplays();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var release = new ManualResetEventSlim();
    var request = displays.RequestAsync(CreateKey(), async token => {
      using var registration = token.Register(() => {
        cancelling.SetResult();
        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cancellation was not released.");
        throw new IOException("controlled cancellation failure");
      });
      entered.SetResult();
      await Task.Delay(Timeout.Infinite, token);
      throw new InvalidOperationException("unreachable capture");
    });
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
#pragma warning disable S5034 // Each DisposeAsync returns a fresh ValueTask over the shared shutdown Task.
    Task shutdown = displays.DisposeAsync().AsTask();
#pragma warning restore S5034
    try {
      await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(10));
      await Assert.ThrowsAsync<ObjectDisposedException>(() => request);
      Assert.False(shutdown.IsCompleted);
    } finally {
      release.Set();
    }
    await Assert.ThrowsAsync<AggregateException>(() => shutdown.WaitAsync(TimeSpan.FromSeconds(10)));
    await Assert.ThrowsAsync<AggregateException>(() => displays.DisposeAsync().AsTask());
  }

  [Fact]
  public async Task CompletionDoesNotRetainCapturedSourceObject() {
    await using var displays = CreateDisplays();
    (Task<MessageDisplayLease> pending, WeakReference sourceReference) = RequestTemporarySource(displays);
    using var ready = await pending;
    using var barrier = await Ready(displays, CreateKey(), "barrier");
    for (int attempt = 0; attempt < 3 && sourceReference.IsAlive; attempt++) {
#pragma warning disable S1215 // Explicitly verifies release of captured source objects.
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
#pragma warning restore S1215
    }
    Assert.False(sourceReference.IsAlive);
    Assert.StartsWith("retained ", DisplayText(displays, ready), StringComparison.Ordinal);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static (Task<MessageDisplayLease> Pending, WeakReference SourceReference) RequestTemporarySource(MessageDisplays displays) {
    var key = CreateKey();
    var source = CreateSource(key, "retained " + new string('x', 4096));
    return (displays.RequestAsync(key, _ => Task.FromResult(source)), new WeakReference(source));
  }

  static MessageDisplays CreateDisplays(int entries = 64, long retainedBytes = 64 * 1024 * 1024,
      int pendingKeys = 32, int leases = 64, Func<MessageDisplaySource, MessageDisplayIndex>? build = null) =>
      new(new(entries, retainedBytes, pendingKeys, leases), build);

  static MessageDisplayKey CreateKey(long throughSegment = 1) => new(
      Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), MessageRole.Assistant, 0), 0, throughSegment, true, false);

  static MessageDisplaySource CreateSource(MessageDisplayKey key, string text) => new(
      key.SessionId, key.Message, new(key.Message, key.FromSegment, 0), new(key.Message, key.ThroughSegment, 0),
      Encoding.UTF8.GetByteCount(text), key.IsClosed, key.FinalOutputCommitted, text, [new(0, key.FromSegment)]);

  static Task<MessageDisplayLease> Ready(MessageDisplays displays, MessageDisplayKey key, string text) =>
      displays.RequestAsync(key, _ => Task.FromResult(CreateSource(key, text)));

  static MessageDisplayIndex Build(MessageDisplaySource source) => MessageDisplayBuilder.Build(source.Source);

  static string DisplayText(MessageDisplays displays, MessageDisplayLease lease) =>
      string.Concat(displays.Read(lease, new(lease.Generation, 0)).Pieces.Select(piece => piece.Text));

  static long AccountedBytes(MessageDisplayKey key, MessageDisplaySource source, MessageDisplayIndex index) =>
      index.AccountedBytes + 512 + 2L * key.Pipeline.Length + 24 + source.Segments.Count * 40L;

  sealed class BlockedBuild : IAsyncDisposable {
    internal MessageDisplays Displays { get; }
    internal ManualResetEventSlim Release { get; } = new();
    internal TaskCompletionSource<Thread> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal List<Thread> Threads { get; } = [];
    internal int Builds => Threads.Count;

    internal BlockedBuild(int pendingKeys = 32, int leases = 64) {
      Displays = CreateDisplays(pendingKeys: pendingKeys, leases: leases, build: source => {
        Threads.Add(Thread.CurrentThread);
        if (Threads.Count == 1) {
          Entered.SetResult(Thread.CurrentThread);
          if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Display worker was not released.");
        }
        return Build(source);
      });
    }

    public async ValueTask DisposeAsync() {
      Release.Set();
      await Displays.DisposeAsync();
      Release.Dispose();
    }
  }
}
