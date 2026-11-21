using Microsoft.Playwright;

namespace Urd.Core.Tools;

internal sealed class PlaywrightProfileLauncher(string proxy, string? executablePath = null, bool offscreen = false) : IPlaywrightProfileLauncher {
  private static readonly string[] HiddenWindowArgs = ["--window-position=-2400,-1200"];

  public async Task<IPlaywrightProfileBrowser> LaunchAsync(string profilePath, bool headless, CancellationToken cancellation) {
    string executable = executablePath ?? await WebBrowserInstall.EnsureAsync(WebBrowserInstall.DefaultCache, null, cancellation);
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

  private async Task<IBrowserContext> LaunchContextAsync(IPlaywright driver, string executable, string profilePath, bool headless) {
    try {
      return await driver.Chromium.LaunchPersistentContextAsync(profilePath, new BrowserTypeLaunchPersistentContextOptions {
        ExecutablePath = executable,
        ChromiumSandbox = true,
        Headless = headless,
        Timeout = 12_000,
        AcceptDownloads = false,
        ViewportSize = headless ? null : ViewportSize.NoViewport,
        Proxy = new Proxy { Server = proxy, Bypass = "<-loopback>" },
        Args = [
          "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1",
          "--disable-dns-prefetch",
          "--disable-quic",
          "--disable-background-networking",
          "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
          .. (offscreen && !headless ? HiddenWindowArgs : Array.Empty<string>())
        ]
      });
    } catch (Exception error) {
      throw new PlaywrightProfileLaunchException(error);
    }
  }
}
