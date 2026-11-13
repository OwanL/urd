using System.ComponentModel;
using System.Diagnostics;
using System.Security;

namespace Urd.Core.Context;

// Lists skills by name, description and path; the model reads a skill's file when needed.
// Skills come from SKILL.md files tracked by Git under the working directory, then from
// skills packaged with Urd. A project skill replaces a packaged skill with the same name.
// Source: harness/pi/packages/coding-agent/src/core/skills.ts (formatSkillsForPrompt).
internal sealed class SkillsGuidance : ContextGuidance {
  private static readonly TimeSpan gitTimeout = TimeSpan.FromSeconds(10);

  internal static string PackagedDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "skills");

  public override int Order => 220;

  public override bool AppliesTo(StartingContextInput input) =>
      input.WorkingDirectory is not null;

  public override string? GetText(StartingContextInput input) =>
      Compose(Find(input.WorkingDirectory!, PackagedDirectory));

  internal static string? Compose(IReadOnlyList<Skill> skills) {
    if (skills.Count == 0)
      return null;

    string entries = string.Concat(skills.Select(skill => $"""
          <skill>
            <name>{SecurityElement.Escape(skill.Name)}</name>
            <description>{SecurityElement.Escape(skill.Description)}</description>
            <location>{SecurityElement.Escape(skill.Path)}</location>
          </skill>

        """));
    return $"""
        The following skills provide specialized instructions for specific tasks.
        Use the read tool to load a skill's file when the task matches its description.
        When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.

        <available_skills>
        {entries}</available_skills>
        """;
  }

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
