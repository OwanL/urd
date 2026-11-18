using System.ComponentModel;

namespace Urd.Core.Context;

internal sealed class InstructionResourceCatalogWatch : IDisposable {
  private readonly List<FileSystemWatcher> watchers = [];

  internal static IDisposable? TryOpen(string directory, Action<string, string?> changed, Action lost) {
    // Recursive inotify registration walks the tree itself and cannot be told to prune /proc,
    // remote mounts, etc. Use an on-demand scan when bounded watching cannot be established.
    if (!OperatingSystem.IsWindows() && !ResourceMountPolicy.CanWatchTree(directory))
      return null;
    if (Entry(directory) != FileAttributes.Directory)
      return null;

    var watch = new InstructionResourceCatalogWatch();
    try {
      // A recursive watcher may follow the original directory when an ancestor is renamed.
      // Observe the lineage from the top down as well, so replacement at the same cwd is reconciled.
      var lineage = new Stack<string>();
      for (string? path = directory; path is not null && Path.GetDirectoryName(path) is not null;
          path = Path.GetDirectoryName(path))
        lineage.Push(path);
      foreach (string path in lineage) {
        var parent = watch.Add(Path.GetDirectoryName(path)!, Path.GetFileName(path), recursive: false, lost);
        parent.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName;
        parent.Created += (_, _) => lost();
        parent.Deleted += (_, _) => lost();
        parent.Renamed += (_, _) => lost();
        parent.EnableRaisingEvents = true;
      }

      var tree = watch.Add(directory, "*", recursive: true, lost);
      tree.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName;
      tree.Created += (_, args) => changed(args.FullPath, null);
      tree.Deleted += (_, args) => changed(args.FullPath, null);
      tree.Renamed += (_, args) => changed(args.OldFullPath, args.FullPath);
      tree.EnableRaisingEvents = true;
      return watch;
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or
        PlatformNotSupportedException or ArgumentException) {
      watch.Dispose();
      return null;
    }
  }

  private FileSystemWatcher Add(string path, string filter, bool recursive, Action lost) {
    var watcher = new FileSystemWatcher(path, filter) { IncludeSubdirectories = recursive };
    watcher.Error += (_, _) => lost();
    watchers.Add(watcher);
    return watcher;
  }

  internal static FileAttributes? Entry(string path) {
    // Check every component before statting the next one; never stat through a link or junction.
    string root = Path.GetPathRoot(path)!;
    if (OperatingSystem.IsWindows() && (root.StartsWith("\\\\", StringComparison.Ordinal) ||
        new DriveInfo(root).DriveType == DriveType.Network))
      return null;
    if (OperatingSystem.IsLinux() && (path == "/proc" || path.StartsWith("/proc/", StringComparison.Ordinal) ||
        path == "/sys" || path.StartsWith("/sys/", StringComparison.Ordinal) ||
        path == "/dev" || path.StartsWith("/dev/", StringComparison.Ordinal)))
      return null;

    var entries = new Stack<string>();
    for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
      entries.Push(current);
    try {
      while (entries.TryPop(out string? entry)) {
        FileAttributes attributes = File.GetAttributes(entry);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
          return null;
        if ((attributes & FileAttributes.Directory) == 0)
          return entries.Count == 0 ? FileAttributes.Normal : null;
      }
      return FileAttributes.Directory;
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return null;
    }
  }

  public void Dispose() {
    foreach (var watcher in watchers)
      watcher.Dispose();
  }
}
