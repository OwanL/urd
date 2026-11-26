using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Execution;

public sealed class ToolInvokerWorkingDirectoryTests {
  [Fact]
  public async Task ToolWithoutWorkingDirectoryRequirementRunsWithoutSavedFolder() {
    string? workingDirectory = null;
    var tool = new NoDirectoryTool((_, context, _) => {
      workingDirectory = context.WorkingDirectory;
      return Task.FromResult(new ToolOutput("ran", []));
    });

    LlmToolResult result = await InvokeAsync(tool, workingDirectory: null);

    Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
    Assert.Equal(string.Empty, workingDirectory);
  }

  [Fact]
  public async Task WorkingDirectoryIsRequiredByDefault() {
    bool invoked = false;
    var tool = new TestAgentTool("requires_folder", (_, _, _) => {
      invoked = true;
      return Task.FromResult(new ToolOutput("ran", []));
    });

    LlmToolResult result = await InvokeAsync(tool, workingDirectory: null);

    Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
    Assert.Equal("The chat has no saved working folder.", result.Output.Text);
    Assert.False(invoked);
  }

  private static Task<LlmToolResult> InvokeAsync(AgentTool tool, string? workingDirectory) {
    var call = new LlmToolCall("call", tool.Name, JsonSerializer.SerializeToElement(new { }));
    return ToolInvoker.InvokeAsync(Guid.NewGuid(), call, [tool], [AgentToolDeclaration.Create(tool)],
        workingDirectory, supportsImages: false, background: false, CancellationToken.None);
  }

  private sealed class NoDirectoryTool(
      Func<JsonElement, ToolContext, CancellationToken, Task<ToolOutput>> run) : AgentTool {
    private static readonly JsonElement schema = JsonSerializer.SerializeToElement(new { type = "object" });

    public override string Name => "optional_folder";
    public override ToolRelevance RelevantWhen => ToolRelevance.Always;
    public override string Description => "Test tool";
    public override JsonElement ParametersSchema => schema;
    internal override bool RequiresWorkingDirectory => false;

    public override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) =>
        run(arguments, context, cancellation);
  }
}
