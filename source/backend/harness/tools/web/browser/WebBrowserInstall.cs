using System.Diagnostics;
using System.Text.Json;

namespace Urd.Core.Tools;

internal static class WebBrowserInstall {
  // Progress report contract shared with consumers that surface first-use provisioning.
  internal const string DownloadStarted = "Downloading Chromium.";
  internal const string DownloadFinished = "Chromium is ready.";
  internal const string SetupFailed = "Chromium setup failed.";

  public static string DefaultCache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "Urd", "browser");

  public static async Task<string> EnsureAsync(string cache, IProgress<string>? progress, CancellationToken cancellation) {
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("Web tools currently require Windows.");

    string driver = Path.Combine(AppContext.BaseDirectory, ".playwright");
    string package = Path.Combine(driver, "package");
    string node = Path.Combine(driver, "node", "win32_x64", "node.exe");
    if (!File.Exists(node))
      throw new FileNotFoundException("Urd's packaged Playwright driver is missing.", node);

    await using FileStream manifest = File.OpenRead(Path.Combine(package, "browsers.json"));
    using JsonDocument catalog = await JsonDocument.ParseAsync(manifest, cancellationToken: cancellation);
    string revision = catalog.RootElement.GetProperty("browsers").EnumerateArray()
        .First(browser => browser.GetProperty("name").GetString() == "chromium").GetProperty("revision").GetString()!;
    string final = Path.Combine(cache, $"playwright-win64-{revision}");
    string executable = BrowserExecutable(final, revision);
    if (File.Exists(executable))
      return executable;
    if (Directory.Exists(final))
      throw new IOException($"Managed Chromium cache is incomplete. Remove '{final}' and retry.");

    Directory.CreateDirectory(cache);
    string staging = Path.Combine(cache, $"install-{Guid.NewGuid():N}");
    try {
      progress?.Report(DownloadStarted);
      await DownloadAsync(node, package, staging, cancellation);
      cancellation.ThrowIfCancellationRequested();
      if (!File.Exists(BrowserExecutable(staging, revision)))
        throw new IOException("Playwright did not install the pinned Chromium executable.");
      try {
        Directory.Move(staging, final);
      } catch (IOException) when (File.Exists(executable)) {
        // Another backend published the same build first.
      }
      progress?.Report(DownloadFinished);
      return executable;
    } finally {
      if (Directory.Exists(staging))
        Directory.Delete(staging, recursive: true);
    }
  }

  private static string BrowserExecutable(string directory, string revision) =>
      Path.Combine(directory, $"chromium-{revision}", "chrome-win64", "chrome.exe");

  private static async Task DownloadAsync(string node, string package, string staging, CancellationToken cancellation) {
    var info = new ProcessStartInfo(node) {
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true
    };
    foreach (string argument in new[] { Path.Combine(package, "cli.js"), "install", "chromium", "--no-shell" })
      info.ArgumentList.Add(argument);
    info.Environment["PLAYWRIGHT_BROWSERS_PATH"] = staging;
    info.Environment.Remove("PLAYWRIGHT_DOWNLOAD_HOST");
    info.Environment.Remove("PLAYWRIGHT_CHROMIUM_DOWNLOAD_HOST");
    info.Environment.Remove("PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD");
    info.Environment["CI"] = "1";
    cancellation.ThrowIfCancellationRequested();
    using Process process = Process.Start(info) ?? throw new IOException("Could not start the Chromium installer.");
    Task output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
    Task errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
    try {
      await process.WaitForExitAsync(cancellation);
    } catch (OperationCanceledException) {
      if (!process.HasExited) {
        try {
          process.Kill(entireProcessTree: true);
        } catch (InvalidOperationException) {
          // The installer exited before the kill arrived.
        }
      }
      await process.WaitForExitAsync(CancellationToken.None);
      throw;
    } finally {
      await Task.WhenAll(output, errors);
    }
    if (process.ExitCode != 0)
      throw new IOException($"Playwright could not install Chromium (exit code {process.ExitCode}).");
  }
}
