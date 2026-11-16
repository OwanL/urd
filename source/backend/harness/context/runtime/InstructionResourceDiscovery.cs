using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Urd.Core.Context;

// Discovers Git-tracked project instructions and skills, plus packaged skills.
internal sealed class InstructionResourceDiscovery {
  private static readonly TimeSpan gitTimeout = TimeSpan.FromSeconds(10);
  private static readonly string[] harnessConfigurationDirectories = [
    ".claude", ".cursor", ".windsurf", ".continue", ".roo", ".gemini", ".opencode", ".github",
  ];

  private readonly string packagedDirectory;

  internal static string PackagedDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "skills");

  internal InstructionResourceDiscovery() : this(PackagedDirectory) { }

  internal InstructionResourceDiscovery(string packagedDirectory) =>
      this.packagedDirectory = Path.GetFullPath(packagedDirectory);

  internal async Task<DiscoveredInstructionResources> FindAsync(
      string workingDirectory,
      CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    workingDirectory = Path.GetFullPath(workingDirectory);

    string? git = FindGit();
    string? root = git is null ? null : await FindRepositoryRootAsync(git, workingDirectory, cancellation);
    string[] trackedFiles = root is null
        ? []
        : await FindTrackedMarkdownFilesAsync(git!, root, cancellation);

    var skills = new List<DiscoveredSkill>();
    var instructions = new List<DiscoveredInstructionDocument>();
    foreach (string relativePath in trackedFiles) {
      cancellation.ThrowIfCancellationRequested();
      string path = Path.GetFullPath(relativePath, root!);
      string fileName = Path.GetFileName(path);
      if (fileName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) {
        DiscoveredSkill? skill = IsWithinDirectory(path, workingDirectory) ? ReadSkill(path) : null;
        if (skill is not null)
          skills.Add(skill);
        continue;
      }

      if (!IsInstructionDocument(relativePath))
        continue;

      try {
        string text = await File.ReadAllTextAsync(path, cancellation);
        instructions.Add(new(text, path, Path.GetDirectoryName(path)!));
      } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
        // A removed or unreadable tracked resource is unavailable for this discovery.
      }
    }

    skills.AddRange(PackagedSkills(cancellation));
    DiscoveredSkill[] selectedSkills = skills
        .DistinctBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    DiscoveredInstructionDocument[] orderedInstructions = instructions
        .OrderBy(instruction => instruction.Order)
        .ThenBy(instruction => instruction.Path, StringComparer.Ordinal)
        .ToArray();

    cancellation.ThrowIfCancellationRequested();
    return new(Array.AsReadOnly(selectedSkills), Array.AsReadOnly(orderedInstructions));
  }

  private static async Task<string?> FindRepositoryRootAsync(
      string git,
      string workingDirectory,
      CancellationToken cancellation) {
    string? output = await RunGitAsync(git, ["-C", workingDirectory, "rev-parse", "--show-toplevel"], cancellation);
    string root = output?.TrimEnd('\r', '\n') ?? "";
    return root.Length == 0 ? null : Path.GetFullPath(root);
  }

  private static async Task<string[]> FindTrackedMarkdownFilesAsync(
      string git,
      string repositoryRoot,
      CancellationToken cancellation) {
    string? output = await RunGitAsync(git, [
      "-C", repositoryRoot,
      "ls-files", "-z", "--full-name", "--", ":(glob,icase)**/*.md",
    ], cancellation);
    return output?.Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Order(StringComparer.Ordinal)
        .ToArray() ?? [];
  }

  private static async Task<string?> RunGitAsync(
      string git,
      IReadOnlyList<string> arguments,
      CancellationToken cancellation) {
    var start = new ProcessStartInfo(git) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (string argument in arguments)
      start.ArgumentList.Add(argument);

    using var process = new Process { StartInfo = start };
    try {
      if (!process.Start())
        return null;
    } catch (Win32Exception) {
      return null;
    }

    using var timeout = new CancellationTokenSource(gitTimeout);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
    Task<string> output = process.StandardOutput.ReadToEndAsync(linked.Token);
    Task<string> error = process.StandardError.ReadToEndAsync(linked.Token);
    try {
      await process.WaitForExitAsync(linked.Token);
      await Task.WhenAll(output, error);
    } catch (OperationCanceledException) {
      await StopAndJoinGitAsync(process, output, error);
      cancellation.ThrowIfCancellationRequested();
      return null;
    } catch (IOException) {
      await StopAndJoinGitAsync(process, output, error);
      return null;
    }

    return process.ExitCode == 0 ? await output : null;
  }

  private static async Task StopAndJoinGitAsync(Process process, Task<string> output, Task<string> error) {
    try {
      if (!process.HasExited)
        process.Kill(entireProcessTree: true);
    } catch (InvalidOperationException) {
      // The process exited between the check and Kill.
    } catch (Win32Exception) {
      // Still join the process and redirected reads if termination is unavailable.
    }

    try {
      await process.WaitForExitAsync(CancellationToken.None);
    } catch (InvalidOperationException) {
      // The process has already been disposed or exited.
    }

    try {
      await Task.WhenAll(output, error);
    } catch (Exception exception) when (exception is IOException or OperationCanceledException) {
      // Both redirected reads have completed; their contents are not needed on failure.
    }
  }

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

  private static DiscoveredSkill? ReadSkill(string file) {
    Dictionary<string, string> fields;
    try {
      fields = SkillFrontmatter.Read(file);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return null;
    }
    if (!fields.TryGetValue("description", out string? description) || string.IsNullOrWhiteSpace(description))
      return null;

    string name = fields.GetValueOrDefault("name") ?? Path.GetFileName(Path.GetDirectoryName(file)!);
    return new(name, description, Path.GetFullPath(file));
  }

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

  private static string? FindGit() =>
      (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
          .Select(entry => entry.Trim().Trim('"'))
          .Where(Path.IsPathFullyQualified)
          .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "git.exe" : "git"))
          .FirstOrDefault(File.Exists);
}
