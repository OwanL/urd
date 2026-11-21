using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class PlaywrightProfileLoopbackTests {
  [Fact]
  [Trait("Category", "PlaywrightProfileLoopback")]
  public async Task PersistentProfileRetainsAndRestoresSyntheticSession() {
    if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("URD_TEST_PLAYWRIGHT_PROFILE") != "1")
      return;

    string executable = PinnedExecutable()
      ?? throw new InvalidOperationException("The pinned Chromium and Playwright driver must already be installed for this local test.");

    string root = Path.Combine(Path.GetTempPath(), $"urd-profile-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try {
      await using var fixture = new SessionFixture();
      var launcher = new LoopbackLauncher(executable);
      using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
      var first = new PlaywrightProfile(root, launcher);
      await using (var lease = await first.AcquireAsync(deadline.Token)) {
        Assert.Equal("authenticated", await VisitAsync(lease, fixture.Url + "sign-in", deadline.Token));
      }

      var second = new PlaywrightProfile(root, launcher);
      await using (var lease = await second.AcquireAsync(deadline.Token)) {
        Assert.Equal("authenticated", await VisitAsync(lease, fixture.Url + "session", deadline.Token));
        fixture.Revoke();
        Assert.Equal("unauthenticated", await VisitAsync(lease, fixture.Url + "session", deadline.Token));
        Assert.Equal("authenticated", await VisitAsync(lease, fixture.Url + "sign-in", deadline.Token));
        Assert.Equal("authenticated", await VisitAsync(lease, fixture.Url + "session", deadline.Token));
      }
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  private static string? PinnedExecutable() {
    string driver = Path.Combine(AppContext.BaseDirectory, ".playwright");
    string manifest = Path.Combine(driver, "package", "browsers.json");
    if (!File.Exists(manifest) || !File.Exists(Path.Combine(driver, "node", "win32_x64", "node.exe")))
      return null;
    using JsonDocument catalog = JsonDocument.Parse(File.ReadAllText(manifest));
    string? revision = catalog.RootElement.GetProperty("browsers").EnumerateArray()
        .First(browser => browser.GetProperty("name").GetString() == "chromium").GetProperty("revision").GetString();
    if (string.IsNullOrEmpty(revision))
      return null;
    string executable = Path.Combine(WebBrowserInstall.DefaultCache, $"playwright-win64-{revision}", $"chromium-{revision}", "chrome-win64", "chrome.exe");
    return File.Exists(executable) ? executable : null;
  }

  private static Task<string> VisitAsync(PlaywrightProfileLease lease, string url, CancellationToken cancellation) =>
      lease.WithAutomationAsync(async (context, _) => {
        IPage page = await context.NewPageAsync();
        try {
          await page.GotoAsync(url);
          return await page.Locator("body").InnerTextAsync();
        } finally {
          await page.CloseAsync();
        }
      }, cancellation);

  private sealed class LoopbackLauncher(string executable) : IPlaywrightProfileLauncher {
    public async Task<IPlaywrightProfileBrowser> LaunchAsync(string profilePath, bool headless, CancellationToken cancellation) {
      Assert.True(headless);
      cancellation.ThrowIfCancellationRequested();
      IPlaywright driver = await Playwright.CreateAsync();
      try {
        IBrowserContext context = await driver.Chromium.LaunchPersistentContextAsync(profilePath, new BrowserTypeLaunchPersistentContextOptions {
          ExecutablePath = executable,
          Headless = true,
          ChromiumSandbox = true,
          Timeout = 12_000,
          Proxy = new Proxy { Server = "http://127.0.0.1:9", Bypass = "127.0.0.1" },
          Args = ["--disable-quic", "--disable-background-networking", "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1"]
        });
        return new PlaywrightProfileBrowser(driver, context);
      } catch {
        driver.Dispose();
        throw;
      }
    }
  }

  private sealed class SessionFixture : IAsyncDisposable {
    private readonly HttpListener listener = new();
    private readonly Task serving;
    private int generation = 1;

    public SessionFixture() {
      var socket = new TcpListener(IPAddress.Loopback, 0);
      socket.Start();
      int port = ((IPEndPoint)socket.LocalEndpoint).Port;
      socket.Stop();
      Url = $"http://127.0.0.1:{port}/";
      listener.Prefixes.Add(Url);
      listener.Start();
      serving = ServeAsync();
    }

    public string Url { get; }
    public void Revoke() => Interlocked.Increment(ref generation);

    private async Task ServeAsync() {
      while (listener.IsListening) {
        HttpListenerContext request;
        try {
          request = await listener.GetContextAsync();
        } catch (HttpListenerException) when (!listener.IsListening) {
          break;
        } catch (ObjectDisposedException) when (!listener.IsListening) {
          break;
        }
        string token = Volatile.Read(ref generation).ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool signIn = request.Request.Url?.AbsolutePath == "/sign-in";
        if (signIn)
          request.Response.Headers.Add("Set-Cookie", $"session={token}; Path=/; Max-Age=3600; HttpOnly; SameSite=Strict");
        bool authenticated = signIn || request.Request.Cookies["session"]?.Value == token;
        byte[] body = Encoding.UTF8.GetBytes(authenticated ? "authenticated" : "unauthenticated");
        request.Response.ContentType = "text/html; charset=utf-8";
        request.Response.ContentLength64 = body.Length;
        await request.Response.OutputStream.WriteAsync(body);
        request.Response.Close();
      }
    }

    public async ValueTask DisposeAsync() {
      listener.Stop();
      listener.Close();
      await serving;
    }
  }
}
