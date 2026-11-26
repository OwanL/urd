namespace Urd.Core.Tools;

// Starts the shared browser on first use and exits it, saving cookies, when it retires.
internal sealed partial class BrowserHost {
  // Launches lazily, once; callers waiting on a shared launch each cancel only their own wait.
  private Task<PersistentContext> BeginLaunchAsync(CancellationToken cancellation) {
    Task<PersistentContext> shared;
    lock (gate) {
      ObjectDisposedException.ThrowIf(disposed, this);
      // A failed or closed launch is handed off to an exit so the next use relaunches cleanly.
      if (launch is { IsCompleted: true } completed && (!completed.IsCompletedSuccessfully || completed.Result.IsClosed)) {
        launch = null;
        exiting = ExitBrowserAsync(completed);
      }
      emptySince = null;
      if (launch is null) {
        launch = RunLaunchAsync(exiting ?? Task.CompletedTask);
        ObserveFailure(launch);
      }
      shared = launch;
    }
    return shared.WaitAsync(cancellation);
  }

  private async Task<PersistentContext> RunLaunchAsync(Task predecessor) {
    await Task.Yield();
    await predecessor;
    return await PersistentContext.StartAsync(profilePath, cookiePath, shutdown.Token);
  }

  private static async Task ExitBrowserAsync(Task<PersistentContext> pending) {
    await Task.Yield();
    PersistentContext? browser;
    try {
      browser = await pending;
    } catch (Exception) {
      return; // Nothing launched; nothing to save or close.
    }
    try {
      await browser.ExitAsync();
    } catch (Exception) {
      // Exiting is best effort; the next launch restores the last staged cookies.
    }
  }

  // Caller holds the gate. Stops the browser; the next open relaunches with restored cookies.
  private void Retire() {
    if (launch is null)
      return;
    Task<PersistentContext> pending = launch;
    launch = null;
    emptySince = null;
    exiting = ExitBrowserAsync(pending);
  }

  private static void ObserveFailure(Task pending) => _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
