using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

// The one shared persistent context behind BrowserHost. It launches the provisioned Chrome for
// Testing build headlessly, at 1280x800, in the existing "playwright-profile" directory so
// logins survive. Session cookies (no expiry) are restored on launch and staged beside the
// profile on every exit, including an empty file after the user signed out.
internal sealed class PersistentContext : IAsyncDisposable {
  private static readonly JsonSerializerOptions CookieJson = new(JsonSerializerDefaults.Web);
  private const int LaunchTimeoutMilliseconds = 12_000;
  private const int CloseTimeoutMilliseconds = 5_000;

  private readonly string profilePath;
  private readonly string cookiePath;
  private readonly string userAgentPath;
  private readonly string? bitwardenExtensionPath;
  private readonly string? bitwardenExtensionId;
  private IPlaywright? driver;
  private IBrowserContext? context;
  private bool ready;
  private bool closed;

  private PersistentContext(string profilePath, string cookiePath, string? bitwardenExtensionPath) {
    this.profilePath = profilePath;
    this.cookiePath = cookiePath;
    this.bitwardenExtensionPath = bitwardenExtensionPath;
    bitwardenExtensionId = bitwardenExtensionPath is null ? null : UnpackedExtensionId(bitwardenExtensionPath);
    userAgentPath = profilePath + ".user-agent";
  }

  internal string? BitwardenUrl => bitwardenExtensionId is null ? null : $"chrome-extension://{bitwardenExtensionId}/popup/index.html";
  internal IBrowserContext Context => context ?? throw new InvalidOperationException("The browser is not running.");

  // True once the context closed or its browser disconnected; the host relaunches.
  internal bool IsClosed => closed || context?.IsClosed != false;

  internal static async Task<PersistentContext> StartAsync(string profilePath, string cookiePath, string? bitwardenExtensionPath,
      CancellationToken cancellation) {
    string executable = await WebBrowserInstall.EnsureAsync(WebBrowserInstall.DefaultCache, null, cancellation);
    cancellation.ThrowIfCancellationRequested();
    var browser = new PersistentContext(profilePath, cookiePath, bitwardenExtensionPath);
    try {
      await browser.LaunchAsync(executable, cancellation);
      return browser;
    } catch {
      await browser.DisposeAsync();
      throw;
    }
  }

  private async Task LaunchAsync(string executable, CancellationToken cancellation) {
    IPlaywright started = await Playwright.CreateAsync();
    driver = started;
    cancellation.ThrowIfCancellationRequested();
    string? browserVersion = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
    string? userAgent = await ReadUserAgentAsync(browserVersion, cancellation);
    IBrowserContext launched = await LaunchAsync(started, executable, userAgent);
    if (userAgent is null) {
      IPage probe = launched.Pages.Count > 0 ? launched.Pages[0] : await launched.NewPageAsync();
      userAgent = (await probe.EvaluateAsync<string>("navigator.userAgent"))
          .Replace("HeadlessChrome", "Chrome", StringComparison.Ordinal)
          .Replace("Headless", "", StringComparison.Ordinal);
      await launched.CloseAsync();
      await SaveUserAgentAsync(browserVersion, userAgent, cancellation);
      launched = await LaunchAsync(started, executable, userAgent);
    }
    launched.Close += (_, _) => this.closed = true;
    context = launched;
    cancellation.ThrowIfCancellationRequested();
    await RestoreSessionCookiesAsync(launched, cancellation);
    ready = true;
  }

  private Task<IBrowserContext> LaunchAsync(IPlaywright started, string executable, string? userAgent) =>
      started.Chromium.LaunchPersistentContextAsync(profilePath,
      new BrowserTypeLaunchPersistentContextOptions {
        ExecutablePath = executable,
        ChromiumSandbox = true,
        Headless = true,
        IgnoreDefaultArgs = bitwardenExtensionPath is null ? ["--enable-automation"] : ["--enable-automation", "--disable-extensions"],
        Args = bitwardenExtensionPath is null ? ["--disable-blink-features=AutomationControlled"] : [
          "--disable-blink-features=AutomationControlled",
          $"--disable-extensions-except={bitwardenExtensionPath}", $"--load-extension={bitwardenExtensionPath}"
        ],
        UserAgent = userAgent,
        ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        AcceptDownloads = true,
        Timeout = LaunchTimeoutMilliseconds
      });

  // The pinned unpacked build has no manifest key. Match Chromium's GenerateIdForPath.
  private static string UnpackedExtensionId(string path) {
    if (OperatingSystem.IsWindows() && Path.IsPathFullyQualified(path) && path[1] == ':')
      path = char.ToUpperInvariant(path[0]) + path[1..];
    byte[] hash = SHA256.HashData((OperatingSystem.IsWindows() ? Encoding.Unicode : Encoding.UTF8).GetBytes(path));
    return string.Create(32, hash, (id, bytes) => {
      for (int i = 0; i < id.Length; i += 2) {
        byte value = bytes[i / 2];
        id[i] = (char)('a' + (value >> 4));
        id[i + 1] = (char)('a' + (value & 15));
      }
    });
  }

