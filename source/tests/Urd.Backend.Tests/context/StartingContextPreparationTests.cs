using System.Collections.Concurrent;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Context;

public sealed class StartingContextPreparationTests {
  [Fact]
  public async Task PreparationKeepsNestedScopedDocumentsAndUnionsRuntimeProtectedTools() {
    string root = Path.Combine(Path.GetTempPath(), $"urd-context-{Guid.NewGuid():N}");
    string project = Path.Combine(root, "project");
    string packaged = Path.Combine(root, "packaged");
    Directory.CreateDirectory(project);
    try {
      string skill = Write(project, "skills/context/SKILL.md", "---\nname: context\ndescription: Context skill.\n---\n");
      string rootDocument = Write(project, "AGENTS.md", "Root rule.\n");
      string nestedDocument = Write(project, Path.Combine("nested", "AGENTS.md"), "Nested rule.\nFull nested content.\n");

      string answer = JsonSerializer.Serialize(new { keptIds = new[] { "i1", "i2" } });
      var auxiliary = new ScriptedChatClient(token => TextUpdates([answer], token));
      await using RunningParent parent = await StartParentAsync();
      var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => auxiliary, [], project);
      var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
      var pruner = new StartingResourcePruner(runner, (_, _) => Task.FromResult(TestLlmSelection.DefaultResolution), (status, _, _) => {
        statuses.Enqueue(status);
        return Task.CompletedTask;
      }, "Apply the project guidance.");
      await using var catalog = Catalog(root, [rootDocument, nestedDocument, skill]);
      var builder = new StartingContextBuilder(new ToolCatalog([
        TestTool("read"), TestTool("write")
      ]), instructionDiscovery: new InstructionResourceDiscovery(catalog, packaged));
      var preferences = new UserPreferences(project, null) {
        ProtectedResources = new() { Tools = ["write"], Skills = [skill], Instructions = [] }
      };
      Guid boundary = Guid.NewGuid();

      StartingContext context = await builder.PrepareAsync(
          new(SessionKind.Primary, ModelFamily.Other, project), boundary, "Apply the project guidance.",
          preferences, new HashSet<string>(["read"], StringComparer.Ordinal), pruner, CancellationToken.None)
          .WaitAsync(TestTimeout);

      string[] instructions = context.Instructions.Select(message => message.Text).ToArray();
      int rootIndex = Array.FindIndex(instructions, text => text.Contains("Root rule.", StringComparison.Ordinal));
      int nestedIndex = Array.FindIndex(instructions, text => text.Contains("Nested rule.", StringComparison.Ordinal));
      Assert.Equal(2, rootIndex);
      Assert.Equal(rootIndex + 1, nestedIndex);
      Assert.Equal(6, instructions.Length);
      Assert.Contains(skill, instructions[^2], StringComparison.Ordinal);
      Assert.StartsWith($"Working directory: {project}\n", instructions[^1], StringComparison.Ordinal);
      Assert.Contains("\nTime: ", instructions[^1], StringComparison.Ordinal);
      Assert.Contains(rootDocument, instructions[rootIndex], StringComparison.Ordinal);
      Assert.Contains(nestedDocument, instructions[nestedIndex], StringComparison.Ordinal);
      Assert.Contains(Path.GetDirectoryName(nestedDocument)!, instructions[nestedIndex], StringComparison.Ordinal);
      Assert.Contains("Full nested content.", instructions[nestedIndex], StringComparison.Ordinal);
      Assert.Equal(boundary, context.BoundaryId);
      Assert.Equal(new[] { "read", "write" }, context.Tools.Select(tool => tool.Name));
      Assert.Equal(1, auxiliary.Calls);
      Assert.DoesNotContain("read", auxiliary.LastRequest[^1].Text, StringComparison.Ordinal);
      Assert.DoesNotContain("write", auxiliary.LastRequest[^1].Text, StringComparison.Ordinal);
      Assert.Contains(statuses, status => status.Category == "instructions" && status.State == "completed"
          && status.Total == 2 && status.Retained == 2);
      Assert.Contains(statuses, status => status.Category == "tools" && status.State == "completed"
          && status.Total == 2 && status.Retained == 2);
    } finally {
      if (Directory.Exists(root)) {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
          File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Theory]
  [InlineData(false, false, true, true, true, false)]
  [InlineData(false, false, false, true, false, true)]
  [InlineData(false, true, true, false, true, true)]
  [InlineData(true, false, false, true, true, false)]
  public async Task DuplicateSkillsArePrunedByPathAndProtectedPackagedSkillsSurviveOverride(
      bool protectProject, bool protectPackaged, bool keepProject, bool keepPackaged,
      bool expectProject, bool expectPackaged) {
    string root = Path.Combine(Path.GetTempPath(), $"urd-context-{Guid.NewGuid():N}");
    string project = Path.Combine(root, "project");
    string packaged = Path.Combine(root, "packaged");
    Directory.CreateDirectory(project);
    try {
      string projectSkill = Write(project, "skills/project/SKILL.md", "---\nname: shared\ndescription: Project skill.\n---\n");
      string packagedSkill = Write(packaged, "shared/SKILL.md", "---\nname: shared\ndescription: Packaged skill.\n---\n");
      string packagedId = protectProject ? "s1" : "s2";
      string[] kept = new[] {
        keepProject && !protectProject ? "s1" : null,
        keepPackaged && !protectPackaged ? packagedId : null
      }.OfType<string>().ToArray();
      string answer = JsonSerializer.Serialize(new { keptIds = kept });
      var auxiliary = new ScriptedChatClient(token => TextUpdates([answer], token));
      await using RunningParent parent = await StartParentAsync();
      var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => auxiliary, [], project);
      var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
      var pruner = new StartingResourcePruner(runner, (_, _) => Task.FromResult(TestLlmSelection.DefaultResolution), (status, _, _) => {
        statuses.Enqueue(status);
        return Task.CompletedTask;
      }, "Use the skill.");
      await using var catalog = Catalog(root, [projectSkill, projectSkill]);
      var builder = new StartingContextBuilder(new ToolCatalog([]),
          instructionDiscovery: new InstructionResourceDiscovery(catalog, packaged));
      var preferences = new UserPreferences(project, null) {
        ProtectedResources = new() {
          Tools = [], Skills = new[] {
            protectProject ? projectSkill : null, protectPackaged ? packagedSkill : null
          }.OfType<string>().ToArray(), Instructions = []
        }
      };

      StartingContext context = await builder.PrepareAsync(
          new(SessionKind.Primary, ModelFamily.Other, project), Guid.NewGuid(), "Use the skill.",
          preferences, new HashSet<string>(StringComparer.Ordinal), pruner, CancellationToken.None)
          .WaitAsync(TestTimeout);

      string text = string.Join("\n", context.Instructions.Select(message => message.Text));
      Assert.Equal(expectProject, text.Contains("Project skill.", StringComparison.Ordinal));
      Assert.Equal(expectPackaged, text.Contains("Packaged skill.", StringComparison.Ordinal));
      Assert.Equal(1, auxiliary.Calls);
      string request = auxiliary.LastRequest[^1].Text;
      Assert.Equal(!protectProject, request.Contains("Project skill.", StringComparison.Ordinal));
      Assert.Equal(!protectPackaged, request.Contains("Packaged skill.", StringComparison.Ordinal));
      int retained = (protectProject || keepProject ? 1 : 0) + (protectPackaged || keepPackaged ? 1 : 0);
      Assert.Contains(statuses, status => status.Category == "skills" && status.State == "completed"
          && status.Total == 2 && status.Retained == retained);
    } finally {
      if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task IgnoredSourcesNeverReachTheSelectorOrStartingContextEvenWhenProtected() {
    string root = Path.Combine(Path.GetTempPath(), $"urd-context-{Guid.NewGuid():N}");
    string project = Path.Combine(root, "project");
    string packaged = Path.Combine(root, "packaged");
    try {
      string ignoredInstruction = Write(project, "CLAUDE.md", "Ignored instruction.");
      string protectedInstruction = Write(project, "AGENTS.md", "Protected instruction.");
      string ignoredSkill = Write(packaged, "secret/SKILL.md", "---\nname: secret\ndescription: Ignored skill.\n---\n");
      string localSkill = Write(project, "local/SKILL.md", "---\nname: local\ndescription: Eligible skill.\n---\n");
      var auxiliary = new ScriptedChatClient(token => TextUpdates(["{\"keptIds\":[\"s1\"]}"], token));
      await using RunningParent parent = await StartParentAsync();
      var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => auxiliary, [], project);
      var pruner = new StartingResourcePruner(runner, (_, _) => Task.FromResult(TestLlmSelection.DefaultResolution),
          (_, _, _) => Task.CompletedTask, "Use the resources.");
      await using var catalog = Catalog(root, [ignoredInstruction, protectedInstruction, localSkill]);
      var builder = new StartingContextBuilder(new ToolCatalog([]), new InstructionResourceDiscovery(catalog, packaged));
      var preferences = new UserPreferences(project, null) {
        SourceIgnores = new() { Skills = [packaged] },
        ProtectedResources = new() { Instructions = [ignoredInstruction, protectedInstruction], Skills = [ignoredSkill] },
      };

      StartingContext context = await builder.PrepareAsync(
          new(SessionKind.Primary, ModelFamily.Other, project), Guid.NewGuid(), "Use the resources.",
          preferences, new HashSet<string>(StringComparer.Ordinal), pruner, CancellationToken.None)
          .WaitAsync(TestTimeout);

      string text = string.Join("\n", context.Instructions.Select(message => message.Text));
      Assert.DoesNotContain("Ignored", text, StringComparison.Ordinal);
      Assert.Contains("Protected instruction.", text, StringComparison.Ordinal);
      Assert.Contains("Eligible skill.", text, StringComparison.Ordinal);
      Assert.Equal(1, auxiliary.Calls);
      Assert.DoesNotContain("Ignored", auxiliary.LastRequest[^1].Text, StringComparison.Ordinal);
    } finally {
      if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task MissingProtectedResourcesSurviveAnIncompleteCatalogWithoutWaiting(bool stale) {
    string root = Path.Combine(Path.GetTempPath(), $"urd-context-{Guid.NewGuid():N}");
    string project = Path.Combine(root, "project");
    string packaged = Path.Combine(root, "packaged");
    Directory.CreateDirectory(project);
    try {
      string document = Write(project, "AGENTS.md", "Root rule.\n");
      string skill = Write(project, "skills/local/SKILL.md", "---\nname: local\ndescription: Local skill.\n---\n");
      if (stale) {
        await using var previous = Catalog(root, [document, skill]);
        await previous.FindPathsAsync(project, CancellationToken.None).WaitAsync(TestTimeout);
      }
      string protectedDocument = Write(project, "rules/AGENTS.md", "Protected rule.\n");
      string protectedSkill = Write(project, "skills/protected/SKILL.md",
          "---\nname: protected\ndescription: Protected skill.\n---\nNot advertised.\n");
      string ignoredDocument = Write(project, "ignored/CLAUDE.md", "Ignored rule.\n");
      string outsideDocument = Write(root, "sibling/AGENTS.md", "Sibling rule.\n");
      string excludedDocument = Write(project, "node_modules/AGENTS.md", "Excluded rule.\n");
      Write(project, "pending/AGENTS.md", "Unlisted optional rule.\n");
      string answer = JsonSerializer.Serialize(new { keptIds = new[] { "s1", "i1" } });
      var auxiliary = new ScriptedChatClient(token => TextUpdates([answer], token));
      await using RunningParent parent = await StartParentAsync();
      var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => auxiliary, [], project);
      var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
      var pruner = new StartingResourcePruner(runner, (_, _) => Task.FromResult(TestLlmSelection.DefaultResolution), (status, _, _) => {
        statuses.Enqueue(status);
        return Task.CompletedTask;
      }, "Use the resources.");
      var scanStarted = Signal();
      await using var catalog = new InstructionResourceCatalog(Path.Combine(root, "catalog.db"),
          (_, matched, cancellation) => {
            if (!stale) {
              matched(document);
              matched(skill);
            }
            scanStarted.SetResult();
            return new TaskCompletionSource<string[]>().Task.WaitAsync(cancellation);
          }, (_, _, _) => null);
      catalog.Warm(project);
      await scanStarted.Task.WaitAsync(TestTimeout);
      var builder = new StartingContextBuilder(new ToolCatalog([]),
          instructionDiscovery: new InstructionResourceDiscovery(catalog, packaged));
      var preferences = new UserPreferences(project, null) {
        ProtectedResources = new() {
          Tools = [], Skills = [protectedSkill],
          Instructions = [protectedDocument, ignoredDocument, outsideDocument, excludedDocument]
        }
      };

      StartingContext context = await builder.PrepareAsync(
          new(SessionKind.Primary, ModelFamily.Other, project), Guid.NewGuid(), "Use the resources.",
          preferences, new HashSet<string>(StringComparer.Ordinal), pruner, CancellationToken.None)
          .WaitAsync(TestTimeout);

      string text = string.Join("\n", context.Instructions.Select(message => message.Text));
      Assert.Contains("Root rule.", text, StringComparison.Ordinal);
      Assert.Contains("Local skill.", text, StringComparison.Ordinal);
      Assert.Contains("Protected rule.", text, StringComparison.Ordinal);
      Assert.Contains(Path.GetDirectoryName(protectedDocument)!, text, StringComparison.Ordinal);
      Assert.Contains("Protected skill.", text, StringComparison.Ordinal);
      Assert.DoesNotContain("Not advertised.", text, StringComparison.Ordinal);
      Assert.DoesNotContain("Ignored rule.", text, StringComparison.Ordinal);
      Assert.DoesNotContain("Sibling rule.", text, StringComparison.Ordinal);
      Assert.DoesNotContain("Excluded rule.", text, StringComparison.Ordinal);
      Assert.DoesNotContain("Unlisted optional rule.", text, StringComparison.Ordinal);
      Assert.Equal(2, auxiliary.Calls);
      string expected = stale ? $"Index of {project} is being refreshed; results may be stale."
          : $"Index of {project} incomplete: 2 files found so far.";
      Assert.Contains(statuses, status => status.Category == "tools" && status.State == "completed" && status.Error is null);
      Assert.Contains(statuses, status => status.Category == "skills" && status.State == "completed"
          && status.Error == expected && status.Total == 2 && status.Retained == 2);
      Assert.Contains(statuses, status => status.Category == "instructions" && status.State == "completed"
          && status.Error == expected && status.Total == 2 && status.Retained == 2);
    } finally {
      if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task InheritedSelectionReplacesToolAndInstructionPruningWhileSkillsStillPrune() {
    string root = Path.Combine(Path.GetTempPath(), $"urd-context-{Guid.NewGuid():N}");
    string project = Path.Combine(root, "project");
    string packaged = Path.Combine(root, "packaged");
    try {
      string rootDocument = Write(project, "AGENTS.md", "Root rule.\n");
      string nestedDocument = Write(project, Path.Combine("nested", "AGENTS.md"), "Nested rule.\n");
      string skill = Write(project, "skills/local/SKILL.md", "---\nname: local\ndescription: Local skill.\n---\n");
      var auxiliary = new ScriptedChatClient(token => TextUpdates(["{\"keptIds\":[\"s1\"]}"], token));
      await using RunningParent parent = await StartParentAsync();
      var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => auxiliary, [], project);
      var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
      var pruner = new StartingResourcePruner(runner, (_, _) => Task.FromResult(TestLlmSelection.DefaultResolution), (status, _, _) => {
        statuses.Enqueue(status);
        return Task.CompletedTask;
      }, "Use the resources.");
      await using var catalog = Catalog(root, [rootDocument, nestedDocument, skill]);
      var builder = new StartingContextBuilder(new ToolCatalog([TestTool("read"), TestTool("write"), TestTool("grep")]),
          new InstructionResourceDiscovery(catalog, packaged));
      var preferences = new UserPreferences(project, null) {
        ProtectedResources = new() { Tools = ["write"], Skills = [], Instructions = [] }
      };
      var inherited = new StartingContext([], [AgentToolDeclaration.Create(TestTool("read")),
          AgentToolDeclaration.Create(TestTool("parent_only"))]) { InstructionPaths = [rootDocument] };

      StartingContext context = await builder.PrepareAsync(
          new(SessionKind.Subagent, ModelFamily.Other, project), Guid.NewGuid(), "Use the resources.",
          preferences, new HashSet<string>(StringComparer.Ordinal), pruner, CancellationToken.None, inherited)
          .WaitAsync(TestTimeout);

      string text = string.Join("\n", context.Instructions.Select(message => message.Text));
      Assert.Contains("Root rule.", text, StringComparison.Ordinal);
      Assert.DoesNotContain("Nested rule.", text, StringComparison.Ordinal);
      Assert.Contains("Local skill.", text, StringComparison.Ordinal);
      Assert.Equal(new[] { "read", "write" }, context.Tools.Select(tool => tool.Name));
      Assert.Equal(new[] { rootDocument }, context.InstructionPaths);
      Assert.Equal(1, auxiliary.Calls);
      Assert.All(statuses, status => Assert.Equal("skills", status.Category));
    } finally {
      if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
    }
  }

  private static TestAgentTool TestTool(string name) =>
      new TestAgentTool(name, (_, _, _) => Task.FromResult(new ToolOutput("unused", [])));

  private static string Write(string project, string relativePath, string text) {
    string path = Path.Combine(project, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
    return Path.GetFullPath(path);
  }

  private static InstructionResourceCatalog Catalog(string root, string[] paths) =>
      new(Path.Combine(root, "catalog.db"), (_, _, _) => Task.FromResult(paths), (_, _, _) => null);

  private static async Task<RunningParent> StartParentAsync() {
    var mainWaiting = Signal();
    var mainRelease = Signal();
    var main = new ScriptedChatClient(token => Paused(mainWaiting, mainRelease, token));
    var fixture = new ExecutionFixture(main);
    Guid executionId = await fixture.StartExecutionAsync("main input");
    await mainWaiting.Task.WaitAsync(TestTimeout);
    return new(fixture, executionId, mainRelease);
  }

  private sealed class RunningParent(ExecutionFixture fixture, Guid executionId, TaskCompletionSource mainRelease)
      : IAsyncDisposable {
    internal ExecutionFixture Fixture => fixture;

    public async ValueTask DisposeAsync() {
      mainRelease.TrySetResult();
      await fixture.Session.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout);
      await fixture.DisposeAsync();
    }
  }
}
