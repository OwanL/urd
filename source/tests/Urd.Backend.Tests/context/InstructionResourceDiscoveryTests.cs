using Urd.Core.Context;
using Xunit;

namespace Urd.Backend.Tests.Context;

public sealed class InstructionResourceDiscoveryTests : IDisposable {
  private readonly string root = Path.Combine(Path.GetTempPath(), $"urd-resources-{Guid.NewGuid():N}");
  private readonly List<string> linkedDirectories = [];
  private string Project => Path.Combine(root, "project");
  private string Packaged => Path.Combine(root, "packaged");

  public InstructionResourceDiscoveryTests() =>
      Directory.CreateDirectory(Project);

  public void Dispose() {
    foreach (string link in linkedDirectories)
      Directory.Delete(link);
    foreach (string file in Directory.EnumerateFiles(root, "*", new EnumerationOptions {
      RecurseSubdirectories = true,
      AttributesToSkip = FileAttributes.ReparsePoint,
    }))
      File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(root, recursive: true);
  }

  [Fact]
  public async Task DiscoversDocumentsWithinWorkingDirectoryInDeterministicScopeOrder() {
    Write("AGENTS.md", "Root instructions\n");
    Write("area/APPEND_SYSTEM.md", "Append instructions\n");
    Write("area/nested/AGENT-INSTRUCTIONS.md", "Nested full instructions\nSecond line\n");
    Write("sibling/INSTRUCTIONS_FOR_AGENTS.md", "Sibling instructions\n");
    Write("README.md", "Ordinary Markdown\n");
    Write("CLAUDE.md", "Other harness\n");
    Write("anything.instructions.md", "Copilot configuration\n");
    Write(".github/AGENTS.md", "GitHub-specific configuration\n");
    Write(".github/copilot-instructions.md", "Copilot instructions\n");
    Write(".cursor/rules/AGENTS.md", "Cursor configuration\n");
    Write("untracked/AGENTS.md", "Untracked instructions\n");
    await using var catalog = CreateCatalog();
    var discovery = new InstructionResourceDiscovery(catalog, Packaged);
    DiscoveredInstructionResources resources = await discovery.FindAsync(Project, CancellationToken.None);

    Assert.Equal(
      ["AGENTS.md", Path.Combine("area", "APPEND_SYSTEM.md"), Path.Combine("sibling", "INSTRUCTIONS_FOR_AGENTS.md"), Path.Combine("untracked", "AGENTS.md"), Path.Combine("area", "nested", "AGENT-INSTRUCTIONS.md")],
      resources.Instructions.Select(document => Path.GetRelativePath(Project, document.Path)));
    Assert.Equal("Nested full instructions\nSecond line\n", resources.Instructions[^1].Text);
    Assert.All(resources.Instructions, document => Assert.True(Path.IsPathFullyQualified(document.Identity)));
    Assert.All(resources.Instructions, document => Assert.True(Path.IsPathFullyQualified(document.Scope)));
    Assert.Equal(
      [true, true, false, false, true],
      resources.Instructions.Select(document => document.AppliesTo(Input(Path.Combine(Project, "area", "nested")))));
    Assert.DoesNotContain(resources.Instructions, document => document.Text.Contains("Ordinary Markdown", StringComparison.Ordinal));
    Assert.DoesNotContain(resources.Instructions, document => document.Path.Contains(".github", StringComparison.OrdinalIgnoreCase));
    Assert.DoesNotContain(resources.Instructions, document => document.Path.Contains(".cursor", StringComparison.OrdinalIgnoreCase));

    DiscoveredInstructionResources scoped = await discovery.FindAsync(Path.Combine(Project, "area"), CancellationToken.None);
    Assert.Equal([Path.Combine("area", "APPEND_SYSTEM.md"), Path.Combine("area", "nested", "AGENT-INSTRUCTIONS.md")],
        scoped.Instructions.Select(document => Path.GetRelativePath(Project, document.Path)));
  }

