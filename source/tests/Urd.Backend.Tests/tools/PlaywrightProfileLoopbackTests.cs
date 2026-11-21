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

  [Fact]
  [Trait("Category", "PlaywrightProfileLoopback")]
  public async Task HandoffPreservesSyntheticSessionAcrossBrowserModes() {
    if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("URD_TEST_PLAYWRIGHT_HANDOFF") != "1")
      return;

    string executable = PinnedExecutable()
      ?? throw new InvalidOperationException("The pinned Chromium and Playwright driver must already be installed for this local test.");
    string root = Path.Combine(Path.GetTempPath(), $"urd-handoff-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try {
      await using var fixture = new SessionFixture();
      await using var denied = new SessionFixture();
      await using var network = new PlaywrightNetwork(() => 0, allowPublic: false);
      Assert.True(network.TryAllowNavigation(new Uri(fixture.Url)));
      Assert.False(network.AllowsNavigation(new Uri(denied.Url)));
      var launcher = new PlaywrightProfileLauncher(network.Address, executable, offscreen: true);
      using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
      var profile = new PlaywrightProfile(root, launcher);
      await using var lease = await profile.AcquireAsync(deadline.Token);

      Assert.Equal("unauthenticated", await VisitAsync(lease, fixture.Url + "session", deadline.Token));
      await lease.WithAutomationAsync(async (context, _) => {
        await AssertDeniedAsync(context, denied.Url);
        return true;
      }, deadline.Token);
      await SignInWithHandoffAsync(lease, fixture.Url, denied.Url, deadline.Token);
      Assert.Equal("authenticated", await VisitAsync(lease, fixture.Url + "session", deadline.Token));
      fixture.Revoke();
      Assert.Equal("unauthenticated", await VisitAsync(lease, fixture.Url + "session", deadline.Token));
      await SignInWithHandoffAsync(lease, fixture.Url, denied.Url, deadline.Token);
      Assert.Equal("authenticated", await VisitAsync(lease, fixture.Url + "session", deadline.Token));
      Assert.Equal(0, denied.RequestCount);
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  private static async Task SignInWithHandoffAsync(PlaywrightProfileLease lease, string url, string deniedUrl,
      CancellationToken cancellation) {
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var continuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<bool> handoff = lease.HandoffAsync(continuation.Task, async (context, _) => {
      await AssertDeniedAsync(context, deniedUrl);
      IPage page = await context.NewPageAsync();
      await page.GotoAsync(url + "login");
      await page.Locator("#username").FillAsync("synthetic-user");
      await page.Locator("#password").FillAsync("synthetic-password");
      await page.Locator("#submit").ClickAsync();
      await page.Locator("text=authenticated").WaitForAsync();
      ready.TrySetResult();
    }, cancellation);
    // Observe preparation failures instead of waiting for the deadline when no ready signal is sent.
    Task completed = await Task.WhenAny(ready.Task, handoff).WaitAsync(cancellation);
    if (completed == handoff)
      Assert.True(await handoff);
    Assert.False(handoff.IsCompleted);
    continuation.SetResult();
    Assert.True(await handoff.WaitAsync(cancellation));
  }

  private static async Task AssertDeniedAsync(IBrowserContext context, string url) {
    IPage page = await context.NewPageAsync();
    try {
      // Chromium reports a proxy denial as a failed navigation, not a rendered HTTP response.
      await Assert.ThrowsAsync<PlaywrightException>(() => page.GotoAsync(url));
    } finally {
      await page.CloseAsync();
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
    private int requestCount;

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
    public int RequestCount => Volatile.Read(ref requestCount);
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
        Interlocked.Increment(ref requestCount);
        string token = Volatile.Read(ref generation).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? path = request.Request.Url?.AbsolutePath;
        bool signIn = path == "/sign-in" || (path == "/authenticate"
            && request.Request.QueryString["username"] == "synthetic-user"
            && request.Request.QueryString["password"] == "synthetic-password");
        if (signIn)
          request.Response.Headers.Add("Set-Cookie", $"session={token}; Path=/; Max-Age=3600; HttpOnly; SameSite=Strict");
        bool authenticated = signIn || request.Request.Cookies["session"]?.Value == token;
        string content = authenticated ? "authenticated" : "unauthenticated";
        if (path == "/login")
          content = "<form action='/authenticate'><input id='username' name='username'><input id='password' name='password' type='password'><button id='submit'>Sign in</button></form>";
        byte[] body = Encoding.UTF8.GetBytes(content);
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
