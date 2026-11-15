using System.ComponentModel;
using System.Diagnostics;

namespace Urd.Core.Context;

// Discovers Git-tracked and packaged skills, preferring project skills with the same name.
internal static class SkillCatalog {
  private static readonly TimeSpan gitTimeout = TimeSpan.FromSeconds(10);

  internal static string PackagedDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "skills");

  internal static IReadOnlyList<Skill> Find(string workingDirectory, string packagedDirectory) =>
      TrackedSkillFiles(workingDirectory)
          .Concat(PackagedSkillFiles(packagedDirectory))
          .Select(Read)
          .OfType<Skill>()
          .DistinctBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
          .ToArray();

  // Outside a Git repository, or when Git is unavailable, there are no project skills.
  private static string[] TrackedSkillFiles(string workingDirectory) {
    string? executable = FindGit();
    if (executable is null)
      return [];

    var start = new ProcessStartInfo(executable) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (string argument in new[] { "-C", workingDirectory, "ls-files", "-z", "--", ":(glob)**/SKILL.md" })
      start.ArgumentList.Add(argument);

    try {
      using Process git = Process.Start(start)!;
      Task<string> output = git.StandardOutput.ReadToEndAsync();
      _ = git.StandardError.ReadToEndAsync();
      if (!git.WaitForExit(gitTimeout)) {
        git.Kill(entireProcessTree: true);
        return [];
      }
      if (git.ExitCode != 0)
        return [];
      return output.GetAwaiter().GetResult()
          .Split('\0', StringSplitOptions.RemoveEmptyEntries)
          .Select(path => Path.GetFullPath(path, workingDirectory))
          .ToArray();
    } catch (Win32Exception) {
      return [];
    }
  }

  private static string? FindGit() =>
      (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
          .Select(entry => entry.Trim().Trim('"'))
          .Where(Path.IsPathFullyQualified)
          .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "git.exe" : "git"))
          .FirstOrDefault(File.Exists);

  private static IEnumerable<string> PackagedSkillFiles(string directory) =>
      Directory.Exists(directory)
          ? Directory.EnumerateFiles(directory, "SKILL.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
          : [];

  private static Skill? Read(string file) {
    Dictionary<string, string> fields;
    try {
      fields = SkillFrontmatter.Read(file);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return null;
    }
    if (!fields.TryGetValue("description", out string? description) || string.IsNullOrWhiteSpace(description))
      return null;

    string name = fields.GetValueOrDefault("name") ?? Path.GetFileName(Path.GetDirectoryName(file))!;
    return new(name, description, file);
  }
}
