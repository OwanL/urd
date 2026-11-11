namespace Urd.Core.Tools;

// Resolves Git for Windows Bash; Urd supplies its environment rather than a login profile.
internal sealed class GitBash {
  public string Executable { get; }

  public GitBash(string? configuredPath = null) {
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("Bash tools currently require Windows and Git for Windows.");

    if (configuredPath is not null) {
      Executable = Path.GetFullPath(configuredPath);
      if (!File.Exists(Executable))
        throw new FileNotFoundException("Configured Git Bash executable was not found.", Executable);
      return;
    }

    string[] roots = [
      Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
      Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
    ];
    IEnumerable<string> candidates = roots.Where(root => !string.IsNullOrEmpty(root))
        .Select(root => Path.Combine(root, "Git", "bin", "bash.exe"));
    // Locate Git, not an unrelated bash.exe (for example WSL) on PATH.
    foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
      string directory = entry.Trim().Trim('"');
      if (directory.Length == 0 || !File.Exists(Path.Combine(directory, "git.exe")))
        continue;
      candidates = candidates.Append(Path.GetFullPath(Path.Combine(directory, "..", "bin", "bash.exe")))
          .Append(Path.Combine(directory, "bash.exe"));
    }

    Executable = candidates.FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException("Git Bash was not found. Install Git for Windows or configure GitBashPath.");
  }
}
