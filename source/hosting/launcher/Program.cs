using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Urd.Launcher;

internal static class Program {
  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

  private static int Main(string[] args) {
    bool noOpen = args.Contains("--no-open", StringComparer.Ordinal);
    try {
      if (!OperatingSystem.IsWindows())
        throw new InvalidOperationException("This launcher requires Windows.");
      if (args.Any(arg => arg is not "--no-open" and not "--no-tray" and not "--staged") || args.Distinct().Count() != args.Length)
        throw new ArgumentException("Usage: Start.exe [--no-open] [--no-tray] [--staged]");

      string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
      string custom = Path.Combine(root, "custom", "app", "Urd.Server.exe");
      string executable = args.Contains("--staged", StringComparer.Ordinal) || File.Exists(custom)
          ? custom : Path.Combine(root, "app", "Urd.Server.exe");
      string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
      string pipeName = "urd-" + key;
      using var mutex = new Mutex(false, @"Local\urd-launch-" + key);
      bool acquired = false;
      try {
        try {
          acquired = mutex.WaitOne(TimeSpan.FromSeconds(75));
        } catch (AbandonedMutexException) {
          acquired = true;
        }
        if (!acquired)
          throw new InvalidOperationException("Another Urd launch did not finish in time.");

        Uri? origin = ReadOrigin(pipeName, executable, 200);
        if (origin is null) {
          if (!File.Exists(executable))
            throw new FileNotFoundException("Urd server was not found. Check that the app is extracted and built.", executable);
          string? portableBash = HasGitBash() ? null : PortableGit.FindOrInstall(Path.Combine(
              Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Urd", "tools"));
          var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, WorkingDirectory = root, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true
          };
          start.ArgumentList.Add("--data-root");
          start.ArgumentList.Add(Path.Combine(root, "data"));
          start.ArgumentList.Add("--port");
          start.ArgumentList.Add("0");
          start.ArgumentList.Add("--launcher-pipe");
          start.ArgumentList.Add(pipeName);
          if (portableBash is not null) {
            start.ArgumentList.Add("--git-bash");
            start.ArgumentList.Add(portableBash);
          }
          if (args.Contains("--no-tray", StringComparer.Ordinal))
            start.ArgumentList.Add("--no-tray");
          using Process server = Process.Start(start) ?? throw new InvalidOperationException("Could not start Urd server.");
          // The detached server must not hold the launcher's caller's stdio open.
          Task<string> startupError = server.StandardError.ReadToEndAsync();
          _ = server.StandardOutput.ReadToEndAsync();
          server.StandardInput.Close();
          var timer = Stopwatch.StartNew();
          while (origin is null && timer.Elapsed < TimeSpan.FromSeconds(60)) {
            if (server.HasExited) {
              string error = startupError.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult().Trim();
              string detail = error.Length == 0 ? "Check server.log in the data folder." : error.Split('\n')[0].Trim();
              throw new InvalidOperationException($"Urd server exited before it was ready (exit code {server.ExitCode}). {detail}");
            }
            origin = ReadOrigin(pipeName, executable, 300);
          }
          if (origin is null)
            throw new TimeoutException("Urd server did not become ready. Check the server log in the data folder; it may still be starting.");
        }
        if (noOpen)
          Console.WriteLine(origin.GetLeftPart(UriPartial.Authority));
        else
          Process.Start(new ProcessStartInfo(origin.AbsoluteUri) { UseShellExecute = true });
        return 0;
      } finally {
        if (acquired)
          mutex.ReleaseMutex();
      }
    } catch (Exception failure) {
      string message = $"Could not start Urd: {failure.Message}";
      Console.Error.WriteLine(message);
      if (!noOpen && OperatingSystem.IsWindows())
        _ = MessageBoxW(IntPtr.Zero, message, "Urd", 0x10);
      return 1;
    }
  }

  private static Uri? ReadOrigin(string name, string executable, int timeout) {
    using var pipe = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.CurrentUserOnly);
    try {
      pipe.Connect(timeout);
    } catch (TimeoutException) {
      return null;
    }
    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint id))
      throw new InvalidOperationException("Could not identify the running Urd server.");
    using Process process = Process.GetProcessById(checked((int)id));
    string? image = process.MainModule?.FileName;
    if (!string.Equals(image, executable, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Another app generation is running for this copy. Stop it before switching builds.");
    using var reader = new StreamReader(pipe, Encoding.UTF8);
    string line = reader.ReadLine() ?? throw new InvalidOperationException("The Urd server returned no address.");
    using JsonDocument response = JsonDocument.Parse(line);
    string? reportedImage = response.RootElement.GetProperty("executable").GetString();
    string? text = response.RootElement.GetProperty("origin").GetString();
    if (!string.Equals(reportedImage, executable, StringComparison.OrdinalIgnoreCase) ||
        !Uri.TryCreate(text, UriKind.Absolute, out Uri? origin) || origin.Scheme != Uri.UriSchemeHttp ||
        origin.Host != "127.0.0.1" || origin.Port is < 1 or > 65535 || origin.UserInfo.Length != 0 ||
        origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
      throw new InvalidOperationException("The Urd server returned an invalid address or executable.");
    return origin;
  }

  private static bool HasGitBash() {
    string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
      Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")];
    if (roots.Where(root => !string.IsNullOrEmpty(root)).Any(root => File.Exists(Path.Combine(root, "Git", "bin", "bash.exe"))))
      return true;
    foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
      string directory = entry.Trim().Trim('"');
      if (directory.Length != 0 && File.Exists(Path.Combine(directory, "git.exe")) &&
          (File.Exists(Path.Combine(directory, "..", "bin", "bash.exe")) || File.Exists(Path.Combine(directory, "bash.exe"))))
        return true;
    }
    return false;
  }
}
