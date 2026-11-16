using System.Diagnostics;
using Urd.Core.Context;
using Xunit;

namespace Urd.Backend.Tests.Context;

public sealed class InstructionResourceDiscoveryTests : IDisposable {
  private readonly string root = Path.Combine(Path.GetTempPath(), $"urd-resources-{Guid.NewGuid():N}");
  private string Project => Path.Combine(root, "project");
  private string Packaged => Path.Combine(root, "packaged");

  public InstructionResourceDiscoveryTests() =>
      Directory.CreateDirectory(Project);

  public void Dispose() {
    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
      File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(root, recursive: true);
  }

  [Fact]
  public async Task DiscoversTrackedAgentDocumentsInDeterministicScopeOrder() {
    Git("init", "--quiet");
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
    Git("add", "AGENTS.md", "area", "sibling", "README.md", "CLAUDE.md", "anything.instructions.md", ".github", ".cursor");

    var discovery = new InstructionResourceDiscovery(Packaged);
    DiscoveredInstructionResources resources = await discovery.FindAsync(Path.Combine(Project, "area", "nested"), CancellationToken.None);

    Assert.Equal(
      ["AGENTS.md", Path.Combine("area", "APPEND_SYSTEM.md"), Path.Combine("sibling", "INSTRUCTIONS_FOR_AGENTS.md"), Path.Combine("area", "nested", "AGENT-INSTRUCTIONS.md")],
      resources.Instructions.Select(document => Path.GetRelativePath(Project, document.Path)));
    Assert.Equal("Nested full instructions\nSecond line\n", resources.Instructions[^1].Text);
    Assert.All(resources.Instructions, document => Assert.True(Path.IsPathFullyQualified(document.Identity)));
    Assert.All(resources.Instructions, document => Assert.True(Path.IsPathFullyQualified(document.Scope)));
    Assert.Equal(
      [true, true, false, true],
      resources.Instructions.Select(document => document.AppliesTo(Input(Path.Combine(Project, "area", "nested")))));
    Assert.DoesNotContain(resources.Instructions, document => document.Text.Contains("Ordinary Markdown", StringComparison.Ordinal));
    Assert.DoesNotContain(resources.Instructions, document => document.Path.Contains(".github", StringComparison.OrdinalIgnoreCase));
    Assert.DoesNotContain(resources.Instructions, document => document.Path.Contains(".cursor", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public async Task ProjectSkillsOverridePackagedSkillsAndUntrackedSkillsAreIgnored() {
    Git("init", "--quiet");
    Write("skills/diagnose/SKILL.md", Skill("diagnose", "Project diagnose."));
    Write("a/b/deploy/SKILL.md", Skill("deploy", ">\n  Deploy the\n  service."));
    Write("untracked/SKILL.md", Skill("untracked", "Not versioned."));
    WritePackaged("diagnose/SKILL.md", Skill("diagnose", "Packaged diagnose."));
    WritePackaged("notes/SKILL.md", Skill("notes", "Take notes."));
    WritePackaged("broken/SKILL.md", "no frontmatter");
    Git("add", "skills", "a");

    DiscoveredInstructionResources resources = await new InstructionResourceDiscovery(Packaged)
        .FindAsync(Project, CancellationToken.None);

    Assert.Equal(
      [("deploy", "Deploy the service."), ("diagnose", "Project diagnose."), ("notes", "Take notes.")],
      resources.Skills.Select(skill => (skill.Name, skill.Description)).Order());
    Assert.Equal(Path.GetFullPath(Path.Combine(Project, "a", "b", "deploy", "SKILL.md")),
        resources.Skills.Single(skill => skill.Name == "deploy").Identity);

    DiscoveredInstructionResources scoped = await new InstructionResourceDiscovery(Packaged)
        .FindAsync(Path.Combine(Project, "a"), CancellationToken.None);
    Assert.Contains(scoped.Skills, skill => skill.Name == "deploy");
    Assert.Contains(scoped.Skills, skill => skill.Name == "diagnose" && skill.Description == "Packaged diagnose.");
  }

  [Fact]
  public async Task OutsideGitRepositoryOnlyPackagedSkillsAreDiscovered() {
    Write("local/SKILL.md", Skill("local", "Not in a repository."));
    WritePackaged("notes/SKILL.md", Skill("notes", "Take notes."));

    DiscoveredInstructionResources resources = await new InstructionResourceDiscovery(Packaged)
        .FindAsync(Project, CancellationToken.None);

    Assert.Equal(["notes"], resources.Skills.Select(skill => skill.Name));
    Assert.Empty(resources.Instructions);
  }

  [Fact]
  public async Task UrdPackagesItsDiagnoseSkill() {
    DiscoveredInstructionResources resources = await new InstructionResourceDiscovery()
        .FindAsync(Project, CancellationToken.None);

    Assert.Contains(resources.Skills, skill => skill.Name == "diagnose");
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

  private void Git(params string[] arguments) {
    string executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(directory => Path.Combine(directory.Trim('"'), "git.exe"))
        .First(File.Exists);
    var start = new ProcessStartInfo(executable) { WorkingDirectory = Project, UseShellExecute = false };
    foreach (string argument in arguments)
      start.ArgumentList.Add(argument);
    using Process git = Process.Start(start)!;
    git.WaitForExit();
    Assert.Equal(0, git.ExitCode);
  }

  private static StartingContextInput Input(string directory) => new(SessionKind.Primary, default, directory, null);

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
