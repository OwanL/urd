using System.Diagnostics.CodeAnalysis;

namespace Urd.Core.Concurrency;

// Async mutual exclusion over SemaphoreSlim. Each lease has one owner and is disposed once.
[SuppressMessage("Design", "CA1001", Justification =
    "Only WaitAsync/Release are used; AvailableWaitHandle is never accessed, so no native handle is allocated.")]
internal sealed class AsyncLock {
  private readonly SemaphoreSlim semaphore = new(1, 1);

  internal async ValueTask<Lease> AcquireAsync(CancellationToken cancellation = default) {
    await semaphore.WaitAsync(cancellation);
    return new Lease(semaphore);
  }

  internal readonly struct Lease(SemaphoreSlim semaphore) : IDisposable {
    public void Dispose() => semaphore.Release();
  }
}
