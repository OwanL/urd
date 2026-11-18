using System.Diagnostics;
using System.Text;

namespace Urd.Core.Context;

// Unknown filesystems and ambiguous mount points fail closed.
internal static class ResourceMountPolicy {
  private static readonly HashSet<string> localFileSystems = new(StringComparer.Ordinal) {
    "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "f2fs", "vfat", "exfat", "ntfs3", "overlay"
  };
  private static readonly HashSet<string> macLocalFileSystems = new(StringComparer.Ordinal) {
    "apfs", "hfs", "msdos", "exfat", "ntfs", "zfs"
  };

  internal sealed record Mount(string Path, bool Local);
  internal sealed record Scan(string Root, string[] Excludes);

  internal static Scan[] Plan(string root, string mountInfo) => PlanMounts(root, Parse(mountInfo));

  internal static Scan[] PlanMac(string root, string mountOutput) {
    var mounts = ParseMac(mountOutput).ToList();
    // /Users is normally an APFS firmlink into Data, not a mount table entry.
    // Scan it separately because fd's one-file-system check can stop at the firmlink.
    if (mounts.Any(mount => mount.Path.Equals("/System/Volumes/Data", StringComparison.OrdinalIgnoreCase) && mount.Local) &&
        !mounts.Any(mount => mount.Path.Equals("/Users", StringComparison.OrdinalIgnoreCase)))
      mounts.Add(new("/Users", true));
    mounts.RemoveAll(mount => mount.Path.Equals("/System/Volumes", StringComparison.OrdinalIgnoreCase));
    mounts.Add(new("/System/Volumes", false));
    return PlanMounts(root, mounts.ToArray(), mac: true);
  }

  private static Scan[] PlanMounts(string root, Mount[] mounts, bool mac = false) {
    var comparison = mac ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (mac && Contains("/System/Volumes", root, comparison))
      return [];
    var covering = mounts.Where(mount => Contains(mount.Path, root, comparison)).OrderByDescending(mount => mount.Path.Length).FirstOrDefault();
    if (covering is null || !covering.Local || mounts.Any(mount => !mount.Local && Contains(mount.Path, root, comparison)))
      return [];

    var nested = mounts.Where(mount => !mount.Path.Equals(root, comparison) && Contains(root, mount.Path, comparison)).ToArray();
    var roots = new List<string> { root };
    foreach (var mount in nested.OrderBy(mount => mount.Path.Length)) {
      if (mount.Local && !(mac && Contains("/System/Volumes", mount.Path, comparison)) &&
          !nested.Any(parent => !parent.Local && !parent.Path.Equals(mount.Path, comparison) && Contains(parent.Path, mount.Path, comparison)))
        roots.Add(mount.Path);
    }
    return roots.Select(path => new Scan(path, nested.Where(mount => !mount.Path.Equals(path, comparison) && Contains(path, mount.Path, comparison))
        .Select(mount => Relative(path, mount.Path)).ToArray())).ToArray();
  }

  internal static Mount[] Parse(string mountInfo) {
    var mounts = new Dictionary<string, Mount>(StringComparer.Ordinal);
    foreach (string line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
      string[] fields = line.Split(' ');
      int separator = Array.IndexOf(fields, "-");
      if (separator < 6 || separator + 2 >= fields.Length)
        throw new IOException("Invalid Linux mount information; refusing to scan.");
      Add(mounts, Decode(fields[4]), localFileSystems.Contains(fields[separator + 1]));
    }
    return Finish(mounts);
  }

  internal static Mount[] ParseMac(string mountOutput) {
    var mounts = new Dictionary<string, Mount>(StringComparer.OrdinalIgnoreCase);
    foreach (string raw in mountOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
      string line = raw.TrimEnd('\r');
      int on = line.IndexOf(" on ", StringComparison.Ordinal);
      int options = line.LastIndexOf(" (", StringComparison.Ordinal);
      if (on <= 0 || options <= on + 4 || !line.EndsWith(')') ||
          line.IndexOf(" on ", on + 4, StringComparison.Ordinal) >= 0)
        throw new IOException("Invalid macOS mount information; refusing to scan.");
      string[] flags = line[(options + 2)..^1].Split(", ", StringSplitOptions.None);
      bool local = flags.Contains("local", StringComparer.Ordinal) && macLocalFileSystems.Contains(flags[0]);
      Add(mounts, line[(on + 4)..options], local);
    }
    return Finish(mounts);
  }

  private static void Add(Dictionary<string, Mount> mounts, string path, bool local) {
    if (!path.StartsWith('/') || path.Contains('\0'))
      throw new IOException("Invalid mount path; refusing to scan.");
    path = path.Length > 1 ? path.TrimEnd('/') : path;
    // Stacked mounts cannot be safely distinguished from this snapshot.
    if (!mounts.TryAdd(path, new(path, local)))
      mounts[path] = new(path, false);
  }

  private static Mount[] Finish(Dictionary<string, Mount> mounts) {
    if (mounts.Count == 0)
      throw new IOException("Mount information is empty; refusing to scan.");
    return mounts.Values.ToArray();
  }

  // A watcher cannot prune mount points. Return false whenever its tree includes one.
  public static bool CanWatchTree(string directory) {
    try {
      if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        return false;
      string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
      if (!Directory.Exists(root))
        return false;
      for (string? path = root; path is not null; path = Path.GetDirectoryName(path)) {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
          return false;
      }
      var scans = OperatingSystem.IsLinux() ? Plan(root, File.ReadAllText("/proc/self/mountinfo")) : PlanMac(root, ReadMacMounts());
      return scans.Length == 1 && scans[0].Excludes.Length == 0;
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) {
      return false;
    }
  }

  internal static string ReadMacMounts() {
    var start = new ProcessStartInfo("/sbin/mount") {
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
      CreateNoWindow = true
    };
    using var process = Process.Start(start) ?? throw new IOException("Could not read macOS mount information.");
    var errors = process.StandardError.ReadToEndAsync();
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
      throw new IOException($"Could not read macOS mount information: {errors.GetAwaiter().GetResult().Trim()}");
    return output;
  }

  private static bool Contains(string parent, string path, StringComparison comparison) => path.Equals(parent, comparison) ||
      path.StartsWith(parent, comparison) &&
      (parent == "/" || path.Length > parent.Length && path[parent.Length] == '/');

  private static string Relative(string parent, string path) => path[(parent == "/" ? 1 : parent.Length + 1)..];

  private static string Decode(string value) => value.Replace("\\040", " ", StringComparison.Ordinal)
      .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal)
      .Replace("\\134", "\\", StringComparison.Ordinal);
}
