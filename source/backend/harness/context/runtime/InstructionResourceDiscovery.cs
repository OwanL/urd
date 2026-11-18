using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Urd.Core.Context;

// Discovers cwd-scoped project instructions and skills, plus packaged skills.
internal sealed class InstructionResourceDiscovery {
  private static readonly string[] harnessConfigurationDirectories = [
    ".claude", ".cursor", ".windsurf", ".continue", ".roo", ".gemini", ".opencode", ".github",
  ];
  private static readonly StringComparer pathComparer = OperatingSystem.IsWindows()
      ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

  private readonly InstructionResourceCatalog catalog;
  private readonly string packagedDirectory;

  internal static string PackagedDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "skills");

  internal InstructionResourceDiscovery(InstructionResourceCatalog catalog, string packagedDirectory) {
    ArgumentNullException.ThrowIfNull(catalog);
    this.catalog = catalog;
    this.packagedDirectory = Path.GetFullPath(packagedDirectory);
  }

  internal async Task<DiscoveredInstructionResources> FindAsync(
      string workingDirectory,
      CancellationToken cancellation) {
    var (skills, paths) = await FindMetadataAsync(workingDirectory, cancellation);
    var instructions = new List<DiscoveredInstructionDocument>();
    foreach (string path in paths) {
      cancellation.ThrowIfCancellationRequested();
      try {
        using FileStream? stream = OpenProjectFile(path, workingDirectory);
        if (stream is null)
          continue;
        using var reader = new StreamReader(stream);
        string text = await reader.ReadToEndAsync(cancellation);
        instructions.Add(new(text, path, Path.GetDirectoryName(path)!));
      } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
        // A removed or unreadable resource is unavailable for this discovery.
      }
    }
    cancellation.ThrowIfCancellationRequested();
    return new(skills, Array.AsReadOnly(instructions.OrderBy(instruction => instruction.Order)
        .ThenBy(instruction => instruction.Path, StringComparer.Ordinal).ToArray()));
  }

  // The settings picker needs paths and skill frontmatter, not instruction bodies.
  internal async Task<(IReadOnlyList<DiscoveredSkill> Skills, IReadOnlyList<string> Instructions)> FindMetadataAsync(
      string workingDirectory,
      CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    workingDirectory = Path.GetFullPath(workingDirectory);
    IReadOnlyList<string> paths = await catalog.FindPathsAsync(workingDirectory, cancellation);
    var skills = new List<DiscoveredSkill>();
    var instructions = new List<string>();
    foreach (string path in paths.Select(Path.GetFullPath).Distinct(pathComparer).Order(StringComparer.Ordinal)) {
      cancellation.ThrowIfCancellationRequested();
      if (!IsWithinDirectory(path, workingDirectory))
        continue;
      if (Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) {
        DiscoveredSkill? skill = ReadSkill(path, workingDirectory);
        if (skill is not null)
          skills.Add(skill);
      } else if (IsInstructionDocument(path)) {
        try {
          using FileStream? stream = OpenProjectFile(path, workingDirectory);
          if (stream is not null)
            instructions.Add(path);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
          // A removed or unreadable resource is unavailable for this discovery.
        }
      }
    }
    skills.AddRange(PackagedSkills(cancellation));
    cancellation.ThrowIfCancellationRequested();
    return (Array.AsReadOnly(skills.DistinctBy(skill => skill.Identity, pathComparer).ToArray()),
        Array.AsReadOnly(instructions.ToArray()));
  }

  internal bool IsPackagedSkill(DiscoveredSkill skill) => IsWithinDirectory(skill.Path, packagedDirectory);

  private IEnumerable<DiscoveredSkill> PackagedSkills(CancellationToken cancellation) {
    if (!Directory.Exists(packagedDirectory))
      yield break;

    foreach (string file in Directory.EnumerateFiles(packagedDirectory, "SKILL.md", SearchOption.AllDirectories)
                 .Order(StringComparer.Ordinal)) {
      cancellation.ThrowIfCancellationRequested();
      DiscoveredSkill? skill = ReadSkill(file);
      if (skill is not null)
        yield return skill;
    }
  }

  private static DiscoveredSkill? ReadSkill(string file, string? workingDirectory = null) {
    Dictionary<string, string> fields;
    try {
      if (workingDirectory is null) {
        fields = SkillFrontmatter.Read(file);
      } else {
        using FileStream? stream = OpenProjectFile(file, workingDirectory);
        if (stream is null)
          return null;
        using var reader = new StreamReader(stream);
        fields = SkillFrontmatter.Read(reader);
      }
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return null;
    }
    if (!fields.TryGetValue("description", out string? description) || string.IsNullOrWhiteSpace(description))
      return null;

    string name = fields.GetValueOrDefault("name") ?? Path.GetFileName(Path.GetDirectoryName(file)!);
    return new(name, description, Path.GetFullPath(file));
  }

  // Validate the opened object, not the pathname that may have changed since indexing.
  // Unsupported platforms fail closed rather than reading an unverified project resource.
  private static FileStream? OpenProjectFile(string path, string directory) {
    if (InstructionResourceCatalogWatch.Entry(path) != FileAttributes.Normal)
      return null;
    var stream = File.OpenRead(path);
    try {
      string? actual = OpenFilePath(stream.SafeFileHandle);
      if (actual is not null && pathComparer.Equals(actual, path) && IsWithinDirectory(actual, directory))
        return stream;
    } catch {
      stream.Dispose();
      throw;
    }
    stream.Dispose();
    return null;
  }

  private static string? OpenFilePath(SafeFileHandle handle) {
    if (OperatingSystem.IsWindows()) {
      var buffer = new char[512];
      uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
      if (length >= buffer.Length && length <= 32768) {
        buffer = new char[length];
        length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
      }
      if (length == 0 || length >= buffer.Length)
        return null;
      string path = new(buffer, 0, (int)length);
      if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        return @"\\" + path[8..];
      return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : null;
    }
    if (OperatingSystem.IsLinux()) {
      // procfs exposes the kernel's path for this descriptor. The calling FileStream owns
      // the handle for this entire synchronous lookup; only its numeric identifier is needed.
#pragma warning disable S3869
      string descriptor = $"/proc/self/fd/{handle.DangerousGetHandle()}";
#pragma warning restore S3869
      return new FileInfo(descriptor).LinkTarget;
    }
    if (OperatingSystem.IsMacOS()) {
      var buffer = new byte[1024];
      if (fcntl(handle, 50, buffer) == -1)
        return null;
      int length = Array.IndexOf(buffer, (byte)0);
      return length < 0 ? null : Encoding.UTF8.GetString(buffer, 0, length);
    }
    return null;
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);

  [DllImport("libc", SetLastError = true)]
  private static extern int fcntl(SafeFileHandle handle, int command, byte[] path);

  private static bool IsWithinDirectory(string path, string directory) {
    string relative = Path.GetRelativePath(directory, path);
    return relative == "." ||
        (!Path.IsPathRooted(relative) && relative != ".." &&
         !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
         !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
  }

  private static bool IsInstructionDocument(string relativePath) {
    string[] segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
    if (segments.Any(segment => harnessConfigurationDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
      return false;

    string name = Path.GetFileName(relativePath);
    if (name.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GEMINI.md", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".instructions.md", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("copilot-", StringComparison.OrdinalIgnoreCase))
      return false;

    return name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("APPEND_SYSTEM.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("APPEND-SYSTEM.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AGENT_INSTRUCTIONS.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AGENT-INSTRUCTIONS.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AGENT_INSTRUCTION.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AGENT-INSTRUCTION.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("INSTRUCTIONS_FOR_AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("INSTRUCTIONS-FOR-AGENTS.md", StringComparison.OrdinalIgnoreCase);
  }
}
