using Urd.Core.Concurrency;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Concurrency;

public sealed class AsyncLockTests {
  [Fact]
  public async Task CancellationDoesNotReleaseTheCurrentOwnerAndDisposalAdmitsTheNextWaiter() {
    var gate = new AsyncLock();
    using var cancellation = new CancellationTokenSource();
    Task waiting;
    using (await gate.AcquireAsync()) {
      Task cancelled = AcquireAndReleaseAsync(cancellation.Token);
      waiting = AcquireAndReleaseAsync(CancellationToken.None);
      Assert.False(cancelled.IsCompleted);
      Assert.False(waiting.IsCompleted);

      await cancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TestTimeout));
      Assert.False(waiting.IsCompleted);
    }
    await waiting.WaitAsync(TestTimeout);

    async Task AcquireAndReleaseAsync(CancellationToken token) {
      using var lease = await gate.AcquireAsync(token);
    }
  }
}
