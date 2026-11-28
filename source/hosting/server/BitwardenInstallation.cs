using System.IO.Compression;
using System.Security.Cryptography;

namespace Urd.Server;

// The code belongs to Urd's installation; Bitwarden's account state stays in the Chromium profile.
internal sealed class BitwardenInstallation : IDisposable {
  private const string Version = "2026.11.0";
  private const string ArchiveSha256 = "07cf19b8f79e649005fdeee67c85d3cf31c735449a01a37a5b9f0287732605a5";
  private const int MaxArchiveBytes = 48 * 1024 * 1024;
  private static readonly Uri Release = new(
      $"https://github.com/bitwarden/clients/releases/download/browser-v{Version}/bit-dist-chrome-{Version}.zip");
  private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
  private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);

  private readonly SemaphoreSlim changes = new(1, 1);
  private readonly string directory;
  private readonly string extension;
  private readonly string enabledFile;
  private readonly string disabledFile;

  internal BitwardenInstallation(string dataRoot) {
    directory = Path.Combine(Path.GetFullPath(dataRoot), "bitwarden");
    // Chromium gives unpacked extensions a path-based ID. Keep this path stable across updates.
    extension = Path.Combine(directory, "extension");
    enabledFile = Path.Combine(directory, "enabled");
    disabledFile = Path.Combine(directory, "disabled");
    (bool enabled, _, bool installed) = Status();
    if (enabled && installed)
      ActiveExtensionPath = extension;
  }

  internal string? ActiveExtensionPath { get; private set; }

  internal (bool Enabled, bool Active, bool Installed) Status() {
    bool installed = IsInstalled();
    // Older builds removed the enabled marker on opt-out, but kept the installed extension.
    bool enabled = !File.Exists(disabledFile) && (!installed || File.Exists(enabledFile));
    return (enabled, ActiveExtensionPath is not null, installed);
  }

  // Prepare once, before the browser host captures the path. Offline startup must still work.
  internal async Task InitializeAsync(CancellationToken cancellation) {
    if (!Status().Enabled || ActiveExtensionPath is not null)
      return;
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    deadline.CancelAfter(StartupTimeout);
    try {
      await ChangeAsync(true, deadline.Token);
      ActiveExtensionPath = extension;
    } catch (Exception error) when (!cancellation.IsCancellationRequested && IsInstallFailure(error)) {
      await Console.Error.WriteLineAsync($"Could not install Bitwarden during startup: {error.Message}");
    }
  }

  internal static bool IsInstallFailure(Exception error) =>
      error is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException;

  internal async Task ChangeAsync(bool enabled, CancellationToken cancellation) {
    await changes.WaitAsync(cancellation);
    try {
      cancellation.ThrowIfCancellationRequested();
      if (!enabled) {
        Directory.CreateDirectory(directory);
        WriteMarker(disabledFile);
        return;
      }
      if (extension.Contains(',', StringComparison.Ordinal))
        throw new IOException("Chromium cannot load an extension from a path containing a comma.");
      Directory.CreateDirectory(directory);
      // Record the intent before installing so an interrupted install isn't treated as an old opt-out.
      WriteMarker(enabledFile);
      if (!IsInstalled())
        await InstallAsync(cancellation);
      cancellation.ThrowIfCancellationRequested();
      File.Delete(disabledFile);
    } finally {
      changes.Release();
    }
  }

  private static void WriteMarker(string path) {
    using FileStream marker = File.Open(path, FileMode.OpenOrCreate, FileAccess.Write);
  }

  private bool IsInstalled() => File.Exists(Path.Combine(extension, "manifest.json")) &&
      File.Exists(Path.Combine(extension, "background.js")) && File.Exists(Path.Combine(extension, "popup", "index.html"));

  private async Task InstallAsync(CancellationToken cancellation) {
    if (Directory.Exists(extension))
      throw new IOException("The installed Bitwarden extension is incomplete.");
    string staging = Path.Combine(directory, $"install-{Guid.NewGuid():N}");
    Directory.CreateDirectory(staging);
    try {
      string archive = Path.Combine(staging, "bitwarden.zip");
      await DownloadAsync(archive, cancellation);
      await using (FileStream saved = File.OpenRead(archive)) {
        byte[] hash = await SHA256.HashDataAsync(saved, cancellation);
        if (!string.Equals(Convert.ToHexString(hash), ArchiveSha256, StringComparison.OrdinalIgnoreCase))
          throw new InvalidDataException("The Bitwarden release did not match its pinned checksum.");
      }
      string extracted = Path.Combine(staging, "extension");
      await ZipFile.ExtractToDirectoryAsync(archive, extracted, cancellationToken: cancellation);
      cancellation.ThrowIfCancellationRequested();
      Directory.Move(extracted, extension);
    } finally {
      Directory.Delete(staging, recursive: true);
    }
  }

  private static async Task DownloadAsync(string archive, CancellationToken cancellation) {
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    deadline.CancelAfter(TimeSpan.FromSeconds(60));
    using HttpResponseMessage response = await Http.GetAsync(Release, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength > MaxArchiveBytes)
      throw new InvalidDataException("The Bitwarden download was too large.");
    await using Stream source = await response.Content.ReadAsStreamAsync(deadline.Token);
    await using FileStream target = File.Create(archive);
    byte[] buffer = new byte[64 * 1024];
    int total = 0;
    int read;
    while ((read = await source.ReadAsync(buffer.AsMemory(), deadline.Token)) > 0) {
      total += read;
      if (total > MaxArchiveBytes)
        throw new InvalidDataException("The Bitwarden download was too large.");
      await target.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
    }
  }

  public void Dispose() => changes.Dispose();
}
