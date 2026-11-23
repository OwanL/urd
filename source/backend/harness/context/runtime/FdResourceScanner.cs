using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Urd.Core.Preferences;

namespace Urd.Core.Context;

internal sealed class FdResourceScanner {
  private readonly string baseDirectory = AppContext.BaseDirectory;
  private static readonly HashSet<string> instructionNames = new(SourceIgnoreRules.InstructionFilenames
      .SelectMany(name => name.Contains('_') ? new[] { name, name.Replace('_', '-') } : [name]), StringComparer.OrdinalIgnoreCase);
  private static readonly string pattern = $"^(?:{string.Join("|", instructionNames.Append("SKILL.md").Select(Regex.Escape))})$";

  // Junk directories below the working directory would otherwise dominate filename scans and
  // watcher pending-event limits: the recycle bin, package stores, build outputs and disposable
  // OS temp artifacts. A scan root at or inside one of these directories is still scanned.
  private static readonly string[] junkDirectories = ["$recycle.bin", "node_modules", "bin", "obj"];
  private static readonly string tempDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
  private static readonly StringComparison nameComparison = OperatingSystem.IsWindows()
      ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

  internal static bool IsInstructionPath(string path) => instructionNames.Contains(Path.GetFileName(path));
  internal static bool IsSkillPath(string path) => Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase);
  public static bool IsResourcePath(string path) => IsInstructionPath(path) || IsSkillPath(path);

  // fd prunes unanchored globs against every entry name below the scan root; a glob containing
  // a separator is anchored to the scan root, so a nested copy of the same path stays visible.
  internal static IReadOnlyList<string> ExclusionGlobs(string scanRoot) {
    string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scanRoot));
    var globs = new List<string>(junkDirectories.Length + 1);
    globs.AddRange(junkDirectories);
    if (IsBelow(tempDirectory, root))
      globs.Add(EscapeGlob(tempDirectory[root.Length..].TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/')));
    return globs;
  }

  // True when a scan-root event lies below a directory pruned by ExclusionGlobs.
  internal static bool IsExcluded(string path, string scanRoot) {
    string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scanRoot));
    string? tempAncestor = IsBelow(tempDirectory, root) ? tempDirectory : null;
    for (string? ancestor = path; ancestor is not null && !string.Equals(ancestor, root, nameComparison);
        ancestor = Path.GetDirectoryName(ancestor)) {
      if (junkDirectories.Contains(Path.GetFileName(ancestor), StringComparer.OrdinalIgnoreCase))
        return true;
      if (tempAncestor is not null &&
          (string.Equals(ancestor, tempAncestor, nameComparison) || IsBelow(ancestor, tempAncestor)))
        return true;
    }
    return false;
  }

  private static bool IsBelow(string path, string directory) => path.Length > directory.Length &&
      path.StartsWith(directory, nameComparison) &&
      (Path.EndsInDirectorySeparator(directory) || path[directory.Length] == Path.DirectorySeparatorChar);

  private static string EscapeGlob(string path) {
    var result = new StringBuilder(path.Length);
    foreach (char character in path) {
      if (character is '*' or '?' or '[' or ']' or '\\' or '{' or '}' or '!' or '#')
        result.Append('\\');
      result.Append(character);
    }
    return result.ToString();
  }

  // matched receives each absolute path as fd reports it, before the scan completes.
  public async Task<string[]> FindPathsAsync(string directory, CancellationToken cancellation, Action<string>? matched = null) {
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
      paths.AddRange(await ScanAsync(scan, cancellation, matched));
    return paths.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
  }

  internal async Task<string[]> ScanAsync(ResourceMountPolicy.Scan scan, CancellationToken cancellation, Action<string>? matched = null) {
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
    if (!OperatingSystem.IsWindows())
      start.ArgumentList.Add("--one-file-system");
    foreach (string excluded in scan.Excludes) {
      start.ArgumentList.Add("--exclude");
      // A leading slash anchors fd's glob to the scan root on every OS.
      start.ArgumentList.Add('/' + EscapeGlob(excluded));
    }
    foreach (string exclusion in ExclusionGlobs(scan.Root)) {
      start.ArgumentList.Add("--exclude");
      start.ArgumentList.Add(exclusion);
    }
    foreach (string argument in new[] { "--", pattern, scan.Root })
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
            string path = Path.GetFullPath(pending.ToString());
            paths.Add(path);
            pending.Clear();
            matched?.Invoke(path);
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
