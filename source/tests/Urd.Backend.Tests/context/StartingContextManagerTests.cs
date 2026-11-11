using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Providers;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Context;

public sealed class StartingContextManagerTests {
  [Fact]
  public async Task SessionKindAndAssignmentComposeWithSharedInstructionsWithoutAddingTheRequest() {
    const string initialRequest = "initial request is supplied separately";
    const string assignment = "Inspect the specified behavior and report back.";
    var manager = CreateManager();

    StartingContext primary = await manager.CreateAsync(
        new(SessionKind.Primary, ModelFamily.OpenAI, null, initialRequest, assignment), default);
    StartingContext subagent = await manager.CreateAsync(
        new(SessionKind.Subagent, ModelFamily.OpenAI, null, initialRequest, assignment), default);
    string primaryText = InstructionText(primary);
    string subagentText = InstructionText(subagent);

    Assert.Contains("Urd", primaryText, StringComparison.Ordinal);
    Assert.Contains("Urd", subagentText, StringComparison.Ordinal);
    Assert.Contains("user", primaryText, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("assigned task", subagentText, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("result", subagentText, StringComparison.OrdinalIgnoreCase);
    Assert.Contains(assignment, primaryText, StringComparison.Ordinal);
    Assert.Contains(assignment, subagentText, StringComparison.Ordinal);
    Assert.NotEqual(primaryText, subagentText);
    Assert.DoesNotContain(initialRequest, primaryText, StringComparison.Ordinal);
    Assert.DoesNotContain(initialRequest, subagentText, StringComparison.Ordinal);
    Assert.DoesNotContain(primary.Instructions, message => message.Role == ChatRole.User);
    Assert.DoesNotContain(subagent.Instructions, message => message.Role == ChatRole.User);
  }

  [Fact]
  public async Task WorkingDirectoryIsIncludedOnlyWhenAvailable() {
    const string workingDirectory = "C:\\workspace\\urd";
    var manager = CreateManager();
    var input = new StartingContextInput(SessionKind.Primary, ModelFamily.Other, null, "request", null);

    StartingContext withoutDirectory = await manager.CreateAsync(input, default);
    StartingContext withDirectory = await manager.CreateAsync(input with { WorkingDirectory = workingDirectory }, default);

    Assert.DoesNotContain("Working directory:", InstructionText(withoutDirectory), StringComparison.Ordinal);
    Assert.DoesNotContain("Assignment context:", InstructionText(withoutDirectory), StringComparison.Ordinal);
    Assert.Contains(workingDirectory, InstructionText(withDirectory), StringComparison.Ordinal);
  }

  [Fact]
  public async Task ToolsKeepCatalogOrderAndUseTheExistingBackgroundDeclaration() {
    var manager = CreateManager(new TestTool("first"), new TestTool("second"));

    StartingContext context = await manager.CreateAsync(
        new(SessionKind.Primary, ModelFamily.Google, null, "request", null), default);

    Assert.Equal(new[] { "first", "second" }, context.Tools.Select(tool => tool.Name));
    JsonElement schema = context.Tools[0].JsonSchema;
    JsonElement properties = schema.GetProperty("properties");
    Assert.Equal("string", properties.GetProperty("value").GetProperty("type").GetString());
    Assert.Equal("boolean", properties.GetProperty("background").GetProperty("type").GetString());
    Assert.Equal(new[] { "value" }, schema.GetProperty("required").EnumerateArray()
        .Select(item => item.GetString()));
  }

  [Fact]
  public async Task ReturnedCollectionsAreReadOnly() {
    var manager = CreateManager(new TestTool("read"));

    StartingContext context = await manager.CreateAsync(
        new(SessionKind.Primary, ModelFamily.Other, null, "request", null), default);

    Assert.True(Assert.IsAssignableFrom<IList<ChatMessage>>(context.Instructions).IsReadOnly);
    Assert.True(Assert.IsAssignableFrom<IList<AIFunctionDeclaration>>(context.Tools).IsReadOnly);
  }

  [Fact]
  public async Task CancellationStopsCompositionBeforeReturningAContext() {
    var manager = CreateManager();
    using var source = new CancellationTokenSource();
    await source.CancelAsync();

    OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        manager.CreateAsync(new(SessionKind.Primary, ModelFamily.Other, null, "request", null), source.Token));

    Assert.Equal(source.Token, exception.CancellationToken);
  }

  private static StartingContextManager CreateManager(params AgentTool[] tools) => new(new ToolCatalog(tools));

  private static string InstructionText(StartingContext context) =>
      string.Join("\n", context.Instructions.Select(message => message.Text));

  private sealed class TestTool(string name) : AgentTool {
    public override string Name => name;
    public override string Description => "Starting-context fixture";
    public override ToolRelevance RelevantWhen => ToolRelevance.Always;
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new {
      type = "object",
      properties = new { value = new { type = "string" } },
      required = new[] { "value" }
    });
    public override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) =>
        Task.FromResult(new ToolOutput("unused", []));
  }
}
