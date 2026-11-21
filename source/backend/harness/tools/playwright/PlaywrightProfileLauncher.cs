using Microsoft.Playwright;

namespace Urd.Core.Tools;

internal sealed class PlaywrightProfileLauncher : IPlaywrightProfileLauncher {
  public async Task<IPlaywrightProfileBrowser> LaunchAsync(string profilePath, bool headless, CancellationToken cancellation) {
    string executable = await WebBrowserInstall.EnsureAsync(WebBrowserInstall.DefaultCache, null, cancellation);
    cancellation.ThrowIfCancellationRequested();
    IPlaywright driver = await Playwright.CreateAsync();
    IBrowserContext context;
    try {
      cancellation.ThrowIfCancellationRequested();
      context = await LaunchContextAsync(driver, executable, profilePath, headless);
    } catch {
      driver.Dispose();
      throw;
    }

    var browser = new PlaywrightProfileBrowser(driver, context);
    if (!cancellation.IsCancellationRequested)
      return browser;

    try {
      await browser.CloseAsync();
    } catch (Exception error) {
      throw new PlaywrightProfileLaunchException(error);
    }
    throw new OperationCanceledException(cancellation);
  }

  private static async Task<IBrowserContext> LaunchContextAsync(IPlaywright driver, string executable, string profilePath, bool headless) {
    try {
      return await driver.Chromium.LaunchPersistentContextAsync(profilePath, new BrowserTypeLaunchPersistentContextOptions {
        ExecutablePath = executable,
        ChromiumSandbox = true,
        Headless = headless,
        Timeout = 12_000
      });
    } catch (Exception error) {
      throw new PlaywrightProfileLaunchException(error);
    }
  }
}
