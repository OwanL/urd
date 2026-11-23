namespace Urd.Core.Preferences;

// Ignore exact source paths, directory subtrees, or instruction filename groups.
public sealed record SourceIgnoreRules {
  public static IReadOnlyList<string> InstructionFilenames { get; } = Array.AsReadOnly<string>([
    "AGENTS.md", "APPEND_SYSTEM.md", "AGENT_INSTRUCTIONS.md", "AGENT_INSTRUCTION.md",
    "INSTRUCTIONS_FOR_AGENTS.md", "CLAUDE.md", "GEMINI.md"
  ]);

  private static readonly StringComparer pathComparer = OperatingSystem.IsWindows()
      ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
  private static readonly StringComparison pathComparison = OperatingSystem.IsWindows()
      ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

  public string[] Instructions { get; init; } = [];
  public string[] Skills { get; init; } = [];
  public string[] InstructionIncludes { get; init; } = [];
  public string[] SkillIncludes { get; init; } = [];
  public string[] Filenames { get; init; } = ["CLAUDE.md", "GEMINI.md"];

  public bool IsIgnored(string path, bool skill) {
    ArgumentNullException.ThrowIfNull(path);
    if (!skill && Filenames.Length > 0) {
      string name = FilenameKey(Path.GetFileName(path));
      if (Filenames.Any(filename => FilenameMatches(filename, name)))
        return true;
    }

    string[] roots = skill ? Skills : Instructions;
    if (roots.Length == 0 || !Path.IsPathFullyQualified(path))
      return false;
    string fullPath = FullPath(path);
    int ignoredLength = LongestMatch(fullPath, roots);
    int includedLength = LongestMatch(fullPath, skill ? SkillIncludes : InstructionIncludes);
    return ignoredLength >= 0 && ignoredLength > includedLength;
  }

  public SourceIgnoreRules Normalize() {
    ArgumentNullException.ThrowIfNull(Filenames);
    return new() {
      Instructions = NormalizePaths(Instructions),
      Skills = NormalizePaths(Skills),
      InstructionIncludes = NormalizePaths(InstructionIncludes),
      SkillIncludes = NormalizePaths(SkillIncludes),
      Filenames = Filenames.Select(NormalizeFilename).Distinct(StringComparer.Ordinal).ToArray()
    };
  }

  private static string[] NormalizePaths(string[] paths) {
    ArgumentNullException.ThrowIfNull(paths);
    return paths.Select(NormalizePath).Distinct(pathComparer).ToArray();
  }

  private static string NormalizePath(string path) {
    if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
      throw new ArgumentException("Source ignore paths must be fully qualified and contain no control characters.", nameof(path));
    try {
      return FullPath(path);
    } catch (Exception failure) when (failure is NotSupportedException or PathTooLongException) {
      throw new ArgumentException("Source ignore paths must be valid file or directory paths.", nameof(path), failure);
    }
  }

  private static int LongestMatch(string path, string[] roots) => roots
      .Where(Path.IsPathFullyQualified).Select(FullPath)
      .Where(root => IsAtOrBelow(path, root)).Select(root => root.Length).DefaultIfEmpty(-1).Max();

  private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

  private static bool IsAtOrBelow(string path, string root) => path.StartsWith(root, pathComparison) &&
      (path.Length == root.Length || Path.EndsInDirectorySeparator(root) ||
       path[root.Length] == Path.DirectorySeparatorChar);

  private static string NormalizeFilename(string filename) {
    if (filename is not null) {
      string key = FilenameKey(filename);
      string? label = InstructionFilenames.FirstOrDefault(name => FilenameMatches(name, key));
      if (label is not null)
        return label;
    }
    throw new ArgumentException("Select a supported instruction filename.", nameof(filename));
  }

  private static string FilenameKey(string filename) => filename.Replace('-', '_');

  private static bool FilenameMatches(string filename, string key) =>
      string.Equals(FilenameKey(filename), key, StringComparison.OrdinalIgnoreCase);
}
