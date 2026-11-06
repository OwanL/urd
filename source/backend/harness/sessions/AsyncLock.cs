namespace Urd.Core.Sessions;

// Async mutual exclusion; dispose the returned handle to release it.
internal sealed class AsyncLock {
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal async Task<Handle> AcquireAsync(CancellationToken cancellation = default) {
        await _semaphore.WaitAsync(cancellation);
        return new Handle(_semaphore);
    }

    internal readonly struct Handle(SemaphoreSlim semaphore) : IDisposable {
        public void Dispose() => semaphore.Release();
    }
}
