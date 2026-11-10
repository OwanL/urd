using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Urd.Core.MarkdownRendering;
using Urd.Core.Sessions;
using Xunit;

namespace Urd.Backend.Tests.MarkdownRendering;

public sealed class MessageMarkdownSchedulerTests {
  const string Small = "small é", Large = "large message";
  static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

  [Fact]
  public async Task SmallCompletesDuringLargeParseWithoutStartingAnotherLargeCapture() {
    await using var worker = new BlockedBuild();
    var largeKey = Key(Large);
    var large = Ready(worker.Scheduler, largeKey, Large);
    await worker.Entered[0].Task.WaitAsync(Timeout);
    int queuedCaptures = 0;
    var nextKey = Key(Large);
    var next = worker.Scheduler.RequestAsync(nextKey, _ => {
      Interlocked.Increment(ref queuedCaptures);
      return Task.FromResult(Source(nextKey, Large));
    });
    var observer = Ready(worker.Scheduler, largeKey, Large);
    var small = Ready(worker.Scheduler, Key(Small), Small);
    await worker.Entered[1].Task.WaitAsync(Timeout);
    Assert.Equal(0, Volatile.Read(ref queuedCaptures));
    Assert.False(large.IsCompleted);
    Assert.Equal(2, worker.Threads.Distinct().Count());
    Assert.All(worker.Threads, thread => Assert.Equal(ThreadPriority.BelowNormal, thread.Priority));
    worker.Release[1].Set();
    using var urgent = await small.WaitAsync(Timeout);
    Assert.Equal(Small, Text(worker.Scheduler, urgent));
    Assert.False(large.IsCompleted);
    worker.Release[0].Set();
    await Task.WhenAll(large, observer, next).WaitAsync(Timeout);
    using var first = await large;
    using var same = await observer;
    using var second = await next;
    Assert.NotSame(first, same);
    Assert.Equal(first.Generation, same.Generation);
    Assert.Equal(1, queuedCaptures);
    Assert.Equal(2, worker.Threads.Count(thread => thread.Name == "Message markdown large"));
  }

