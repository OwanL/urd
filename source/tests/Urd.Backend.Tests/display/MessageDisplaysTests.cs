using System.Runtime.CompilerServices;
using System.Text;
using Urd.Core.Display;
using Urd.Core.Sessions;
using Xunit;

namespace Urd.Backend.Tests.Display;

public sealed class MessageDisplaysTests {
  [Fact]
  public async Task UsesOneBelowNormalWorkerAndDeduplicatesCapture() {
    var firstKey = CreateKey();
    var secondKey = CreateKey();
    var firstSource = CreateSource(firstKey, "first");
    var secondSource = CreateSource(secondKey, "second");
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
    int builds = 0, activeBuilds = 0, maximumActiveBuilds = 0, firstCaptures = 0, secondCaptures = 0;
    Thread? workerThread = null;
    var displays = CreateDisplays(build: source => {
      int active = Interlocked.Increment(ref activeBuilds);
      RecordMaximum(ref maximumActiveBuilds, active);
      int build = Interlocked.Increment(ref builds);
      workerThread = Thread.CurrentThread;
      if (build == 1) {
        entered.TrySetResult(Thread.CurrentThread);
        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the display worker.");
      }
      try {
        return Build(source);
      } finally {
        Interlocked.Decrement(ref activeBuilds);
      }
    });

    try {
      Task<MessageDisplayReady> first = displays.RequestAsync(firstKey, _ => {
        Interlocked.Increment(ref firstCaptures);
        return Task.FromResult(firstSource);
      });
      Thread firstWorker = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
      Task<MessageDisplayReady> duplicate = displays.RequestAsync(firstKey, _ => {
        Interlocked.Increment(ref firstCaptures);
        return Task.FromResult(firstSource);
      });
      Task<MessageDisplayReady> second = displays.RequestAsync(secondKey, _ => {
        Interlocked.Increment(ref secondCaptures);
        return Task.FromResult(secondSource);
      });

      Assert.Equal(1, Volatile.Read(ref builds));
      Assert.Equal(1, Volatile.Read(ref firstCaptures));
      Assert.Equal(0, Volatile.Read(ref secondCaptures));
      release.Set();

      MessageDisplayReady firstReady = await first;
      MessageDisplayReady duplicateReady = await duplicate;
      await second;

      Assert.Same(firstReady, duplicateReady);
      Assert.Equal(2, Volatile.Read(ref builds));
      Assert.Equal(1, Volatile.Read(ref maximumActiveBuilds));
      Assert.Equal(1, Volatile.Read(ref secondCaptures));
      Assert.Same(firstWorker, workerThread);
      Assert.Equal("Message displays", workerThread!.Name);
      Assert.Equal(ThreadPriority.BelowNormal, workerThread.Priority);
    } finally {
      release.Set();
      await displays.DisposeAsync();
    }
  }

  [Fact]
  public async Task BoundsPendingWorkReplacesLatestExtentAndPublishesCompletedRunningExtent() {
    var activeKey = CreateKey();
    var pendingKey = activeKey with { ThroughSegment = 2 };
    var newestKey = activeKey with { ThroughSegment = 3 };
    var otherKey = CreateKey();
    var activeSource = CreateSource(activeKey, "active");
    var pendingSource = CreateSource(pendingKey, "superseded");
    var newestSource = CreateSource(newestKey, "newest");
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int builds = 0, supersededCaptures = 0, newestCaptures = 0;
    var displays = CreateDisplays(pendingKeys: 1, build: source => {
      if (Interlocked.Increment(ref builds) == 1) {
        entered.TrySetResult();
        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the display worker.");
      }
      return Build(source);
    });

    try {
      Task<MessageDisplayReady> running = displays.RequestAsync(activeKey, Capture(activeSource));
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
      Task<MessageDisplayReady> superseded = displays.RequestAsync(pendingKey, _ => {
        Interlocked.Increment(ref supersededCaptures);
        return Task.FromResult(pendingSource);
      });
      Task<MessageDisplayReady> newest = displays.RequestAsync(newestKey, _ => {
        Interlocked.Increment(ref newestCaptures);
        return Task.FromResult(newestSource);
      });

      await Assert.ThrowsAsync<DisplaySupersededException>(async () => await superseded);
      Assert.Equal(0, Volatile.Read(ref supersededCaptures));
      Assert.Equal(1, displays.Usage.PendingKeys);
      await Assert.ThrowsAsync<DisplayQueueFullException>(() => displays.RequestAsync(otherKey,
          Capture(CreateSource(otherKey, "other"))));

      release.Set();
      MessageDisplayReady runningReady = await running;
      MessageDisplayReady newestReady = await newest;

      Assert.Equal("active", DisplayText(displays, runningReady));
      Assert.Equal("newest", DisplayText(displays, newestReady));
      Assert.Equal(0, Volatile.Read(ref supersededCaptures));
      Assert.Equal(1, Volatile.Read(ref newestCaptures));
      Assert.Equal(2, Volatile.Read(ref builds));
    } finally {
      release.Set();
      await displays.DisposeAsync();
    }
  }

