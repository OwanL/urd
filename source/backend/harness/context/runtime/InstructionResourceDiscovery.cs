using System.ComponentModel;
using Urd.Core.Preferences;
using Urd.Core.Tools;

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

  internal Task<DiscoveredInstructionResources> FindAsync(string workingDirectory, CancellationToken cancellation) =>
      FindAsync(workingDirectory, new SourceIgnoreRules(), cancellation);

  internal Task<DiscoveredInstructionResources> FindAsync(
      string workingDirectory, SourceIgnoreRules rules, CancellationToken cancellation) =>
      FindAsync(workingDirectory, rules, [], [], cancellation);

  internal async Task<DiscoveredInstructionResources> FindAsync(
      string workingDirectory, SourceIgnoreRules rules, IReadOnlyList<string> protectedSkills,
      IReadOnlyList<string> protectedInstructions, CancellationToken cancellation) {
    workingDirectory = Path.GetFullPath(workingDirectory);
    InstructionResourceCatalogResult served = await catalog.FindPathsAsync(workingDirectory, cancellation);
    string[] paths = ProjectPaths(served, workingDirectory);
    if (protectedSkills.Count != 0 || protectedInstructions.Count != 0) {
      paths = await IncludeMissingProtectedPathsAsync(paths, workingDirectory, rules,
          protectedSkills, protectedInstructions, cancellation);
    }
    DiscoveredSkill[] skills = ReadSkills(SkillPaths(paths, cancellation)
        .Where(path => !rules.IsIgnored(path, skill: true)), workingDirectory, cancellation);
    DiscoveredInstructionDocument[] instructions = await ReadInstructionsAsync(paths.Where(IsInstructionDocument)
        .Where(path => !rules.IsIgnored(path, skill: false)), workingDirectory, cancellation);
    cancellation.ThrowIfCancellationRequested();
    return new(Array.AsReadOnly(skills), Array.AsReadOnly(instructions), served.State);
  }

  internal Task<(IReadOnlyList<DiscoveredSkill> Skills, IReadOnlyList<string> Instructions)> FindMetadataAsync(
      string workingDirectory, CancellationToken cancellation) => FindMetadataAsync(workingDirectory, new SourceIgnoreRules(), cancellation);

  // The legacy protection picker needs skill names, not instruction bodies.
  internal async Task<(IReadOnlyList<DiscoveredSkill> Skills, IReadOnlyList<string> Instructions)> FindMetadataAsync(
      string workingDirectory, SourceIgnoreRules rules, CancellationToken cancellation) {
    workingDirectory = Path.GetFullPath(workingDirectory);
    string[] paths = ProjectPaths(await catalog.FindPathsAsync(workingDirectory, cancellation), workingDirectory);
    DiscoveredSkill[] skills = ReadSkills(SkillPaths(paths, cancellation)
        .Where(path => !rules.IsIgnored(path, skill: true)), workingDirectory, cancellation);
    var instructions = new List<string>();
    foreach (string path in paths.Where(IsInstructionDocument)) {
      cancellation.ThrowIfCancellationRequested();
      if (rules.IsIgnored(path, skill: false))
        continue;
      try {
        using FileStream? stream = OpenResourceFile(path, workingDirectory);
        if (stream is not null)
          instructions.Add(path);
      } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
        // A removed or unreadable resource is unavailable for this discovery.
      }
    }
    cancellation.ThrowIfCancellationRequested();
    return (Array.AsReadOnly(skills), instructions.AsReadOnly());
  }

  internal async Task<SourcePreview> PreviewAsync(string workingDirectory, bool skills, SourceIgnoreRules rules,
      IReadOnlyList<string> protectedResources, CancellationToken cancellation) {
    workingDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
    InstructionResourceCatalogResult served = await catalog.FindPathsAsync(workingDirectory, cancellation);
    string[] projectPaths = ProjectPaths(served, workingDirectory);
    IEnumerable<string> categoryPaths = skills ? SkillPaths(projectPaths, cancellation)
        : projectPaths.Where(IsInstructionDocument);
    string[] paths = categoryPaths.Where(path => {
      cancellation.ThrowIfCancellationRequested();
      // A stale index may contain a removed path or a path now reached through a link.
      // This check uses only file metadata, even for an ignored source.
      return InstructionResourceCatalogWatch.Entry(path) == FileAttributes.Normal;
    }).ToArray();

    // Protected sources skip the selector. They remain visible in the tree, just like ignored sources.
    var protectedPaths = StartingContextBuilder.ProtectedPaths(protectedResources);
    var eligible = paths.Where(path => !rules.IsIgnored(path, skill: skills) && !protectedPaths.Contains(path));
    int count;
    int estimatedTokens;
    if (skills) {
      DiscoveredSkill[] candidates = ReadSkills(eligible, workingDirectory, cancellation);
      count = candidates.Length;
      estimatedTokens = StartingResourcePruner.EstimateManifestTokens(candidates);
    } else {
      DiscoveredInstructionDocument[] candidates = await ReadInstructionsAsync(eligible, workingDirectory, cancellation);
      count = candidates.Length;
      estimatedTokens = StartingResourcePruner.EstimateManifestTokens(candidates);
    }
    cancellation.ThrowIfCancellationRequested();
    return new(paths.Order(StringComparer.Ordinal).ToArray(), workingDirectory, count, estimatedTokens,
        served.State.Phase != InstructionResourceIndexPhase.Complete);
  }

  internal bool IsPackagedSkill(DiscoveredSkill skill) => IsWithinDirectory(skill.Path, packagedDirectory);

  private static string[] ProjectPaths(InstructionResourceCatalogResult served, string workingDirectory) =>
      served.Paths.Select(Path.GetFullPath).Where(path => IsWithinDirectory(path, workingDirectory))
          .Distinct(pathComparer).Order(StringComparer.Ordinal).ToArray();

  // The background catalog is best effort. Check only explicitly protected paths it missed,
  // then let the ordinary readers apply the same opened-file validation and ordering.
  private static async Task<string[]> IncludeMissingProtectedPathsAsync(string[] paths, string workingDirectory,
      SourceIgnoreRules rules, IReadOnlyList<string> protectedSkills, IReadOnlyList<string> protectedInstructions,
      CancellationToken cancellation) {
    var indexed = paths.ToHashSet(pathComparer);
    string[] missing = protectedSkills.Select(Path.GetFullPath)
        .Where(path => FdResourceScanner.IsSkillPath(path) && !rules.IsIgnored(path, skill: true))
        .Concat(protectedInstructions.Select(Path.GetFullPath)
            .Where(path => IsInstructionDocument(path) && !rules.IsIgnored(path, skill: false)))
        .Where(path => !indexed.Contains(path) && IsWithinDirectory(path, workingDirectory))
        .Distinct(pathComparer).ToArray();
    if (missing.Length == 0)
      return paths;

    ResourceMountPolicy.Scan[] scans;
    try {
      scans = await FdResourceScanner.PlanAsync(Path.TrimEndingDirectorySeparator(workingDirectory), cancellation);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or PlatformNotSupportedException) {
      // Do not read missing paths when local mount coverage cannot be established.
      return paths;
    }
    string[] eligible = missing.Where(path => scans.Any(scan => FdResourceScanner.IncludesPath(scan, path))).ToArray();
    return eligible.Length == 0 ? paths : paths.Concat(eligible).Order(StringComparer.Ordinal).ToArray();
  }

  private IEnumerable<string> SkillPaths(string[] projectPaths, CancellationToken cancellation) =>
      projectPaths.Where(FdResourceScanner.IsSkillPath).Concat(PackagedSkillPaths(cancellation)).Distinct(pathComparer);

  private IEnumerable<string> PackagedSkillPaths(CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    if (InstructionResourceCatalogWatch.Entry(packagedDirectory) != FileAttributes.Directory)
      return [];

    return Directory.EnumerateFiles(packagedDirectory, "SKILL.md", new EnumerationOptions {
      RecurseSubdirectories = true,
      AttributesToSkip = FileAttributes.ReparsePoint,
      MatchCasing = MatchCasing.CaseInsensitive,
    }).Order(StringComparer.Ordinal);
  }

  private DiscoveredSkill[] ReadSkills(IEnumerable<string> paths, string workingDirectory, CancellationToken cancellation) {
    var skills = new List<DiscoveredSkill>();
    foreach (string path in paths) {
      cancellation.ThrowIfCancellationRequested();
      string directory = IsWithinDirectory(path, workingDirectory) ? workingDirectory : packagedDirectory;
      if (ReadSkill(path, directory) is { } skill)
        skills.Add(skill);
    }
    return skills.ToArray();
  }

  private static async Task<DiscoveredInstructionDocument[]> ReadInstructionsAsync(
      IEnumerable<string> paths, string workingDirectory, CancellationToken cancellation) {
    var instructions = new List<DiscoveredInstructionDocument>();
    foreach (string path in paths) {
      cancellation.ThrowIfCancellationRequested();
      try {
        using FileStream? stream = OpenResourceFile(path, workingDirectory);
        if (stream is null)
          continue;
        using var reader = new StreamReader(stream);
        string text = await reader.ReadToEndAsync(cancellation);
        instructions.Add(new(text, path, Path.GetDirectoryName(path)!));
      } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
        // A removed or unreadable resource is unavailable for this discovery.
      }
    }
    return instructions.OrderBy(document => document.ScopeDepth).ThenBy(document => document.Path, StringComparer.Ordinal).ToArray();
  }

  private static DiscoveredSkill? ReadSkill(string file, string directory) {
    Dictionary<string, string> fields;
    long sizeBytes;
    try {
      using FileStream? stream = OpenResourceFile(file, directory);
      if (stream is null)
        return null;
      sizeBytes = stream.Length;
      using var reader = new StreamReader(stream);
      fields = SkillFrontmatter.Read(reader);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return null;
    }
    if (!fields.TryGetValue("description", out string? description) || string.IsNullOrWhiteSpace(description))
      return null;

    string name = fields.GetValueOrDefault("name") ?? Path.GetFileName(Path.GetDirectoryName(file)!);
    return new(name, description, file) { SizeBytes = sizeBytes };
  }

  // Validate the opened object, not the pathname that may have changed since indexing.
  // Unsupported platforms fail closed rather than reading an unverified resource.
  private static FileStream? OpenResourceFile(string path, string directory) {
    if (InstructionResourceCatalogWatch.Entry(path) != FileAttributes.Normal)
      return null;
    var stream = File.OpenRead(path);
    try {
      string? actual = OpenedFilePath.Resolve(stream.SafeFileHandle);
      if (actual is not null && pathComparer.Equals(actual, path) && IsWithinDirectory(actual, directory))
        return stream;
    } catch {
      stream.Dispose();
      throw;
    }
    stream.Dispose();
    return null;
  }

  private static bool IsWithinDirectory(string path, string directory) {
    string relative = Path.GetRelativePath(directory, path);
    return relative == "." ||
        (!Path.IsPathRooted(relative) && relative != ".." &&
         !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
         !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
  }

  private static bool IsInstructionDocument(string path) => FdResourceScanner.IsInstructionPath(path) &&
      !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
          .Any(segment => harnessConfigurationDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
}