  [Fact]
  public async Task LeaseExposesRenderManifestAndFencesBackwardReads() {
    await using var scheduler = CreateScheduler();
    using var lease = await Ready(scheduler, Key(Small), Small);
    var expected = MarkdownParser.Parse(Small);
    Assert.Equal(new MarkdownRenderManifest(lease.Generation, expected.SourceUtf8Bytes,
        expected.PieceCount, expected.ContextCount), lease.Manifest);
    var page = scheduler.ReadBefore(lease, new(lease.Generation, lease.Manifest.PieceCount));
    Assert.Equal(lease.Manifest.PieceCount, page.Next);
    Assert.Equal(Small, string.Concat(page.Pieces.Select(piece => piece.Text)));

    await using var other = CreateScheduler();
    Assert.Throws<MarkdownUnavailableException>(() => other.ReadBefore(lease,
        new(lease.Generation, lease.Manifest.PieceCount)));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ClassFifoRetainsReplacementAgeAcrossClassChange(bool changeClass) {
    await using var worker = new BlockedBuild();
    var blockers = new[] { Ready(worker.Scheduler, Key(Large), Large), Ready(worker.Scheduler, Key(Small), Small) };
    await Task.WhenAll(worker.Entered.Select(entered => entered.Task)).WaitAsync(Timeout);
    var key = Key(Small) with { IsClosed = false };
    var old = worker.Scheduler.RequestAsync(key, _ => throw new InvalidOperationException("obsolete capture"));
    string replacementText = changeClass ? Large : Small;
    var replacement = key with { ThroughSegment = 2, SourceUtf8Bytes = Bytes(replacementText), IsClosed = true };
    var nextKey = Key(replacementText);
    var updated = Ready(worker.Scheduler, replacement, replacementText);
    var next = Ready(worker.Scheduler, nextKey, replacementText);
    var duplicate = Ready(worker.Scheduler, replacement, replacementText);
    var lastKey = Key(Small);
    var last = Ready(worker.Scheduler, lastKey, Small);
    await Assert.ThrowsAsync<MarkdownSupersededException>(() => old);
    await Assert.ThrowsAsync<MarkdownSupersededException>(() => Ready(worker.Scheduler, key, Small));
    Assert.Equal(3, worker.Scheduler.Usage.PendingKeys);
    worker.Release[changeClass ? 0 : 1].Set();
    using var latest = await updated.WaitAsync(Timeout);
    using var same = await duplicate;
    using var following = await next;
    var order = worker.Parsed.ToArray();
    Assert.True(Array.IndexOf(order, replacement) < Array.IndexOf(order, nextKey));
    Assert.Equal(latest.Generation, same.Generation);
    worker.Release[0].Set();
    worker.Release[1].Set();
    using var tail = await last;
    using var large = await blockers[0];
    using var small = await blockers[1];
    order = worker.Parsed.ToArray();
    if (!changeClass) Assert.True(Array.IndexOf(order, nextKey) < Array.IndexOf(order, lastKey));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ActiveCompletionRemainsReadableWhileNewerExtentWaits(bool small) {
    string text = small ? Small : Large;
    await using var worker = new BlockedBuild();
    int lane = small ? 1 : 0;
    var key = Key(text) with { IsClosed = false };
    var running = Ready(worker.Scheduler, key, text);
    await worker.Entered[lane].Task.WaitAsync(Timeout);
    var next = key with { ThroughSegment = 2, IsClosed = true, FinalOutputCommitted = true };
    var updated = Ready(worker.Scheduler, next, text);
    worker.Release[lane].Set();
    using var stale = await running;
    using var current = await updated;
    Assert.NotEqual(stale.Generation, current.Generation);
    Assert.Equal(text, Text(worker.Scheduler, stale));
    Assert.Equal(text, Text(worker.Scheduler, current));
  }

  [Theory]
  [InlineData(false, false)]
  [InlineData(false, true)]
  [InlineData(true, false)]
  [InlineData(true, true)]
  public async Task CancellationReleasesOnlyItsInterestAndLastQueuedSlot(bool small, bool queued) {
    string text = small ? Small : Large;
    int lane = small ? 1 : 0;
    await using var worker = new BlockedBuild(pendingKeys: 1);
    var key = Key(text);
    var blocker = queued ? Ready(worker.Scheduler, Key(text), text) : null;
    if (queued) await worker.Entered[lane].Task.WaitAsync(Timeout);
    using var cancellation = new CancellationTokenSource();
    var abandoned = worker.Scheduler.RequestAsync(key, _ => Task.FromResult(Source(key, text)), cancellation.Token);
    if (!queued) await worker.Entered[lane].Task.WaitAsync(Timeout);
    var interested = Ready(worker.Scheduler, key, text);
    long before = worker.Scheduler.Usage.RetainedBytes;
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
    Assert.Equal(before - MessageMarkdownCache.LeaseBytes, worker.Scheduler.Usage.RetainedBytes);
    if (queued) {
      using var lastCancellation = new CancellationTokenSource();
      var removable = Key(text);
      await Assert.ThrowsAsync<MarkdownQueueFullException>(() => worker.Scheduler.RequestAsync(removable,
          _ => Task.FromResult(Source(removable, text)), lastCancellation.Token));
    }
    worker.Release[lane].Set();
    using var ready = await interested;
    using var blocking = blocker is null ? null : await blocker;
    Assert.Equal(text, Text(worker.Scheduler, ready));
    using var alreadyCancelled = new CancellationTokenSource();
    await alreadyCancelled.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.Scheduler.RequestAsync(key,
        _ => throw new InvalidOperationException("cancelled capture"), alreadyCancelled.Token));
  }

  [Theory]
  [InlineData(false, false)]
  [InlineData(false, true)]
  [InlineData(true, false)]
  [InlineData(true, true)]
  public async Task LastInterestCancellationAllowsRetry(bool small, bool queued) {
    string text = small ? Small : Large;
    await using var worker = new BlockedBuild(pendingKeys: 1);
    var blocking = queued ? Ready(worker.Scheduler, Key(text), text) : null;
    if (queued) await worker.Entered[small ? 1 : 0].Task.WaitAsync(Timeout);
    using var cancellation = new CancellationTokenSource();
    var key = Key(text);
    var cancelled = worker.Scheduler.RequestAsync(key, _ => Task.FromResult(Source(key, text)), cancellation.Token);
    if (!queued) await worker.Entered[small ? 1 : 0].Task.WaitAsync(Timeout);
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    Assert.Equal(0, worker.Scheduler.Usage.PendingKeys);
    var retry = Ready(worker.Scheduler, key, text);
    worker.Release[small ? 1 : 0].Set();
    using var ready = await retry;
    using var blocker = blocking is null ? null : await blocking;
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task PinsAndHitReservationsSurviveInvalidationAndShutdownInBothLanes(bool small) {
    string text = small ? Small : Large;
    await using var worker = new BlockedBuild(leases: 3);
    var key = Key(text);
    var first = Ready(worker.Scheduler, key, text);
    await worker.Entered[small ? 1 : 0].Task.WaitAsync(Timeout);
    var duplicate = Ready(worker.Scheduler, key, text);
    worker.Release[small ? 1 : 0].Set();
    await Task.WhenAll(first, duplicate).WaitAsync(Timeout);
    using var original = await first;
    using var second = await duplicate;
    using var hit = await worker.Scheduler.RequestAsync(key, _ => throw new InvalidOperationException("cache miss"));
    await Assert.ThrowsAsync<MarkdownCacheFullException>(() => Ready(worker.Scheduler, key, text));
    Assert.Equal(original.Generation, hit.Generation);
    worker.Scheduler.Invalidate(key.SessionId);
    await worker.Scheduler.DisposeAsync();
    long bytes = worker.Scheduler.Usage.RetainedBytes;
    original.Dispose();
    original.Dispose();
    Assert.Equal(bytes - MessageMarkdownCache.LeaseBytes, worker.Scheduler.Usage.RetainedBytes);
    Assert.Equal(text, Text(worker.Scheduler, second));
    second.Dispose();
    Assert.Equal(text, Text(worker.Scheduler, hit));
    hit.Dispose();
    Assert.Equal((0, 0L, 0), worker.Scheduler.Usage);
  }

  [Fact]
  public async Task InvalidationFencesBothLanesAndTotalQueueLimitAllowsFreshGeneration() {
    await using var worker = new BlockedBuild(pendingKeys: 2);
    var key = Key(Large);
    var smallKey = Key(Small) with { SessionId = key.SessionId };
    var large = Ready(worker.Scheduler, key, Large);
    var small = Ready(worker.Scheduler, smallKey, Small);
    await Task.WhenAll(worker.Entered.Select(entered => entered.Task)).WaitAsync(Timeout);
    var queued = Ready(worker.Scheduler, key with { ThroughSegment = 2 }, Large);
    var queuedSmall = Ready(worker.Scheduler, smallKey with { ThroughSegment = 2 }, Small);
    await Assert.ThrowsAsync<MarkdownQueueFullException>(() => Ready(worker.Scheduler, Key(Small), Small));
    worker.Scheduler.Invalidate(key.SessionId);
    foreach (var task in new[] { large, small, queued, queuedSmall })
      await Assert.ThrowsAsync<MarkdownUnavailableException>(() => task);
    Assert.Equal((0, 0L, 0), worker.Scheduler.Usage);
    var retry = Ready(worker.Scheduler, key, Large);
    worker.Release[0].Set();
    using var fresh = await retry;
    worker.Release[1].Set();
    Assert.Equal(Large, Text(worker.Scheduler, fresh));
  }

  [Fact]
  public async Task ShutdownWaitsForBothParsersAndRejectsQueuedInterests() {
    await using var worker = new BlockedBuild();
    var large = Ready(worker.Scheduler, Key(Large), Large);
    var small = Ready(worker.Scheduler, Key(Small), Small);
    await Task.WhenAll(worker.Entered.Select(entered => entered.Task)).WaitAsync(Timeout);
    var queued = Ready(worker.Scheduler, Key(Small), Small);
    var shutdown = worker.Scheduler.DisposeAsync().AsTask();
    foreach (var task in new[] { large, small, queued })
      await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
    await Assert.ThrowsAsync<ObjectDisposedException>(() => Ready(worker.Scheduler, Key(Small), Small));
    Assert.Equal((0, 0L, 0), worker.Scheduler.Usage);
    worker.Release[0].Set();
    Assert.False(shutdown.IsCompleted);
    worker.Release[1].Set();
    await shutdown.WaitAsync(Timeout);
    await worker.Scheduler.DisposeAsync();
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task SizeIntegrityRejectsContradictionsInActivePendingCachedAndStaleGenerations(bool small) {
    string text = small ? Small : Large;
    await using var worker = new BlockedBuild();
    var key = Key(text);
    var active = Ready(worker.Scheduler, key, text);
    await worker.Entered[small ? 1 : 0].Task.WaitAsync(Timeout);
    async Task Reject() {
      await Assert.ThrowsAsync<ArgumentException>(() => Ready(worker.Scheduler, key with { SourceUtf8Bytes = Bytes(text) + 1 }, text));
      await Assert.ThrowsAsync<ArgumentException>(() => Ready(worker.Scheduler,
          key with { ThroughSegment = 3, SourceUtf8Bytes = 0 }, text));
    }
    await Reject();
    var pendingKey = Key(text);
    var pending = Ready(worker.Scheduler, pendingKey, text);
    await Assert.ThrowsAsync<ArgumentException>(() => Ready(worker.Scheduler, pendingKey with { SourceUtf8Bytes = 0 }, text));
    worker.Release[small ? 1 : 0].Set();
    using var ready = await active;
    using var other = await pending;
    await Reject();
    worker.Scheduler.Invalidate(key.SessionId);
    await Reject();
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  [InlineData(2)]
  [InlineData(3)]
  public async Task CaptureMismatchNeverInvokesParserIncludingLyingSmallClassification(int mismatch) {
    var key = Key("é");
    var source = Source(key, "é");
    var wrong = mismatch switch {
      0 => Source(key, Large) with { Utf8Bytes = key.SourceUtf8Bytes },
      1 => source with { Utf8Bytes = 1 },
      2 => source with { Message = key.Message with { HistoryRevision = 1 } },
      _ => source with { IsClosed = false }
    };
    await using var scheduler = CreateScheduler(parse: _ => throw new InvalidOperationException("unexpected parse"));
    await Assert.ThrowsAsync<MarkdownUnavailableException>(() => scheduler.RequestAsync(key, _ => Task.FromResult(wrong)));
    Assert.Equal((0, 0L, 0), scheduler.Usage);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CacheEvictsOnlyUnpinnedLruAndPreservesOldViewOnFull(bool byBytes) {
    var key = Key(Small);
    long bytes = MarkdownParser.Parse(Small).AccountedBytes + 512 + 2L * key.Pipeline.Length + 64;
    await using var scheduler = CreateScheduler(entries: byBytes ? 8 : 2,
        retainedBytes: byBytes ? bytes * 2 + MessageMarkdownCache.LeaseBytes * 3 : 1_000_000);
    using var first = await Ready(scheduler, key, Small);
    var secondKey = Key(Small);
    using var second = await Ready(scheduler, secondKey, Small);
    long retained = scheduler.Usage.RetainedBytes;
    await Assert.ThrowsAsync<MarkdownCacheFullException>(() => Ready(scheduler, Key(Small), Small));
    Assert.Equal(retained, scheduler.Usage.RetainedBytes);
    Assert.Equal(Small, Text(scheduler, first));
    var staleCursor = new MarkdownRenderCursor(second.Generation, 0);
    second.Dispose();
    using var third = await Ready(scheduler, Key(Small), Small);
    Assert.Equal(2, scheduler.Usage.Entries);
    Assert.Throws<MarkdownUnavailableException>(() => Text(scheduler, second));
    first.Dispose();
    third.Dispose();
    using var hit = await scheduler.RequestAsync(key, _ => throw new InvalidOperationException("cache miss"));
    using var rebuilt = await Ready(scheduler, secondKey, Small);
    Assert.NotEqual(second.Generation, rebuilt.Generation);
    Assert.Throws<ArgumentException>(() => scheduler.Read(rebuilt, staleCursor));
  }

  [Fact]
  public async Task StaleLeaseKeepsContextAndSourceMappingAcrossNewGeneration() {
    await using var scheduler = CreateScheduler();
    const string text = "**café** tail";
    var key = Key(text, 2);
    var source = Source(key, text) with { Segments = [new(0, 0), new(9, 1)] };
    using var old = await scheduler.RequestAsync(key, _ => Task.FromResult(source));
    var anchor = scheduler.Locate(old, 3);
    var page = scheduler.Read(old, new(old.Generation, 0));
    scheduler.Invalidate(key.SessionId);
    using var fresh = await Ready(scheduler, key, text);
    Assert.NotEqual(old.Generation, fresh.Generation);
    Assert.Equal(anchor, scheduler.Locate(old, 3));
    var strong = Assert.Single(page.Pieces, piece => piece.Text == "café");
    Assert.Equal(MarkdownElementKind.Strong, scheduler.ReadContext(old, strong.Context, new(old.Generation, 0)).Context.Kind);
    var descriptors = scheduler.ReadContexts(old, [strong.Context, 0], new(old.Generation, 0));
    Assert.Equal(new[] { strong.Context, 0 }, descriptors.Contexts.Select(context => context.Id));
    Assert.Equal(new(key.Message, 1, 2), scheduler.SourceCursor(old, 11));
    Assert.Equal(new(key.Message, 2, 0), scheduler.SourceCursor(old, old.SourceUtf8Bytes));
    Assert.Throws<ArgumentException>(() => scheduler.Read(fresh, anchor));
    old.Dispose();
    Assert.Equal(1, scheduler.Usage.Entries);
    await using var other = CreateScheduler();
    Assert.Throws<MarkdownUnavailableException>(() => Text(other, fresh));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task FailuresReleaseInterestsAndAllowRetry(bool captureFailure) {
    var key = Key(Small);
    int parses = 0;
    await using var scheduler = CreateScheduler(parse: source => {
      if (!captureFailure && Interlocked.Increment(ref parses) == 1) throw new IOException("parse failure");
      return MarkdownParser.Parse(source.Source);
    });
    await Assert.ThrowsAsync<IOException>(() => scheduler.RequestAsync(key, _ => captureFailure
        ? Task.FromException<MessageMarkdownSource>(new IOException("capture failure")) : Task.FromResult(Source(key, Small))));
    Assert.Equal((0, 0L, 0), scheduler.Usage);
    using var retry = await Ready(scheduler, key, Small);
  }

  [Fact]
  public async Task OversizeMetadataDoesNotRemainRetained() {
    string text = "[label](https://example.test/" + new string('x', 8192) + ")";
    var key = Key(text);
    long bytes = MarkdownParser.Parse(text).AccountedBytes + 512 + 2L * key.Pipeline.Length + 64;
    await using var scheduler = CreateScheduler(retainedBytes: bytes + MessageMarkdownCache.LeaseBytes - 1);
    await Assert.ThrowsAsync<MarkdownTooLargeException>(() => Ready(scheduler, key, text));
    Assert.Equal((0, 0L, 0), scheduler.Usage);
  }

  [Fact]
  public async Task ShutdownJoinsCaptureCancellationCallbacksAndReportsFailure() {
    var scheduler = CreateScheduler();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var release = new ManualResetEventSlim();
    var request = scheduler.RequestAsync(Key(Small), async token => {
      using var registration = token.Register(() => {
        cancelling.SetResult();
        if (!release.Wait(Timeout)) throw new TimeoutException("Cancellation was not released.");
        throw new IOException("controlled cancellation failure");
      });
      entered.SetResult();
      await Task.Delay(System.Threading.Timeout.Infinite, token);
      throw new InvalidOperationException("unreachable capture");
    });
    await entered.Task.WaitAsync(Timeout);
#pragma warning disable S5034 // Each DisposeAsync returns a fresh ValueTask over the shared shutdown Task.
    Task shutdown = scheduler.DisposeAsync().AsTask();
#pragma warning restore S5034
    try {
      await cancelling.Task.WaitAsync(Timeout);
      await Assert.ThrowsAsync<ObjectDisposedException>(() => request);
      Assert.False(shutdown.IsCompleted);
    } finally {
      release.Set();
    }
    await Assert.ThrowsAsync<AggregateException>(() => shutdown.WaitAsync(Timeout));
    await Assert.ThrowsAsync<AggregateException>(() => scheduler.DisposeAsync().AsTask());
  }

  [Fact]
  public async Task CompletionDoesNotRetainCapturedSourceObject() {
    await using var scheduler = CreateScheduler();
    (Task<MessageMarkdownLease> pending, WeakReference sourceReference) = RequestTemporarySource(scheduler);
    using var ready = await pending;
    using var barrier = await Ready(scheduler, Key(Large), Large);
    for (int attempt = 0; attempt < 3 && sourceReference.IsAlive; attempt++) {
#pragma warning disable S1215 // Explicitly verifies release of captured source objects.
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
#pragma warning restore S1215
    }
    Assert.False(sourceReference.IsAlive);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static (Task<MessageMarkdownLease>, WeakReference) RequestTemporarySource(MessageMarkdownScheduler scheduler) {
    var key = Key(Large);
    var source = Source(key, Large);
    return (scheduler.RequestAsync(key, _ => Task.FromResult(source)), new WeakReference(source));
  }

  static MessageMarkdownScheduler CreateScheduler(int entries = 64, long retainedBytes = 64 * 1024 * 1024,
      int pendingKeys = 32, int leases = 64, Func<MessageMarkdownSource, MarkdownRenderIndex>? parse = null) =>
      new(new(entries, retainedBytes, pendingKeys, leases, 8), parse);
  static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);
  static MessageMarkdownKey Key(string text, long through = 1) => new(
      Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), MessageRole.Assistant, 0), 0, through, true, false, Bytes(text));
  static MessageMarkdownSource Source(MessageMarkdownKey key, string text) => new(
      key.SessionId, key.Message, new(key.Message, key.FromSegment, 0), new(key.Message, key.ThroughSegment, 0),
      Bytes(text), key.IsClosed, key.FinalOutputCommitted, text, [new(0, key.FromSegment)]);
  static Task<MessageMarkdownLease> Ready(MessageMarkdownScheduler scheduler, MessageMarkdownKey key, string text) =>
      scheduler.RequestAsync(key, _ => Task.FromResult(Source(key, text)));
  static string Text(MessageMarkdownScheduler scheduler, MessageMarkdownLease lease) =>
      string.Concat(scheduler.Read(lease, new(lease.Generation, 0)).Pieces.Select(piece => piece.Text));

  sealed class BlockedBuild : IAsyncDisposable {
    internal MessageMarkdownScheduler Scheduler { get; }
    internal ManualResetEventSlim[] Release { get; } = [new(), new()];
    internal TaskCompletionSource[] Entered { get; } = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
    internal ConcurrentQueue<Thread> Threads { get; } = new();
    internal ConcurrentQueue<MessageMarkdownKey> Parsed { get; } = new();

    internal BlockedBuild(int pendingKeys = 32, int leases = 64) {
      int[] counts = new int[2];
      Scheduler = CreateScheduler(pendingKeys: pendingKeys, leases: leases, parse: source => {
        int lane = source.Utf8Bytes <= 8 ? 1 : 0;
        Threads.Enqueue(Thread.CurrentThread);
        Parsed.Enqueue(new(source.SessionId, source.Message, source.From.SegmentIndex, source.AvailableThrough.SegmentIndex,
            source.IsClosed, source.FinalOutputCommitted, source.Utf8Bytes));
        if (Interlocked.Increment(ref counts[lane]) == 1) {
          Entered[lane].SetResult();
          if (!Release[lane].Wait(Timeout)) throw new TimeoutException("Markdown worker was not released.");
        }
        return MarkdownParser.Parse(source.Source);
      });
    }

    public async ValueTask DisposeAsync() {
      foreach (var release in Release) release.Set();
      await Scheduler.DisposeAsync();
      foreach (var release in Release) release.Dispose();
    }
  }
}
