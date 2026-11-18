using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

internal sealed class WebBrowserInstance(IPlaywright driver, IBrowser browser, X509Certificate2 certificate, Socket denyProxy)
    : IAsyncDisposable {
  private int unusable;

  public IBrowser Browser => browser;
  public X509Certificate2 Certificate => certificate;
  public bool IsReusable => Volatile.Read(ref unusable) == 0 && browser.IsConnected;

  public void MarkUnusable() => Volatile.Write(ref unusable, 1);

  public static async Task<WebBrowserInstance> StartAsync(string cache, IProgress<string>? progress, CancellationToken cancellation) {
    string executable = await WebBrowserInstall.EnsureAsync(cache, progress, cancellation);
    cancellation.ThrowIfCancellationRequested();
    X509Certificate2 certificate = WebBrowserCertificate.Create();
    Socket? denyProxy = null;
    IPlaywright? driver = null;
    IBrowser? browser = null;
    try {
      // Reserve a never-listening loopback port so the browser's default proxy always refuses connections.
      denyProxy = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
      denyProxy.Bind(new IPEndPoint(IPAddress.Loopback, 0));
      driver = await Playwright.CreateAsync();
      cancellation.ThrowIfCancellationRequested();
      string proxyKey = Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
      browser = await driver.Chromium.LaunchAsync(new BrowserTypeLaunchOptions {
        ExecutablePath = executable,
        ChromiumSandbox = true,
        Headless = true,
        Timeout = 12_000,
        Proxy = new Proxy { Server = WebBrowserProxyProtocol.AddressOf(denyProxy.LocalEndPoint!), Bypass = "<-loopback>" },
        Args = [
          // Contexts override the deny-by-default proxy. Don't resolve sites or allow QUIC/WebRTC UDP locally.
          "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1",
          "--disable-dns-prefetch",
          "--disable-quic",
          "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
          // Only the fresh proxy key is trusted here. The .NET upstream performs normal TLS validation.
          $"--ignore-certificate-errors-spki-list={proxyKey}"
        ]
      });
      cancellation.ThrowIfCancellationRequested();
      return new WebBrowserInstance(driver, browser, certificate, denyProxy);
    } catch {
      try {
        if (browser is not null)
          await CloseAsync(browser);
      } finally {
        driver?.Dispose();
        denyProxy?.Dispose();
        certificate.Dispose();
      }
      throw;
    }
  }

  public async ValueTask DisposeAsync() {
    try {
      await CloseAsync(browser);
    } finally {
      driver.Dispose();
      denyProxy.Dispose();
      certificate.Dispose();
    }
  }

  private static async Task CloseAsync(IBrowser browser) {
    try {
      await browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
    } catch (Exception failure) when (failure is PlaywrightException or TimeoutException) {
      // Closing the driver is the last fallback, including after a browser crash.
    }
  }
}
