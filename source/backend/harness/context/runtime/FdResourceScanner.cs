using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Urd.Core.Context;

internal sealed class FdResourceScanner {
  private readonly string baseDirectory = AppContext.BaseDirectory;
  private const string Pattern = "^(AGENTS|APPEND[_-]SYSTEM|AGENT[_-]INSTRUCTIONS?|INSTRUCTIONS[_-]FOR[_-]AGENTS|SKILL)\\.md$";
  private static readonly HashSet<string> ResourceNames = new(StringComparer.OrdinalIgnoreCase) {
    "AGENTS.md", "APPEND_SYSTEM.md", "APPEND-SYSTEM.md", "AGENT_INSTRUCTIONS.md",
    "AGENT-INSTRUCTIONS.md", "AGENT_INSTRUCTION.md", "AGENT-INSTRUCTION.md",
    "INSTRUCTIONS_FOR_AGENTS.md", "INSTRUCTIONS-FOR-AGENTS.md", "SKILL.md"
  };

  public static bool IsResourcePath(string path) => ResourceNames.Contains(Path.GetFileName(path));

  private static string EscapeGlob(string path) {
    var result = new StringBuilder(path.Length);
    foreach (char character in path) {
      if (character is '*' or '?' or '[' or ']' or '\\' or '{' or '}' or '!' or '#')
        result.Append('\\');
      result.Append(character);
    }
    return result.ToString();
  }

  public async Task<string[]> FindPathsAsync(string directory, CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    var root = Path.GetFullPath(directory);
    if (!Directory.Exists(root))
      throw new DirectoryNotFoundException(root);
    for (string? path = root; path is not null; path = Path.GetDirectoryName(path)) {
      if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        throw new IOException("The scan directory and its ancestors must not be symbolic links or junctions.");
    }

    ResourceMountPolicy.Scan[] scans;
    if (OperatingSystem.IsLinux()) {
      scans = ResourceMountPolicy.Plan(root, await File.ReadAllTextAsync("/proc/self/mountinfo", cancellation));
    } else if (OperatingSystem.IsMacOS()) {
      scans = ResourceMountPolicy.PlanMac(root, ResourceMountPolicy.ReadMacMounts());
    } else if (OperatingSystem.IsWindows()) {
      scans = [new(root, [])];
    } else {
      throw new PlatformNotSupportedException("Safe mount traversal is not available on this platform.");
    }

    var paths = new List<string>();
    foreach (var scan in scans)
      paths.AddRange(await ScanAsync(scan, cancellation));
    return paths.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
  }

  private async Task<string[]> ScanAsync(ResourceMountPolicy.Scan scan, CancellationToken cancellation) {
    var platform = OperatingSystem.IsWindows() ? "win" : "linux";
    if (OperatingSystem.IsMacOS())
      platform = "osx";
    var architecture = RuntimeInformation.ProcessArchitecture switch {
      Architecture.X64 => "x64",
      Architecture.Arm64 => "arm64",
      _ => throw new PlatformNotSupportedException()
    };
    var executable = Path.Combine(baseDirectory, "fd", $"{platform}-{architecture}", OperatingSystem.IsWindows() ? "fd.exe" : "fd");
    var start = new ProcessStartInfo(executable) {
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
      CreateNoWindow = true
    };
    foreach (var argument in new[] { "--absolute-path", "--hidden", "--no-ignore", "--type", "file", "--print0", "--ignore-case", "--threads", "4" })
      start.ArgumentList.Add(argument);
    if (!OperatingSystem.IsWindows()) {
      start.ArgumentList.Add("--one-file-system");
      foreach (string excluded in scan.Excludes) {
        start.ArgumentList.Add("--exclude");
        start.ArgumentList.Add(EscapeGlob(excluded));
      }
    }
    foreach (string argument in new[] { "--", Pattern, scan.Root })
      start.ArgumentList.Add(argument);

    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the bundled fd scanner.");
    using var registration = cancellation.Register(() => {
      try {
        if (!process.HasExited)
          process.Kill(entireProcessTree: true);
      } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) {
        // The process may have already exited.
      }
    });
    var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
    var paths = new List<string>();
    var buffer = new char[4096];
    var pending = new StringBuilder();
    try {
      int count;
      while ((count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), CancellationToken.None)) != 0) {
        for (var i = 0; i < count; i++) {
          if (buffer[i] == '\0') {
            paths.Add(Path.GetFullPath(pending.ToString()));
            pending.Clear();
          } else {
            pending.Append(buffer[i]);
          }
        }
      }
    } finally {
      await process.WaitForExitAsync(CancellationToken.None);
    }
    var error = await errors;
    cancellation.ThrowIfCancellationRequested();
    if (process.ExitCode != 0)
      throw new IOException($"The bundled fd scanner failed (exit code {process.ExitCode}): {error.Trim()}");
    if (pending.Length != 0)
      throw new IOException("The bundled fd scanner returned an incomplete path.");
    return paths.ToArray();
  }
}