  [Fact]
  public async Task DiscoversProjectAndPackagedSkillsIncludingDuplicateNames() {
    Write("skills/diagnose/SKILL.md", Skill("diagnose", "Project diagnose."));
    Write("a/b/deploy/SKILL.md", Skill("deploy", ">\n  Deploy the\n  service."));
    Write("untracked/SKILL.md", Skill("untracked", "Not versioned."));
    WritePackaged("diagnose/SKILL.md", Skill("diagnose", "Packaged diagnose."));
    WritePackaged("notes/SKILL.md", Skill("notes", "Take notes."));
    WritePackaged("broken/SKILL.md", "no frontmatter");
    await using var catalog = CreateCatalog();
    var discovery = new InstructionResourceDiscovery(catalog, Packaged);
    DiscoveredInstructionResources resources = await discovery.FindAsync(Project, CancellationToken.None);

    Assert.Equal(
      [("deploy", "Deploy the service."), ("diagnose", "Packaged diagnose."), ("diagnose", "Project diagnose."), ("notes", "Take notes."), ("untracked", "Not versioned.")],
      resources.Skills.Select(skill => (skill.Name, skill.Description)).Order());
    Assert.Equal(Path.GetFullPath(Path.Combine(Project, "a", "b", "deploy", "SKILL.md")),
        resources.Skills.Single(skill => skill.Name == "deploy").Identity);

    DiscoveredInstructionResources scoped = await discovery.FindAsync(Path.Combine(Project, "a"), CancellationToken.None);
    Assert.Contains(scoped.Skills, skill => skill.Name == "deploy");
    Assert.Contains(scoped.Skills, skill => skill.Name == "diagnose" && skill.Description == "Packaged diagnose.");
  }

  [Fact]
  public async Task OutsideGitRepositoryProjectAndPackagedSkillsAreDiscovered() {
    Write("local/SKILL.md", Skill("local", "Not in a repository."));
    WritePackaged("notes/SKILL.md", Skill("notes", "Take notes."));

    await using var catalog = CreateCatalog();
    DiscoveredInstructionResources resources = await new InstructionResourceDiscovery(catalog, Packaged)
        .FindAsync(Project, CancellationToken.None);

    Assert.Equal(["local", "notes"], resources.Skills.Select(skill => skill.Name).Order());
    Assert.Empty(resources.Instructions);
  }

  [Fact]
  public async Task UrdPackagesItsDiagnoseSkill() {
    await using var catalog = CreateCatalog();
    DiscoveredInstructionResources resources = await new InstructionResourceDiscovery(catalog, InstructionResourceDiscovery.PackagedDirectory)
        .FindAsync(Project, CancellationToken.None);

    Assert.Contains(resources.Skills, skill => skill.Name == "diagnose");
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task StaleIndexedResourcesDoNotFollowSwappedLinks(bool swapAncestor) {
    string instruction = Write("area/AGENTS.md", "Original instructions");
    string skill = Write("area/SKILL.md", Skill("original", "Original skill."));
    string outside = Path.Combine(root, "outside");
    WriteTo(outside, "AGENTS.md", "Outside instructions");
    WriteTo(outside, "SKILL.md", Skill("outside", "Outside skill."));
    await using var catalog = new InstructionResourceCatalog(Path.Combine(root, "catalog.sqlite"),
        (_, _, _) => Task.FromResult(new[] { instruction, skill }), (_, _, _) => new Watch());
    var discovery = new InstructionResourceDiscovery(catalog, Packaged);
    DiscoveredInstructionResources initial = await discovery.FindAsync(Project, CancellationToken.None);
    Assert.Single(initial.Instructions);
    Assert.Equal("original", Assert.Single(initial.Skills).Name);

    if (!swapAncestor) {
      File.Delete(instruction);
      File.Delete(skill);
      try {
        File.CreateSymbolicLink(instruction, Path.Combine(outside, "AGENTS.md"));
        File.CreateSymbolicLink(skill, Path.Combine(outside, "SKILL.md"));
      } catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException) {
        if (File.Exists(instruction))
          File.Delete(instruction);
        if (File.Exists(skill))
          File.Delete(skill);
        swapAncestor = true;
      }
    }
    if (swapAncestor) {
      Directory.Delete(Path.Combine(Project, "area"), recursive: true);
      CreateDirectoryLink(Path.Combine(Project, "area"), outside);
    }

    var metadata = await discovery.FindMetadataAsync(Project, CancellationToken.None);
    Assert.Empty(metadata.Instructions);
    Assert.Empty(metadata.Skills);
    DiscoveredInstructionResources resources = await discovery.FindAsync(Project, CancellationToken.None);
    Assert.Empty(resources.Instructions);
    Assert.Empty(resources.Skills);
  }

