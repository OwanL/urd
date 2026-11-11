namespace Urd.Core.Tools;

// Shared I/O paths and queue identities, including existing parents of new files.
internal static class ToolPaths {
  public static string Resolve(string path, string workingDirectory) {
    string fullPath = ResolveLexically(path, workingDirectory);
    string root = Path.GetPathRoot(fullPath)!;
    string resolved = root;
    foreach (string part in fullPath[root.Length..].Split(
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)) {
      string candidate = Path.Combine(resolved, part);
      FileSystemInfo entry = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
      resolved = entry.Exists ? entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate : candidate;
    }
    return resolved;
  }

  internal static string ResolveLexically(string path, string workingDirectory) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
    return Path.GetFullPath(WindowsDrivePath(path),
      Path.GetFullPath(WindowsDrivePath(workingDirectory)));
  }

  private static string WindowsDrivePath(string path) {
    if (!OperatingSystem.IsWindows() || path.Length < 2 || path[0] != '/' || !char.IsAsciiLetter(path[1]))
      return path;
    if (path.Length == 2)
      return $"{path[1]}:\\";
    return path[2] == '/' ? $"{path[1]}:\\{path[3..]}" : path;
  }
}