  [Fact]
  public async Task EvictsLeastRecentlyUsedEntriesByCountAndRetainedBytes() {
    var countDisplays = CreateDisplays(entries: 2, retainedBytes: 1_000_000);
    try {
      var firstKey = CreateKey();
      var secondKey = CreateKey();
      var thirdKey = CreateKey();
      MessageDisplayReady first = await Ready(countDisplays, firstKey, "same");
      MessageDisplayReady second = await Ready(countDisplays, secondKey, "same");
      Assert.Equal("same", DisplayText(countDisplays, first));
      MessageDisplayReady third = await Ready(countDisplays, thirdKey, "same");

      Assert.Equal(2, countDisplays.Usage.Entries);
      Assert.Equal("same", DisplayText(countDisplays, first));
      Assert.Equal("same", DisplayText(countDisplays, third));
      Assert.Throws<DisplayUnavailableException>(() => DisplayText(countDisplays, second));
    } finally {
      await countDisplays.DisposeAsync();
    }

    var templateKey = CreateKey();
    var templateSource = CreateSource(templateKey, "same");
    long oneEntryBytes = AccountedBytes(templateKey, templateSource, Build(templateSource));
    var byteDisplays = CreateDisplays(entries: 8, retainedBytes: oneEntryBytes * 2);
    try {
      var firstKey = CreateKey();
      var secondKey = CreateKey();
      var thirdKey = CreateKey();
      MessageDisplayReady first = await Ready(byteDisplays, firstKey, "same");
      MessageDisplayReady second = await Ready(byteDisplays, secondKey, "same");
      Assert.Equal("same", DisplayText(byteDisplays, first));
      MessageDisplayReady third = await Ready(byteDisplays, thirdKey, "same");

      Assert.Equal(2, byteDisplays.Usage.Entries);
      Assert.Equal(oneEntryBytes * 2, byteDisplays.Usage.RetainedBytes);
      Assert.Equal("same", DisplayText(byteDisplays, first));
      Assert.Equal("same", DisplayText(byteDisplays, third));
      Assert.Throws<DisplayUnavailableException>(() => DisplayText(byteDisplays, second));
    } finally {
      await byteDisplays.DisposeAsync();
    }
  }

  [Fact]
  public async Task RebuiltGenerationRejectsEvictedHandlesAndOldCursors() {
    var displays = CreateDisplays(entries: 1);
    try {
      var firstKey = CreateKey();
      MessageDisplayReady original = await Ready(displays, firstKey, "first");
      DisplayCursor staleCursor = new(original.Generation, 0);
      var secondKey = CreateKey();
      await Ready(displays, secondKey, "second");

      Assert.Throws<DisplayUnavailableException>(() => DisplayText(displays, original));
      MessageDisplayReady rebuilt = await Ready(displays, firstKey, "first");

      Assert.NotEqual(original.Generation, rebuilt.Generation);
      Assert.Throws<DisplayUnavailableException>(() => DisplayText(displays, original));
      Assert.Throws<ArgumentException>(() => displays.Read(rebuilt, staleCursor));
    } finally {
      await displays.DisposeAsync();
    }
  }

