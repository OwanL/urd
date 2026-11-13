namespace Urd.Server;

internal sealed record DirectoryListing(
    string? Path,
    string? ParentPath,
    IReadOnlyList<DirectoryEntry> Directories,
    bool Truncated) {
  internal const int MaximumEntries = 200;

  private static readonly Comparer<DirectoryEntry> entryComparer = Comparer<DirectoryEntry>.Create((left, right) => {
    int comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
    if (comparison != 0)
      return comparison;
    comparison = StringComparer.Ordinal.Compare(left.Name, right.Name);
    return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Path, right.Path);
  });

  internal static DirectoryListing ForPath(string path, string? filter, CancellationToken cancellationToken) {
    string fullPath = System.IO.Path.GetFullPath(path);
    cancellationToken.ThrowIfCancellationRequested();
    FileAttributes attributes = File.GetAttributes(fullPath);
    if ((attributes & FileAttributes.Directory) == 0)
      throw new DirectoryNotFoundException();

    IEnumerable<string> directories = Directory.EnumerateDirectories(
        fullPath, "*", SearchOption.TopDirectoryOnly);
    return Create(fullPath, Directory.GetParent(fullPath)?.FullName,
        directories.Select(directoryPath => new DirectoryEntry(
            System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(directoryPath)), directoryPath)),
        filter, cancellationToken);
  }

  internal static DirectoryListing Roots(string? filter, CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    if (!OperatingSystem.IsWindows()) {
      string root = System.IO.Path.GetPathRoot(Environment.CurrentDirectory)!;
      return ForPath(root, filter, cancellationToken);
    }

    IEnumerable<DirectoryEntry> drives = DriveInfo.GetDrives()
        .Select(drive => new DirectoryEntry(drive.Name, drive.Name));
    return Create(null, null, drives, filter, cancellationToken);
  }

  private static DirectoryListing Create(string? path, string? parentPath,
      IEnumerable<DirectoryEntry> directories, string? filter, CancellationToken cancellationToken) {
    var entries = new SortedSet<DirectoryEntry>(entryComparer);
    bool truncated = false;
    foreach (DirectoryEntry entry in directories) {
      cancellationToken.ThrowIfCancellationRequested();
      if (filter is not null && !entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        continue;

      if (entries.Count < MaximumEntries) {
        entries.Add(entry);
        continue;
      }

      truncated = true;
      DirectoryEntry last = entries.Max!;
      if (entryComparer.Compare(entry, last) < 0) {
        entries.Remove(last);
        entries.Add(entry);
      }
    }

    cancellationToken.ThrowIfCancellationRequested();
    return new(path, parentPath, entries.ToArray(), truncated);
  }
}

internal sealed record DirectoryEntry(string Name, string Path);
