using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Operations and handoffs share a gate, including disposal. Callbacks must not retain the context.
internal sealed class PlaywrightProfileLease(PlaywrightProfile owner, IPlaywrightProfileLauncher launcher, string profilePath) : IAsyncDisposable {
  private readonly SemaphoreSlim gate = new(1, 1);
  private readonly CancellationTokenSource lifetime = new();
  private IPlaywrightProfileBrowser? browser;
  private Exception? failure;
  private bool handoffEnded;
  private int disposing;

  public async Task<T> WithAutomationAsync<T>(Func<IBrowserContext, CancellationToken, Task<T>> operation, CancellationToken cancellation) {
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
    await gate.WaitAsync(linked.Token);
    try {
      ThrowIfUnavailable();
      browser ??= await LaunchAsync(true, linked.Token);
      return await operation(browser.Context, linked.Token);
    } finally {
      gate.Release();
    }
  }

  // The caller signals Continue by completing continueSignal. Window closure never resumes automation.
  public Task<bool> HandoffAsync(Task continueSignal, CancellationToken cancellation) =>
      HandoffAsync(continueSignal, static (_, _) => Task.CompletedTask, cancellation);

  internal async Task<bool> HandoffAsync(Task continueSignal, Func<IBrowserContext, CancellationToken, Task> prepareWindow,
      CancellationToken cancellation) {
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
    await gate.WaitAsync(linked.Token);
    try {
      ThrowIfUnavailable();
      bool continued = false;
      try {
        await CloseAsync();
        browser = await LaunchAsync(false, linked.Token);
        try {
          await prepareWindow(browser.Context, linked.Token);
        } catch (PlaywrightException) when (browser.Closed.IsCompleted) {
          return false;
        }
        Task completed = await Task.WhenAny(continueSignal, browser.Closed).WaitAsync(linked.Token);
        if (completed == browser.Closed)
          return false;
        await continueSignal;
        linked.Token.ThrowIfCancellationRequested();
        continued = !browser.Closed.IsCompleted;
        return continued;
      } finally {
        if (!continued)
          handoffEnded = true;
        await CloseAsync();
      }
    } finally {
      gate.Release();
    }
  }

  public async ValueTask DisposeAsync() {
    ObjectDisposedException.ThrowIf(Interlocked.Exchange(ref disposing, 1) != 0, this);
    await lifetime.CancelAsync();
    await gate.WaitAsync(CancellationToken.None);
    try {
      try {
        await CloseAsync();
      } finally {
        owner.Release(failure);
      }
    } finally {
      gate.Release();
      lifetime.Dispose();
    }
  }

  private void ThrowIfUnavailable() {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref disposing) != 0, this);
    if (failure is not null)
      throw new InvalidOperationException("The browser profile is unavailable after a lifecycle failure.", failure);
    if (handoffEnded)
      throw new InvalidOperationException("The handoff ended without Continue; release the lease.");
  }

  private async Task<IPlaywrightProfileBrowser> LaunchAsync(bool headless, CancellationToken cancellation) {
    try {
      return await launcher.LaunchAsync(profilePath, headless, cancellation);
    } catch (PlaywrightProfileLaunchException error) {
      failure = error;
      throw;
    }
  }

  private async Task CloseAsync() {
    if (browser is null)
      return;
    try {
      await browser.CloseAsync();
      browser = null;
    } catch (Exception error) {
      failure = error;
      browser = null;
      throw;
    }
  }
}