  private async Task<string?> ReadUserAgentAsync(string? browserVersion, CancellationToken cancellation) {
    if (string.IsNullOrEmpty(browserVersion) || !File.Exists(userAgentPath))
      return null;
    try {
      string[] cached = await File.ReadAllLinesAsync(userAgentPath, cancellation);
      return cached is [var version, var userAgent] && version == browserVersion ? userAgent : null;
    } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
      return null;
    }
  }

  private async Task SaveUserAgentAsync(string? browserVersion, string userAgent, CancellationToken cancellation) {
    if (string.IsNullOrEmpty(browserVersion))
      return;
    try {
      await File.WriteAllLinesAsync(userAgentPath, [browserVersion, userAgent], cancellation);
    } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
      // A read-only cache does not prevent this launch from using the detected value.
    }
  }

  // Saves session cookies, then closes the browser and releases the driver. Best effort: a
  // crashed browser keeps the last staged file, and a launch that never became ready cannot
  // overwrite it.
  internal async Task ExitAsync() {
    IBrowserContext? live = Interlocked.Exchange(ref context, (IBrowserContext?)null);
    List<SessionCookie>? saved = null;
    if (live is not null && ready && !live.IsClosed) {
      try {
        saved = SessionCookies(await live.CookiesAsync(), bitwardenExtensionId);
      } catch (PlaywrightException) {
        // The browser may have died; the previous cookie file stays authoritative.
      }
    }
    if (saved is not null)
      StageCookies(saved);
    try {
      if (live is not null)
        await live.CloseAsync().WaitAsync(TimeSpan.FromMilliseconds(CloseTimeoutMilliseconds));
    } catch (Exception error) when (error is PlaywrightException or TimeoutException) {
      // The browser may already be gone.
    }
    closed = true;
    driver?.Dispose();
    driver = null;
  }

  public ValueTask DisposeAsync() => new(ExitAsync());

  private async Task RestoreSessionCookiesAsync(IBrowserContext launched, CancellationToken cancellation) {
    if (!File.Exists(cookiePath))
      return;
    try {
      await using FileStream stream = File.OpenRead(cookiePath);
      SessionCookie[]? saved = await JsonSerializer.DeserializeAsync<SessionCookie[]>(stream, CookieJson, cancellation);
      if (saved is not null && saved.Length > 0)
        await launched.AddCookiesAsync(saved.Select(ToCookie).ToArray());
    } catch (Exception error) when (error is JsonException or IOException) {
      // An unreadable file starts the browser without restored session cookies.
    }
  }

  // Writes the file in one step: a staged file next to the target, then an atomic rename.
  private void StageCookies(List<SessionCookie> cookies) {
    string staging = cookiePath + ".staging";
    try {
      Directory.CreateDirectory(Path.GetDirectoryName(cookiePath)!);
      File.WriteAllText(staging, JsonSerializer.Serialize(cookies, CookieJson));
      File.Move(staging, cookiePath, overwrite: true);
    } catch (Exception) {
      // Best effort; the next exit saves again.
    } finally {
      try {
        if (File.Exists(staging))
          File.Delete(staging);
      } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
        // The staging file is disposable.
      }
    }
  }

  // A cookie with no expiry holds the -1 sentinel; both are session cookies that die with the
  // browser.
  private static List<SessionCookie> SessionCookies(IReadOnlyList<BrowserContextCookiesResult> cookies, string? extensionId) => cookies
      .Where(cookie => cookie.Expires < 0 && cookie.Domain != extensionId)
      .Select(cookie => new SessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path,
          cookie.Expires, cookie.HttpOnly, cookie.Secure,
          cookie.SameSite.ToString(), cookie.PartitionKey))
      .ToList();

  private static Cookie ToCookie(SessionCookie cookie) => new() {
    Name = cookie.Name,
    Value = cookie.Value,
    Domain = cookie.Domain,
    Path = cookie.Path,
    Expires = cookie.Expires,
    HttpOnly = cookie.HttpOnly,
    Secure = cookie.Secure,
    SameSite = Enum.TryParse<SameSiteAttribute>(cookie.SameSite, out SameSiteAttribute sameSite) ? sameSite : SameSiteAttribute.Lax,
    PartitionKey = cookie.PartitionKey
  };
  // One saved session cookie: without expiry it dies with the browser and is re-added on launch.
  private sealed record SessionCookie(string Name, string Value, string? Domain, string? Path, float Expires,
      bool HttpOnly, bool Secure, string SameSite, string? PartitionKey) {
  };
}
