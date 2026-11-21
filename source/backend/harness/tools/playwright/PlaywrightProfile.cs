using Microsoft.Playwright;

namespace Urd.Core.Tools;

// One owner must be shared by all users of a profile. It does not launch until acquired.
#pragma warning disable CA1001 // Host-lived owner; the semaphore's wait handle is never allocated.
internal sealed class PlaywrightProfile(string dataRoot, IPlaywrightProfileLauncher? launcher = null) {
  private readonly SemaphoreSlim ownership = new(1, 1);
  private readonly IPlaywrightProfileLauncher launcher = launcher ?? new PlaywrightProfileLauncher();
  private readonly string profilePath = Path.Combine(Path.GetFullPath(dataRoot), "playwright-profile");
  private Exception? failure;

  public async Task<PlaywrightProfileLease> AcquireAsync(CancellationToken cancellation) {
    await ownership.WaitAsync(cancellation);
    try {
      if (failure is not null)
        throw new InvalidOperationException("The browser profile could not be safely released; ensure the browser has exited before retrying in a new host.", failure);
      return new PlaywrightProfileLease(this, launcher, profilePath);
    } catch {
      ownership.Release();
      throw;
    }
  }

  internal void Release(Exception? error) {
    if (error is not null)
      failure = error;
    ownership.Release();
  }
}

#pragma warning restore CA1001

internal interface IPlaywrightProfileLauncher {
  Task<IPlaywrightProfileBrowser> LaunchAsync(string profilePath, bool headless, CancellationToken cancellation);
}

internal interface IPlaywrightProfileBrowser {
  IBrowserContext Context { get; }
  Task Closed { get; }
  Task CloseAsync();
}
