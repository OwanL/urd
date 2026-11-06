using System.Diagnostics.CodeAnalysis;

namespace Urd.Core.Sessions;

// Async mutual exclusion; dispose the returned handle to release it.
[SuppressMessage("Design", "CA1001", Justification =
    "Only WaitAsync/Release are used; AvailableWaitHandle is never accessed, so no native handle is allocated.")]
internal sealed class AsyncLock {
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal async ValueTask<Handle> AcquireAsync(CancellationToken cancellation = default) {
        await _semaphore.WaitAsync(cancellation);
        return new Handle(_semaphore);
    }

    internal readonly struct Handle(SemaphoreSlim semaphore) : IDisposable {
        public void Dispose() => semaphore.Release();
    }
}
