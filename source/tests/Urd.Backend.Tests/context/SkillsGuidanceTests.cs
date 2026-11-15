using System.Diagnostics;
using Urd.Core.Context;
using Xunit;

namespace Urd.Backend.Tests.Context;

public sealed class SkillsGuidanceTests : IDisposable {
  private readonly string root = Path.Combine(Path.GetTempPath(), $"urd-skills-{Guid.NewGuid():N}");
  private string Project => Path.Combine(root, "project");
  private string Packaged => Path.Combine(root, "packaged");

  public SkillsGuidanceTests() =>
      Directory.CreateDirectory(Project);

  public void Dispose() {
    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
      File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(root, recursive: true);
  }

  [Fact]
  public void TrackedProjectSkillsReplacePackagedSkillsWithTheSameName() {
    Git("init", "--quiet");
    Write(Path.Combine(Project, "skills", "diagnose", "SKILL.md"), Skill("diagnose", "\"Project diagnose.\""));
    Write(Path.Combine(Project, "a", "b", "deploy", "SKILL.md"), Skill("deploy", ">\n  Deploy the\n  service."));
    Write(Path.Combine(Project, "untracked", "SKILL.md"), Skill("untracked", "Not versioned."));
    Write(Path.Combine(Packaged, "diagnose", "SKILL.md"), Skill("diagnose", "Packaged diagnose."));
    Write(Path.Combine(Packaged, "notes", "SKILL.md"), Skill("notes", "Take notes."));
    Write(Path.Combine(Packaged, "broken", "SKILL.md"), "no frontmatter");
    Git("add", "skills", "a");

    IReadOnlyList<Skill> skills = SkillCatalog.Find(Project, Packaged);

    Assert.Equal(
        [("deploy", "Deploy the service."), ("diagnose", "Project diagnose."), ("notes", "Take notes.")],
        skills.Select(skill => (skill.Name, skill.Description)).Order());
    Assert.Equal(Path.Combine(Project, "a", "b", "deploy", "SKILL.md"), skills.Single(skill => skill.Name == "deploy").Path);
  }

  [Fact]
  public void OutsideARepositoryOnlyPackagedSkillsAreListed() {
    Write(Path.Combine(Project, "local", "SKILL.md"), Skill("local", "Not in a repository."));
    Write(Path.Combine(Packaged, "notes", "SKILL.md"), Skill("notes", "Take <notes> & more."));

    IReadOnlyList<Skill> skills = SkillCatalog.Find(Project, Packaged);
    string text = SkillsGuidance.Compose(skills)!;

    Assert.Equal(["notes"], skills.Select(skill => skill.Name));
    Assert.StartsWith("The following skills provide specialized instructions for specific tasks.", text, StringComparison.Ordinal);
    Assert.Contains("<description>Take &lt;notes&gt; &amp; more.</description>", text, StringComparison.Ordinal);
    Assert.EndsWith("</available_skills>", text, StringComparison.Ordinal);
    Assert.Null(SkillsGuidance.Compose([]));
  }

  [Theory]
  [InlineData("Plain text # note", "Plain text")]
  [InlineData("\"Quoted # kept\" # note", "Quoted # kept")]
  [InlineData("'It''s here' # note", "It's here")]
  [InlineData("Issue#42 stays", "Issue#42 stays")]
  [InlineData("> # note\n  Folded\n  text.", "Folded text.")]
  public void DescriptionsDropTrailingComments(string value, string expected) {
    string file = Path.Combine(Project, "SKILL.md");
    Write(file, Skill("example", value));

    Assert.Equal(expected, SkillFrontmatter.Read(file)["description"]);
  }

  [Fact]
  public void UrdPackagesItsDiagnoseSkill() =>
      Assert.Contains(SkillCatalog.Find(Project, SkillCatalog.PackagedDirectory), skill => skill.Name == "diagnose");

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

  private static string Skill(string name, string description) =>
      $"---\nname: {name}\ndescription: {description}\n---\nBody.\n";

  private static void Write(string path, string text) {
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
  }
}
