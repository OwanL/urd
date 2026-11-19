using System.Diagnostics;
using System.Security.Cryptography;

namespace Urd.Launcher;

internal static class PortableGit {
  private const string Version = "2.56.1";
  private const string Sha256 = "9dd04a608ceb5f6ab32a73709e0ecb159d211da71b697c45ff81f6dd0c6b49c7";
  // The upstream location is pinned together with the asset checksum for this trial.
#pragma warning disable S1075
  private const string DownloadUrl = "https://github.com/git-for-windows/git/releases/download/v2.56.1.windows.1/PortableGit-2.56.1-64-bit.7z.exe";
#pragma warning restore S1075
  private static readonly byte[] expectedHash = Convert.FromHexString(Sha256);

  internal static string FindOrInstall(string cacheRoot) {
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("Portable Git is available only on Windows.");
    ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);

    string root = Path.GetFullPath(cacheRoot);
    Directory.CreateDirectory(root);
    string installPath = Path.Combine(root, $"PortableGit-{Version}-64-bit");
    string bashPath = Path.Combine(installPath, "bin", "bash.exe");
    if (File.Exists(bashPath))
      return bashPath;
    if (Directory.Exists(installPath))
      throw new InvalidDataException("The cached portable Git installation is incomplete.");

    string temporaryPath = Path.Combine(root, ".portable-git-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryPath);
    try {
      Console.WriteLine("Installing portable Git...");
      string downloadPath = Path.Combine(temporaryPath, "PortableGit.7z.exe");
      DownloadAndVerify(downloadPath);

      // The SFX defaults to PortableGit beside its executable. Its custom path argument
      // can silently select the wrong destination when the cache path contains spaces.
      string extractedPath = Path.Combine(temporaryPath, "PortableGit");
      Extract(downloadPath);
      string extractedBashPath = Path.Combine(extractedPath, "bin", "bash.exe");
      if (!File.Exists(extractedBashPath))
        throw new InvalidDataException("Portable Git extraction did not produce bin/bash.exe.");

      try {
        Directory.Move(extractedPath, installPath);
      } catch (IOException) when (File.Exists(bashPath)) {
        // Another launcher completed the atomic publish while this copy was extracting.
      }

      if (!File.Exists(bashPath))
        throw new InvalidDataException("Portable Git could not be published to the cache.");
      Console.WriteLine("Portable Git is ready.");
      return bashPath;
    } finally {
      if (Directory.Exists(temporaryPath))
        Directory.Delete(temporaryPath, recursive: true);
    }
  }

  private static void DownloadAndVerify(string downloadPath) {
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl);
    using (HttpResponseMessage response = client.Send(request, HttpCompletionOption.ResponseHeadersRead)) {
      response.EnsureSuccessStatusCode();
      using Stream content = response.Content.ReadAsStream();
      using var download = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
      content.CopyTo(download);
    }

    using var downloadedFile = File.OpenRead(downloadPath);
    byte[] actualHash = SHA256.HashData(downloadedFile);
    if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
      throw new InvalidDataException("The downloaded portable Git archive failed SHA-256 verification.");
  }

  private static void Extract(string downloadPath) {
    var start = new ProcessStartInfo(downloadPath) {
      UseShellExecute = false,
      CreateNoWindow = true,
      WindowStyle = ProcessWindowStyle.Hidden,
      WorkingDirectory = Path.GetDirectoryName(downloadPath)!
    };
    start.ArgumentList.Add("-y");
    start.ArgumentList.Add("-gm2");

    using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start portable Git extraction.");
    if (!process.WaitForExit(TimeSpan.FromMinutes(10))) {
      try {
        if (!process.HasExited)
          process.Kill(entireProcessTree: true);
      } catch (InvalidOperationException) when (process.HasExited) {
        // The extractor can finish between the exit check and Kill.
      }
      process.WaitForExit();
      throw new TimeoutException("Portable Git extraction timed out.");
    }
    if (process.ExitCode != 0)
      throw new InvalidOperationException($"Portable Git extraction failed (exit code {process.ExitCode}).");
  }
}
