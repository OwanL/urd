using System.Runtime.CompilerServices;
using System.Text;
using Urd.Core.MarkdownRendering;
using Urd.Core.Sessions;
using Xunit;

namespace Urd.Backend.Tests.MarkdownRendering;

public sealed class MessageMarkdownSchedulerTests {
  [Fact]
  public async Task PublishesIndependentPinsForObserversAndCacheHitsBeforeAnyAwait() {
    await using var worker = new BlockedBuild();
    var scheduler = worker.Scheduler;
    var key = CreateKey();
    int captures = 0;
    Task<MessageMarkdownSource> CaptureOnce(CancellationToken _) {
      Interlocked.Increment(ref captures);
      return Task.FromResult(CreateSource(key, "shared"));
    }
    var first = scheduler.RequestAsync(key, CaptureOnce);
    var thread = await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var duplicate = scheduler.RequestAsync(key, CaptureOnce);
    worker.Release.Set();
    // Completion itself must own the pin, even while its consumer hasn't resumed.
    await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(10));
    using var hit = await scheduler.RequestAsync(key, _ => throw new InvalidOperationException("cache miss"));
    scheduler.Invalidate(key.SessionId);
    using var second = await duplicate;
    var original = await first;
    Assert.NotSame(original, second);
    Assert.NotSame(second, hit);
    Assert.Equal(original.Generation, second.Generation);
    Assert.Equal(original.Generation, hit.Generation);
    Assert.Equal(1, captures);
    Assert.Equal(1, worker.Builds);
    Assert.Equal("Message markdown scheduler", thread.Name);
    Assert.Equal(ThreadPriority.BelowNormal, thread.Priority);
    long pinnedBytes = scheduler.Usage.RetainedBytes;
    original.Dispose();
    original.Dispose();
    Assert.Equal(pinnedBytes - MessageMarkdownCache.LeaseBytes, scheduler.Usage.RetainedBytes);
    Assert.Equal("shared", MarkdownText(scheduler, second));
    second.Dispose();
    Assert.Equal("shared", MarkdownText(scheduler, hit));
    hit.Dispose();
    Assert.Equal((0, 0L, 0), scheduler.Usage);
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
    var scheduler = worker.Scheduler;
    var key = CreateKey() with { IsClosed = false };
    var older = key with { ThroughSegment = 2, IsClosed = progress == 2 };
    var newer = older with {
      ThroughSegment = progress == 0 ? 3 : 2,
      IsClosed = progress != 0,
      FinalOutputCommitted = progress == 2
    };
    var running = Ready(scheduler, key, "running");
    await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    int obsoleteCaptures = 0;
    Task<MessageMarkdownLease> Older() => scheduler.RequestAsync(older, _ => {
      obsoleteCaptures++;
      return Task.FromResult(CreateSource(older, "obsolete"));
    });
    Task<MessageMarkdownLease> Newer() => Ready(scheduler, newer, "newest");
    var first = reverse ? Newer() : Older();
    var second = reverse ? Older() : Newer();
    var duplicate = Newer();
    await Assert.ThrowsAsync<MarkdownSupersededException>(() => reverse ? second : first);
    Assert.Equal(1, scheduler.Usage.PendingKeys);
    await Assert.ThrowsAsync<MarkdownQueueFullException>(() => Ready(scheduler, CreateKey(), "full"));
    worker.Release.Set();
    using var completed = await running;
    using var newest = await (reverse ? first : second);
    using var same = await duplicate;
    Assert.Equal("running", MarkdownText(scheduler, completed));
    Assert.Equal("newest", MarkdownText(scheduler, newest));
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
    var scheduler = worker.Scheduler;
    var key = CreateKey();
    var independent = revision ? key with { Message = key.Message with { HistoryRevision = 1 } }
        : key with { FromSegment = 1, ThroughSegment = 2 };
    var running = Ready(scheduler, CreateKey(), "blocker");
    await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var first = Ready(scheduler, key, "first");
    var second = Ready(scheduler, independent, "independent");
    Assert.Equal(2, scheduler.Usage.PendingKeys);
    await Assert.ThrowsAsync<ArgumentException>(() => Ready(scheduler, key with { Pipeline = "other" }, "bad"));
    worker.Release.Set();
    using var blocker = await running;
    using var one = await first;
    using var two = await second;
    Assert.Equal("first", MarkdownText(scheduler, one));
    Assert.Equal("independent", MarkdownText(scheduler, two));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task EvictsOnlyUnpinnedLeastRecentlyUsedGenerations(bool byBytes) {
    var key = CreateKey();
    var source = CreateSource(key, "same");
    long bytes = AccountedBytes(key, source, Build(source));
    await using var scheduler = CreateScheduler(entries: byBytes ? 8 : 2,
        retainedBytes: byBytes ? bytes * 2 + MessageMarkdownCache.LeaseBytes * 2 : 1_000_000);
    using var first = await Ready(scheduler, key, "same");
    var secondKey = CreateKey();
    var second = await Ready(scheduler, secondKey, "same");
    var staleCursor = new MarkdownRenderCursor(second.Generation, 0);
    second.Dispose();
    using var third = await Ready(scheduler, CreateKey(), "same");
    Assert.Equal(2, scheduler.Usage.Entries);
    Assert.Equal(bytes * 2 + MessageMarkdownCache.LeaseBytes * 2, scheduler.Usage.RetainedBytes);
    Assert.Equal("same", MarkdownText(scheduler, first));
    Assert.Equal("same", MarkdownText(scheduler, third));
    Assert.Throws<MarkdownUnavailableException>(() => MarkdownText(scheduler, second));
    first.Dispose();
    third.Dispose();
    // Touch first through a fresh cache lease; the unpinned third is now LRU.
    using (var touched = await Ready(scheduler, key, "must not be captured"))
      Assert.Equal(first.Generation, touched.Generation);
    using var rebuilt = await Ready(scheduler, secondKey, "same");
    using var recent = await Ready(scheduler, key, "same");
    Assert.Equal(first.Generation, recent.Generation);
    Assert.NotEqual(second.Generation, rebuilt.Generation);
    Assert.Throws<ArgumentException>(() => scheduler.Read(rebuilt, staleCursor));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task FullPinnedCachePreservesOldViewAndAllowsRetryAfterRelease(bool byBytes) {
    var key = CreateKey();
    var source = CreateSource(key, "same");
    long bytes = AccountedBytes(key, source, Build(source));
    await using var scheduler = CreateScheduler(entries: byBytes ? 8 : 1,
        retainedBytes: byBytes ? bytes * 2 - 1 : 1_000_000);
    var old = await Ready(scheduler, key, "same");
    long retained = scheduler.Usage.RetainedBytes;
    var next = key with { ThroughSegment = 2 };
    await Assert.ThrowsAsync<MarkdownCacheFullException>(() => Ready(scheduler, next, "same"));
    Assert.Equal(retained, scheduler.Usage.RetainedBytes);
    Assert.Equal("same", MarkdownText(scheduler, old));
    scheduler.Invalidate(key.SessionId);
    await Assert.ThrowsAsync<MarkdownCacheFullException>(() => Ready(scheduler, next, "same"));
    Assert.Equal(retained, scheduler.Usage.RetainedBytes);
    Assert.Equal("same", MarkdownText(scheduler, old));
    Assert.Equal(new(key.Message, 0, 2), scheduler.SourceCursor(old, 2));
    Assert.Equal(new(key.Message, 1, 0), scheduler.SourceCursor(old, old.SourceUtf8Bytes));
    old.Dispose();
    Assert.Equal((0, 0L, 0), scheduler.Usage);
    using var fresh = await Ready(scheduler, next, "same");
    Assert.Equal("same", MarkdownText(scheduler, fresh));
  }

  [Fact]
  public async Task StaleLeaseKeepsContextAndSourceMappingAcrossInvalidationAndNewGeneration() {
    await using var scheduler = CreateScheduler();
    var key = CreateKey(2);
    const string text = "**café** tail";
    var source = CreateSource(key, text) with { Segments = [new(0, 0), new(9, 1)] };
    using var old = await scheduler.RequestAsync(key, _ => Task.FromResult(source));
    var anchor = scheduler.Locate(old, 3);
    var page = scheduler.Read(old, new(old.Generation, 0));
    scheduler.Invalidate(key.SessionId);
    using var fresh = await Ready(scheduler, key, "replacement");
    Assert.Equal(2, scheduler.Usage.Entries);
    Assert.NotEqual(old.Generation, fresh.Generation);
    Assert.Equal(anchor, scheduler.Locate(old, 3));
    Assert.Equal("café tail", MarkdownText(scheduler, old));
    var strong = Assert.Single(page.Pieces, piece => piece.Text == "café");
    Assert.Equal(MarkdownElementKind.Strong, scheduler.ReadContext(old, strong.Context, new(old.Generation, 0)).Context.Kind);
    Assert.Equal(new(key.Message, 1, 2), scheduler.SourceCursor(old, 11));
    Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.SourceCursor(old, -1));
    Assert.Throws<ArgumentException>(() => scheduler.Read(fresh, anchor));
    long retained = scheduler.Usage.RetainedBytes;
    old.Dispose();
    Assert.Equal(1, scheduler.Usage.Entries);
    Assert.True(scheduler.Usage.RetainedBytes < retained);
    Assert.Equal("replacement", MarkdownText(scheduler, fresh));
    await using var other = CreateScheduler();
    Assert.Throws<MarkdownUnavailableException>(() => MarkdownText(other, fresh));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CancellationReleasesOneInterestBeforePublicationButNotDeliveredLeases(bool queued) {
    await using var worker = new BlockedBuild();
    var scheduler = worker.Scheduler;
    var key = CreateKey();
    var blocker = queued ? Ready(scheduler, CreateKey(), "blocker") : null;
    if (queued) await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    using var cancellation = new CancellationTokenSource();
    var cancelled = scheduler.RequestAsync(key, _ => Task.FromResult(CreateSource(key, "shared")), cancellation.Token);
    if (!queued) await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    using var deliveredCancellation = new CancellationTokenSource();
    var interested = scheduler.RequestAsync(key, _ => throw new InvalidOperationException("duplicate capture"), deliveredCancellation.Token);
    long before = scheduler.Usage.RetainedBytes;
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    Assert.Equal(before - MessageMarkdownCache.LeaseBytes, scheduler.Usage.RetainedBytes);
    worker.Release.Set();
    using var ready = await interested;
    using var blockingLease = blocker is null ? null : await blocker;
    await deliveredCancellation.CancelAsync();
    Assert.Equal("shared", MarkdownText(scheduler, ready));
    Assert.Equal(queued ? 2 : 1, worker.Builds);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CancellingLastInterestReleasesQueueSlotAndAllowsRetry(bool queued) {
    await using var worker = new BlockedBuild(pendingKeys: 1);
    var scheduler = worker.Scheduler;
    var key = CreateKey();
    var blocker = queued ? Ready(scheduler, CreateKey(), "blocker") : null;
    if (queued) await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    using var cancellation = new CancellationTokenSource();
    var abandoned = scheduler.RequestAsync(key, _ => Task.FromResult(CreateSource(key, "retry")), cancellation.Token);
    if (!queued) await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
    Assert.Equal(0, scheduler.Usage.PendingKeys);
    var retry = Ready(scheduler, key, "retry");
    worker.Release.Set();
    using var ready = await retry;
    using var blockingLease = blocker is null ? null : await blocker;
    Assert.Equal("retry", MarkdownText(scheduler, ready));
    Assert.Equal(queued ? 2 : 1, worker.Builds);
  }

  [Fact]
  public async Task BoundsObserverAndHitLeasesAndRejectsAlreadyCancelledRequests() {
    await using var worker = new BlockedBuild(leases: 2);
    var scheduler = worker.Scheduler;
    var key = CreateKey();
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RequestAsync(key,
        _ => throw new InvalidOperationException("cancelled capture"), cancellation.Token));
    Assert.Equal((0, 0L, 0), scheduler.Usage);
    var first = Ready(scheduler, key, "shared");
    await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var second = Ready(scheduler, key, "shared");
    await Assert.ThrowsAsync<MarkdownCacheFullException>(() => Ready(scheduler, key, "full"));
    worker.Release.Set();
    using var one = await first;
    using var two = await second;
    await Assert.ThrowsAsync<MarkdownCacheFullException>(() => Ready(scheduler, key, "full"));
    scheduler.Invalidate(key.SessionId);
    await Assert.ThrowsAsync<MarkdownCacheFullException>(() => Ready(scheduler, key, "full"));
    two.Dispose();
    using var hit = await Ready(scheduler, key, "fresh");
    Assert.NotEqual(one.Generation, hit.Generation);
    Assert.Equal("shared", MarkdownText(scheduler, one));
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
    await using var scheduler = CreateScheduler(build: _ => throw new InvalidOperationException("unexpected build"));
    await Assert.ThrowsAsync<MarkdownUnavailableException>(() => scheduler.RequestAsync(key, _ => Task.FromResult(wrong)));
    Assert.Equal((0, 0L, 0), scheduler.Usage);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task FailureReleasesInterestsWithoutNegativeRetention(bool captureFailure) {
    var key = CreateKey();
    int builds = 0;
    await using var scheduler = CreateScheduler(build: source => {
      if (!captureFailure && Interlocked.Increment(ref builds) == 1) throw new IOException("build failure");
      return Build(source);
    });
    await Assert.ThrowsAsync<IOException>(() => scheduler.RequestAsync(key, _ => captureFailure
        ? Task.FromException<MessageMarkdownSource>(new IOException("capture failure"))
        : Task.FromResult(CreateSource(key, "retry"))));
    Assert.Equal((0, 0L, 0), scheduler.Usage);
    using var retry = await Ready(scheduler, key, "retry");
    Assert.Equal("retry", MarkdownText(scheduler, retry));
  }

  [Fact]
  public async Task AccountsVariableMetadataAndRejectsOversizeWithoutRetention() {
    var key = CreateKey();
    var source = CreateSource(key, "[label](https://example.test/" + new string('x', 8192) + ")");
    var index = Build(source);
    Assert.True(index.AccountedBytes > 8192 * 2);
    await using var scheduler = CreateScheduler(retainedBytes: AccountedBytes(key, source, index) + MessageMarkdownCache.LeaseBytes - 1);
    await Assert.ThrowsAsync<MarkdownTooLargeException>(() => scheduler.RequestAsync(key, _ => Task.FromResult(source)));
    Assert.Equal((0, 0L, 0), scheduler.Usage);
    using var retry = await Ready(scheduler, key, "retry");
    Assert.Equal("retry", MarkdownText(scheduler, retry));
  }

  [Fact]
  public async Task InvalidationFencesRunningAndQueuedWorkButAllowsNewGeneration() {
    await using var worker = new BlockedBuild();
    var scheduler = worker.Scheduler;
    var key = CreateKey();
    var running = Ready(scheduler, key, "invalidated");
    await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var queued = Ready(scheduler, key with { ThroughSegment = 2 }, "queued");
    scheduler.Invalidate(key.SessionId);
    await Assert.ThrowsAsync<MarkdownUnavailableException>(() => running);
    await Assert.ThrowsAsync<MarkdownUnavailableException>(() => queued);
    Assert.Equal((0, 0L, 0), scheduler.Usage);
    var retry = Ready(scheduler, key, "fresh");
    worker.Release.Set();
    using var fresh = await retry;
    Assert.Equal("fresh", MarkdownText(scheduler, fresh));
    Assert.Equal(2, worker.Builds);
  }

  [Fact]
  public async Task ShutdownDrainsBuildRejectsInterestsAndKeepsDeliveredViewsUntilRelease() {
    var cold = CreateScheduler();
    Task coldShutdown = cold.DisposeAsync().AsTask();
    await cold.DisposeAsync();
    await coldShutdown.WaitAsync(TimeSpan.FromSeconds(10));
    await using var worker = new BlockedBuild();
    var scheduler = worker.Scheduler;
    var running = Ready(scheduler, CreateKey(), "shutdown");
    await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var queued = Ready(scheduler, CreateKey(), "queued");
    Task shutdown = scheduler.DisposeAsync().AsTask();
    Assert.False(shutdown.IsCompleted);
    await Assert.ThrowsAsync<ObjectDisposedException>(() => Ready(scheduler, CreateKey(), "denied"));
    await Assert.ThrowsAsync<ObjectDisposedException>(() => running);
    await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
    Assert.Equal((0, 0L, 0), scheduler.Usage);
    worker.Release.Set();
    await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
    await scheduler.DisposeAsync();
    await using var completed = CreateScheduler();
    var lease = await Ready(completed, CreateKey(), "retained");
    long bytes = completed.Usage.RetainedBytes;
    await completed.DisposeAsync();
    Assert.Equal(bytes, completed.Usage.RetainedBytes);
    Assert.Equal("retained", MarkdownText(completed, lease));
    lease.Dispose();
    lease.Dispose();
    Assert.Equal((0, 0L, 0), completed.Usage);
  }

  [Fact]
  public async Task ShutdownJoinsCaptureCancellationCallbacksAndReportsFailure() {
    var scheduler = CreateScheduler();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var release = new ManualResetEventSlim();
    var request = scheduler.RequestAsync(CreateKey(), async token => {
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
    Task shutdown = scheduler.DisposeAsync().AsTask();
#pragma warning restore S5034
    try {
      await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(10));
      await Assert.ThrowsAsync<ObjectDisposedException>(() => request);
      Assert.False(shutdown.IsCompleted);
    } finally {
      release.Set();
    }
    await Assert.ThrowsAsync<AggregateException>(() => shutdown.WaitAsync(TimeSpan.FromSeconds(10)));
    await Assert.ThrowsAsync<AggregateException>(() => scheduler.DisposeAsync().AsTask());
  }

  [Fact]
  public async Task CompletionDoesNotRetainCapturedSourceObject() {
    await using var scheduler = CreateScheduler();
    (Task<MessageMarkdownLease> pending, WeakReference sourceReference) = RequestTemporarySource(scheduler);
    using var ready = await pending;
    using var barrier = await Ready(scheduler, CreateKey(), "barrier");
    for (int attempt = 0; attempt < 3 && sourceReference.IsAlive; attempt++) {
#pragma warning disable S1215 // Explicitly verifies release of captured source objects.
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
#pragma warning restore S1215
    }
    Assert.False(sourceReference.IsAlive);
    Assert.StartsWith("retained ", MarkdownText(scheduler, ready), StringComparison.Ordinal);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static (Task<MessageMarkdownLease> Pending, WeakReference SourceReference) RequestTemporarySource(MessageMarkdownScheduler scheduler) {
    var key = CreateKey();
    var source = CreateSource(key, "retained " + new string('x', 4096));
    return (scheduler.RequestAsync(key, _ => Task.FromResult(source)), new WeakReference(source));
  }

  static MessageMarkdownScheduler CreateScheduler(int entries = 64, long retainedBytes = 64 * 1024 * 1024,
      int pendingKeys = 32, int leases = 64, Func<MessageMarkdownSource, MarkdownRenderIndex>? build = null) =>
      new(new(entries, retainedBytes, pendingKeys, leases), build);

  static MessageMarkdownKey CreateKey(long throughSegment = 1) => new(
      Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), MessageRole.Assistant, 0), 0, throughSegment, true, false);

  static MessageMarkdownSource CreateSource(MessageMarkdownKey key, string text) => new(
      key.SessionId, key.Message, new(key.Message, key.FromSegment, 0), new(key.Message, key.ThroughSegment, 0),
      Encoding.UTF8.GetByteCount(text), key.IsClosed, key.FinalOutputCommitted, text, [new(0, key.FromSegment)]);

  static Task<MessageMarkdownLease> Ready(MessageMarkdownScheduler scheduler, MessageMarkdownKey key, string text) =>
      scheduler.RequestAsync(key, _ => Task.FromResult(CreateSource(key, text)));

  static MarkdownRenderIndex Build(MessageMarkdownSource source) => MarkdownParser.Parse(source.Source);

  static string MarkdownText(MessageMarkdownScheduler scheduler, MessageMarkdownLease lease) =>
      string.Concat(scheduler.Read(lease, new(lease.Generation, 0)).Pieces.Select(piece => piece.Text));

  static long AccountedBytes(MessageMarkdownKey key, MessageMarkdownSource source, MarkdownRenderIndex index) =>
      index.AccountedBytes + 512 + 2L * key.Pipeline.Length + 24 + source.Segments.Count * 40L;

  sealed class BlockedBuild : IAsyncDisposable {
    internal MessageMarkdownScheduler Scheduler { get; }
    internal ManualResetEventSlim Release { get; } = new();
    internal TaskCompletionSource<Thread> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal List<Thread> Threads { get; } = [];
    internal int Builds => Threads.Count;

    internal BlockedBuild(int pendingKeys = 32, int leases = 64) {
      Scheduler = CreateScheduler(pendingKeys: pendingKeys, leases: leases, build: source => {
        Threads.Add(Thread.CurrentThread);
        if (Threads.Count == 1) {
          Entered.SetResult(Thread.CurrentThread);
          if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Markdown worker was not released.");
        }
        return Build(source);
      });
    }

    public async ValueTask DisposeAsync() {
      Release.Set();
      await Scheduler.DisposeAsync();
      Release.Dispose();
    }
  }
}
