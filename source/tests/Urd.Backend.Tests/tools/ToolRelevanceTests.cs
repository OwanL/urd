using Urd.Core.Providers;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class ToolRelevanceTests {
  [Fact]
  public async Task BuiltInToolsAreAlwaysRelevantAndKeepTheirAgentDescriptions() {
    if (!OperatingSystem.IsWindows())
      return;
    var bash = new GitBash();
    await using var processes = new ChatProcesses(bash);
    var files = new FileMutationQueue();
    var pending = new PendingToolCalls();
    AgentTool[] tools = [
      new BashTool(processes, bash, new Dictionary<string, string>()),
      new ReadTool(files), new WriteTool(files), new EditTool(files),
      new ListPendingCallsTool(pending), new CancelPendingCallsTool(pending)
    ];

    foreach (AgentTool tool in tools) {
      Assert.Same(ToolRelevance.Always, tool.RelevantWhen);
      Assert.Equal(tool.Description, AgentToolDeclaration.Create(tool).Description);
    }
  }

}