  [Fact]
  public async Task RejectsCapturedSourceThatDoesNotMatchRequestedKey() {
    var key = CreateKey();
    var wrongSource = CreateSource(key, "wrong") with { SessionId = Guid.NewGuid() };
    int builds = 0;
    var displays = CreateDisplays(build: source => {
      Interlocked.Increment(ref builds);
      return Build(source);
    });
    try {
      await Assert.ThrowsAsync<DisplayUnavailableException>(async () =>
          await displays.RequestAsync(key, Capture(wrongSource)));
      Assert.Equal(0, Volatile.Read(ref builds));
    } finally {
      await displays.DisposeAsync();
    }
  }

  [Fact]
  public async Task CancellingQueuedInterestRemovesWorkAndAllowsRetry() {
    var activeKey = CreateKey();
    var queuedKey = CreateKey();
    var activeSource = CreateSource(activeKey, "active");
    var queuedSource = CreateSource(queuedKey, "queued");
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int queuedCaptures = 0;
    var displays = CreateDisplays(build: source => {
      if (source.SessionId == activeKey.SessionId) {
        entered.TrySetResult();
        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the display worker.");
      }
      return Build(source);
    });

    try {
      Task<MessageDisplayReady> active = displays.RequestAsync(activeKey, Capture(activeSource));
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
      using var cancellation = new CancellationTokenSource();
      Task<MessageDisplayReady> abandoned = displays.RequestAsync(queuedKey, _ => {
        Interlocked.Increment(ref queuedCaptures);
        return Task.FromResult(queuedSource);
      }, cancellation.Token);
      await cancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);

      Assert.Equal(0, displays.Usage.PendingKeys);
      Assert.Equal(0, Volatile.Read(ref queuedCaptures));
      Task<MessageDisplayReady> retry = displays.RequestAsync(queuedKey, _ => {
        Interlocked.Increment(ref queuedCaptures);
        return Task.FromResult(queuedSource);
      });
      Assert.Equal(1, displays.Usage.PendingKeys);
      release.Set();
      await active;
      Assert.Equal("queued", DisplayText(displays, await retry));
      Assert.Equal(1, Volatile.Read(ref queuedCaptures));
    } finally {
      release.Set();
      await displays.DisposeAsync();
    }
  }

  [Fact]
  public async Task CancellingRunningInterestDoesNotCancelSharedBuildOrRetry() {
    var key = CreateKey();
    var source = CreateSource(key, "running");
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int captures = 0;
    var displays = CreateDisplays(build: message => {
      entered.TrySetResult();
      if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the display worker.");
      return Build(message);
    });

    try {
      using var cancellation = new CancellationTokenSource();
      Task<MessageDisplayReady> abandoned = displays.RequestAsync(key, _ => {
        Interlocked.Increment(ref captures);
        return Task.FromResult(source);
      }, cancellation.Token);
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
      await cancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);
      Task<MessageDisplayReady> retry = displays.RequestAsync(key, _ => {
        Interlocked.Increment(ref captures);
        return Task.FromResult(source);
      });

      Assert.Equal(1, Volatile.Read(ref captures));
      release.Set();
      MessageDisplayReady ready = await retry;
      Assert.Equal("running", DisplayText(displays, ready));
      Assert.Equal(1, Volatile.Read(ref captures));
    } finally {
      release.Set();
      await displays.DisposeAsync();
    }
  }

  [Fact]
  public async Task FailedBuildIsNotCachedAndCanBeRetried() {
    var key = CreateKey();
    var source = CreateSource(key, "retry");
    int targetBuilds = 0;
    var displays = CreateDisplays(build: message => {
      if (message.SessionId == key.SessionId && Interlocked.Increment(ref targetBuilds) == 1)
        throw new InvalidOperationException("controlled build failure");
      return Build(message);
    });
    try {
      await Assert.ThrowsAsync<InvalidOperationException>(async () =>
          await displays.RequestAsync(key, Capture(source)));

      // A distinct queued build proves the failed work left the active slot before retry.
      var barrierKey = CreateKey();
      await Ready(displays, barrierKey, "barrier");
      MessageDisplayReady retried = await Ready(displays, key, "retry");

      Assert.Equal(2, Volatile.Read(ref targetBuilds));
      Assert.Equal("retry", DisplayText(displays, retried));
    } finally {
      await displays.DisposeAsync();
    }
  }

  [Fact]
  public async Task CaptureFailureHasNoNegativeRetentionAndCanRetryImmediately() {
    await using var displays = CreateDisplays();
    var key = CreateKey();
    await Assert.ThrowsAsync<IOException>(() => displays.RequestAsync(key,
        _ => Task.FromException<MessageDisplaySource>(new IOException("temporary storage failure"))));
    Assert.Equal(0, displays.Usage.Entries);
    Assert.Equal("retry", DisplayText(displays, await Ready(displays, key, "retry")));
  }

  [Fact]
  public async Task OversizedVariableTextAndMetadataAreAccountedAndNotRetained() {
    var key = CreateKey();
    var source = CreateSource(key, "[label](https://example.test/" + new string('x', 8192) + ")");
    var index = Build(source);
    Assert.True(index.AccountedBytes > 8192 * 2);
    long bytes = AccountedBytes(key, source, index);
    await using var displays = CreateDisplays(retainedBytes: bytes - 1);
    await Assert.ThrowsAsync<DisplayTooLargeException>(() => displays.RequestAsync(key, Capture(source)));
    Assert.Equal((0, 0L, 0), displays.Usage);
    Assert.Equal("retry", DisplayText(displays, await Ready(displays, key, "retry")));
  }

  [Fact]
  public async Task CancellingOneQueuedObserverKeepsOtherInterest() {
    var key = CreateKey();
    var blockerKey = CreateKey();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var release = new ManualResetEventSlim();
    int captures = 0;
    var displays = CreateDisplays(build: source => {
      if (source.SessionId == blockerKey.SessionId) {
        entered.TrySetResult();
        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the display worker.");
      }
      return Build(source);
    });
    try {
      var blocker = Ready(displays, blockerKey, "blocker");
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
      using var cancellation = new CancellationTokenSource();
      Task<MessageDisplaySource> CaptureOnce(CancellationToken _) {
        Interlocked.Increment(ref captures);
        return Task.FromResult(CreateSource(key, "shared"));
      }
      var cancelled = displays.RequestAsync(key, CaptureOnce, cancellation.Token);
      var interested = displays.RequestAsync(key, CaptureOnce);
      await cancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
      Assert.Equal(1, displays.Usage.PendingKeys);
      release.Set();
      await blocker;
      Assert.Equal("shared", DisplayText(displays, await interested));
      Assert.Equal(1, captures);
    } finally {
      release.Set();
      await displays.DisposeAsync();
    }
  }

  [Fact]
  public async Task InvalidationRejectsRunningGenerationAndAllowsFreshRequest() {
    var key = CreateKey();
    var source = CreateSource(key, "invalidated");
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int builds = 0;
    var displays = CreateDisplays(build: message => {
      if (Interlocked.Increment(ref builds) == 1) {
        entered.TrySetResult();
        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the display worker.");
      }
      return Build(message);
    });

    try {
      Task<MessageDisplayReady> invalidated = displays.RequestAsync(key, Capture(source));
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
      displays.Invalidate(key.SessionId);
      await Assert.ThrowsAsync<DisplayUnavailableException>(async () => await invalidated);
      Task<MessageDisplayReady> retry = displays.RequestAsync(key, Capture(source));

      release.Set();
      MessageDisplayReady ready = await retry;
      Assert.Equal("invalidated", DisplayText(displays, ready));
      Assert.Equal(2, Volatile.Read(ref builds));
    } finally {
      release.Set();
      await displays.DisposeAsync();
    }
  }

  [Fact]
  public async Task ShutdownWaitsForSynchronousBuildAndDeniesNewRequests() {
    var key = CreateKey();
    var source = CreateSource(key, "shutdown");
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var displays = CreateDisplays(build: message => {
      entered.TrySetResult();
      if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the display worker.");
      return Build(message);
    });

    try {
      Task<MessageDisplayReady> request = displays.RequestAsync(key, Capture(source));
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
      Task shutdown = displays.DisposeAsync().AsTask();

      Assert.False(shutdown.IsCompleted);
      await Assert.ThrowsAsync<ObjectDisposedException>(() => displays.RequestAsync(key, Capture(source)));
      await Assert.ThrowsAsync<ObjectDisposedException>(async () => await request);
      release.Set();
      await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
    } finally {
      release.Set();
      await displays.DisposeAsync();
    }
  }

  [Fact]
  public async Task CompletedCacheDoesNotRetainCapturedSourceObject() {
    await using var displays = CreateDisplays();
    (MessageDisplayKey key, Task<MessageDisplayReady> pending, WeakReference sourceReference) =
        RequestTemporarySource(displays);
    MessageDisplayReady ready = await pending;
    var barrierKey = CreateKey();
    await Ready(displays, barrierKey, "barrier");

    for (int attempt = 0; attempt < 3 && sourceReference.IsAlive; attempt++) {
#pragma warning disable S1215 // This test explicitly verifies release of captured source objects.
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
#pragma warning restore S1215
    }

    Assert.False(sourceReference.IsAlive);
    Assert.StartsWith("retained ", DisplayText(displays, ready), StringComparison.Ordinal);
    Assert.Equal(key, ready.Key);
    GC.KeepAlive(displays);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  static (MessageDisplayKey Key, Task<MessageDisplayReady> Pending, WeakReference SourceReference)
      RequestTemporarySource(MessageDisplays displays) {
    var key = CreateKey();
    var source = CreateSource(key, "retained " + new string('x', 4096));
    return (key, displays.RequestAsync(key, _ => Task.FromResult(source)), new WeakReference(source));
  }

  static void RecordMaximum(ref int maximum, int candidate) {
    int observed;
    while (candidate > (observed = Volatile.Read(ref maximum)))
      if (Interlocked.CompareExchange(ref maximum, candidate, observed) == observed) return;
  }

  static MessageDisplays CreateDisplays(int entries = 64, long retainedBytes = 64 * 1024 * 1024,
      int pendingKeys = 32, Func<MessageDisplaySource, MessageDisplayIndex>? build = null) =>
      new(new(entries, retainedBytes, pendingKeys), build);

  static MessageDisplayKey CreateKey(long throughSegment = 1) => new(
      Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), MessageRole.Assistant, 0),
      0, throughSegment, false);

  static MessageDisplaySource CreateSource(MessageDisplayKey key, string text) => new(
      key.SessionId, key.Message,
      new(key.Message, key.FromSegment, 0), new(key.Message, key.ThroughSegment, 0),
      Encoding.UTF8.GetByteCount(text), true, key.FinalOutputCommitted, text,
      [new(0, key.FromSegment)]);

  static Func<CancellationToken, Task<MessageDisplaySource>> Capture(MessageDisplaySource source) =>
      _ => Task.FromResult(source);

  static Task<MessageDisplayReady> Ready(MessageDisplays displays, MessageDisplayKey key, string text) =>
      displays.RequestAsync(key, Capture(CreateSource(key, text)));

  static MessageDisplayIndex Build(MessageDisplaySource source) => MessageDisplayBuilder.Build(source.Source);

  static string DisplayText(MessageDisplays displays, MessageDisplayReady ready) =>
      string.Concat(displays.Read(ready, new(ready.Generation, 0)).Pieces.Select(piece => piece.Text));

  static long AccountedBytes(MessageDisplayKey key, MessageDisplaySource source, MessageDisplayIndex index) =>
      index.AccountedBytes + 512 + 2L * key.Pipeline.Length + 24 + source.Segments.Count * 40L;
}