  [Fact]
  public async Task StableLinksAndRemovedIndexedResourcesAreNotDiscovered() {
    string removedInstruction = Write("removed/AGENTS.md", "Removed");
    string removedSkill = Write("removed/SKILL.md", Skill("removed", "Removed."));
    string outside = Path.Combine(root, "outside");
    WriteTo(outside, "AGENTS.md", "Outside");
    WriteTo(outside, "SKILL.md", Skill("outside", "Outside."));
    string link = Path.Combine(Project, "link");
    CreateDirectoryLink(link, outside);
    string[] paths = [removedInstruction, removedSkill, Path.Combine(link, "AGENTS.md"), Path.Combine(link, "SKILL.md")];
    await using var catalog = new InstructionResourceCatalog(Path.Combine(root, "catalog.sqlite"),
        (_, _, _) => Task.FromResult(paths), (_, _, _) => new Watch());
    var discovery = new InstructionResourceDiscovery(catalog, Packaged);
    DiscoveredInstructionResources initial = await discovery.FindAsync(Project, CancellationToken.None);
    Assert.Equal("Removed", Assert.Single(initial.Instructions).Text);
    Assert.Equal("removed", Assert.Single(initial.Skills).Name);
    File.Delete(removedInstruction);
    File.Delete(removedSkill);

    var metadata = await discovery.FindMetadataAsync(Project, CancellationToken.None);
    Assert.Empty(metadata.Instructions);
    Assert.Empty(metadata.Skills);
    DiscoveredInstructionResources resources = await discovery.FindAsync(Project, CancellationToken.None);
    Assert.Empty(resources.Instructions);
    Assert.Empty(resources.Skills);
  }

  [Theory]
  [InlineData(".claude")]
  [InlineData(".cursor")]
  [InlineData(".github")]
  [InlineData(".windsurf")]
  public async Task WorkingDirectoryInsideAnotherHarnessDoesNotExposeItsInstructions(string harness) {
    Write($"{harness}/nested/AGENTS.md", "Other harness");
    await using var catalog = CreateCatalog();
    var discovery = new InstructionResourceDiscovery(catalog, Packaged);

    var metadata = await discovery.FindMetadataAsync(Path.Combine(Project, harness, "nested"), CancellationToken.None);
    Assert.Empty(metadata.Instructions);
    DiscoveredInstructionResources resources = await discovery.FindAsync(Path.Combine(Project, harness, "nested"), CancellationToken.None);
    Assert.Empty(resources.Instructions);
  }

  [Theory]
  [InlineData("Plain text # note", "Plain text")]
  [InlineData("\"Quoted # kept\" # note", "Quoted # kept")]
  [InlineData("'It''s here' # note", "It's here")]
  [InlineData("Issue#42 stays", "Issue#42 stays")]
  [InlineData("> # note\n  Folded\n  text.", "Folded text.")]
  public void SkillDescriptionsDropTrailingComments(string value, string expected) {
    string file = Write("SKILL.md", Skill("example", value));

    Assert.Equal(expected, SkillFrontmatter.Read(file)["description"]);
  }

  private InstructionResourceCatalog CreateCatalog() => new(Path.Combine(root, "catalog.sqlite"),
      (directory, _, _) => Task.FromResult(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
          .Where(FdResourceScanner.IsResourcePath).ToArray()), (_, _, _) => null);

  private void CreateDirectoryLink(string link, string target) {
    try {
      Directory.CreateSymbolicLink(link, target);
    } catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException) {
      using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") {
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "/c", "mklink", "/J", link, target },
      })!;
      process.WaitForExit();
      Assert.Equal(0, process.ExitCode);
    }
    linkedDirectories.Add(link);
  }

  private sealed class Watch : IDisposable {
    public void Dispose() { }
  }

  private static StartingContextInput Input(string directory) => new(SessionKind.Primary, default, directory);

  private string Write(string relativePath, string text) => WriteTo(Project, relativePath, text);

  private void WritePackaged(string relativePath, string text) => WriteTo(Packaged, relativePath, text);

  private static string WriteTo(string directory, string relativePath, string text) {
    string path = Path.Combine(directory, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
    return path;
  }

  private static string Skill(string name, string description) =>
      $"---\nname: {name}\ndescription: {description}\n---\nBody.\n";
}
