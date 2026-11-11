using Urd.Core.Providers;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class ToolRelevanceTests {
  [Fact]
  public async Task BuiltInToolsKeepSelectionGuidanceSeparateFromAgentDescriptions() {
    if (!OperatingSystem.IsWindows())
      return;
    var bash = new GitBash();
    await using var processes = new ChatProcesses(bash);
    var files = new FileMutationQueue();
    var pending = new PendingToolCalls();
    var shell = new BashTool(processes, bash, new Dictionary<string, string>());
    AgentTool[] conditional = [
      new ReadTool(files), new WriteTool(files), new EditTool(files),
      new ListPendingCallsTool(pending), new CancelPendingCallsTool(pending)
    ];

    Assert.Same(ToolRelevance.Always, shell.RelevantWhen);
    foreach (AgentTool tool in conditional) {
      string guidance = Assert.IsType<ToolRelevance.Conditional>(tool.RelevantWhen).Guidance;
      Assert.False(string.IsNullOrWhiteSpace(guidance));
      Assert.NotEqual(tool.Description, guidance);
    }
    foreach (AgentTool tool in conditional.Append(shell))
      Assert.Equal(tool.Description, AgentToolDeclaration.Create(tool).Description);
  }

  [Fact]
  public void AlwaysIsASingletonDistinctFromConditionalRelevance() {
    Assert.Same(ToolRelevance.Always, ToolRelevance.Always);
    Assert.NotEqual(ToolRelevance.Always, ToolRelevance.When("inspect files"));
  }

  [Fact]
  public void WhenPreservesGuidanceAndConditionalsHaveValueEquality() {
    const string guidance = "Inspect files when the task requires it.";

    var relevance = ToolRelevance.When(guidance);

    Assert.Equal(guidance, Assert.IsType<ToolRelevance.Conditional>(relevance).Guidance);
    Assert.Equal(ToolRelevance.When(guidance), relevance);
    Assert.NotEqual(relevance, ToolRelevance.When("Use a different tool."));
  }

  [Fact]
  public void WhenRejectsNullAndBlankGuidance() {
    Assert.Throws<ArgumentNullException>(() => ToolRelevance.When(null!));
    Assert.Throws<ArgumentException>(() => ToolRelevance.When(""));
    Assert.Throws<ArgumentException>(() => ToolRelevance.When(" \t\r\n "));
  }
}
