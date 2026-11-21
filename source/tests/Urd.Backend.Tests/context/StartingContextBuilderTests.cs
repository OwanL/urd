using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Providers;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Context;

public sealed class StartingContextBuilderTests {
  [Fact]
  public void SessionKindAndAssignmentComposeWithSharedInstructions() {
    const string assignment = "Inspect the specified behavior and report back.";
    var manager = CreateManager();

    StartingContext primary = manager.Create(
        new(SessionKind.Primary, ModelFamily.OpenAI, null, assignment), default);
    StartingContext subagent = manager.Create(
        new(SessionKind.Subagent, ModelFamily.OpenAI, null, assignment), default);
    string primaryText = InstructionText(primary);
    string subagentText = InstructionText(subagent);

    Assert.Contains("Urd", primaryText, StringComparison.Ordinal);
    Assert.Contains("Urd", subagentText, StringComparison.Ordinal);
    Assert.Contains("user", primaryText, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("final report", subagentText, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("result", subagentText, StringComparison.OrdinalIgnoreCase);
    Assert.Contains(assignment, primaryText, StringComparison.Ordinal);
    Assert.Contains(assignment, subagentText, StringComparison.Ordinal);
    Assert.NotEqual(primaryText, subagentText);
    Assert.DoesNotContain(primary.Instructions, message => message.Role == ChatRole.User);
    Assert.DoesNotContain(subagent.Instructions, message => message.Role == ChatRole.User);
  }

  [Fact]
  public void SessionGuidanceKeepsPrimaryWorkflowAndSubagentHandoffSeparate() {
    var manager = CreateManager();
    var input = new StartingContextInput(SessionKind.Primary, ModelFamily.Other, null, null);

    StartingContext primary = manager.Create(input, default);
    StartingContext subagent = manager.Create(input with { SessionKind = SessionKind.Subagent }, default);
    string primaryText = InstructionText(primary);
    string subagentText = InstructionText(subagent);

    Assert.Contains("# Default working guidance", primaryText, StringComparison.Ordinal);
    Assert.Contains("Ask before materially expanding scope.", primaryText, StringComparison.Ordinal);
    Assert.DoesNotContain("# Working preferences", primaryText, StringComparison.Ordinal);
    Assert.DoesNotContain("final report", primaryText, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("# Default working guidance", subagentText, StringComparison.Ordinal);
    ChatMessage handoff = Assert.Single(subagent.Instructions,
        message => message.Text.Contains("final report", StringComparison.OrdinalIgnoreCase));
    Assert.Contains("Stay within its scope", handoff.Text, StringComparison.Ordinal);
    Assert.Contains("Start with a plain-language outcome sentence", handoff.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void ImportedGuidanceDoesNotClaimUnavailableToolsOrPieFeatures() {
    var manager = CreateManager();
    foreach (SessionKind kind in new[] { SessionKind.Primary, SessionKind.Subagent }) {
      StartingContext context = manager.Create(
          new(kind, ModelFamily.Other, "C:\\workspace\\urd", null), default);
      string text = InstructionText(context);

      foreach (string unsupported in new[] { "ask_user", "session_resources", "session_changes", "request_capability", "invoke_tool", "Pie", "Pi runtime" })
        Assert.DoesNotContain(unsupported, text, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void WorkingDirectoryIsIncludedOnlyWhenAvailable() {
    const string workingDirectory = "C:\\workspace\\urd";
    var manager = CreateManager();
    var input = new StartingContextInput(SessionKind.Primary, ModelFamily.Other, null, null);

    StartingContext withoutDirectory = manager.Create(input, default);
    StartingContext withDirectory = manager.Create(input with { WorkingDirectory = workingDirectory }, default);

    Assert.DoesNotContain("Working directory:", InstructionText(withoutDirectory), StringComparison.Ordinal);
    Assert.DoesNotContain("Assignment context:", InstructionText(withoutDirectory), StringComparison.Ordinal);
    Assert.Contains(workingDirectory, InstructionText(withDirectory), StringComparison.Ordinal);
    Assert.DoesNotContain("Tool guidance:", InstructionText(withDirectory), StringComparison.Ordinal);
  }

  [Fact]
  public void ToolsKeepCatalogOrderAndUseTheExistingBackgroundDeclaration() {
    var manager = CreateManager(new TestTool("first"), new TestTool("second"));

    StartingContext context = manager.Create(
        new(SessionKind.Primary, ModelFamily.Google, null, null), default);

    Assert.Equal(new[] { "first", "second" }, context.Tools.Select(tool => tool.Name));
    JsonElement schema = context.Tools[0].JsonSchema;
    JsonElement properties = schema.GetProperty("properties");
    Assert.Equal("string", properties.GetProperty("value").GetProperty("type").GetString());
    Assert.Equal("boolean", properties.GetProperty("background").GetProperty("type").GetString());
    Assert.Equal(new[] { "value" }, schema.GetProperty("required").EnumerateArray()
        .Select(item => item.GetString()));
  }

  [Fact]
  public void ReturnedCollectionsAreReadOnly() {
    var manager = CreateManager(new TestTool("read"));

    StartingContext context = manager.Create(
        new(SessionKind.Primary, ModelFamily.Other, null, null), default);

    Assert.True(Assert.IsAssignableFrom<IList<ChatMessage>>(context.Instructions).IsReadOnly);
    Assert.True(Assert.IsAssignableFrom<IList<AIFunctionDeclaration>>(context.Tools).IsReadOnly);
  }

  [Fact]
  public void AdditionalGuidanceIsMergedBeforeAndAfterBuiltInGuidance() {
    var manager = CreateManagerWithGuidance(
        new TestGuidance(-1, _ => "before built-ins"),
        new TestGuidance(1000, _ => "after built-ins"));

    StartingContext context = manager.Create(
        new(SessionKind.Primary, ModelFamily.Other, null, "assignment"), default);
    string[] instructions = context.Instructions.Select(message => message.Text).ToArray();

    int beforeBuiltInsIndex = Array.IndexOf(instructions, "before built-ins");
    int baseGuidanceIndex = Array.FindIndex(instructions, text => text.Contains("Urd", StringComparison.Ordinal));
    int assignmentGuidanceIndex = Array.FindIndex(instructions,
        text => text.Contains("Assignment context:", StringComparison.Ordinal));
    int afterBuiltInsIndex = Array.IndexOf(instructions, "after built-ins");

    Assert.True(beforeBuiltInsIndex >= 0 && beforeBuiltInsIndex < baseGuidanceIndex);
    Assert.True(assignmentGuidanceIndex >= 0 && afterBuiltInsIndex > assignmentGuidanceIndex);
  }

  [Fact]
  public void EqualOrderGuidanceOfTheSameTypePreservesSuppliedOrder() {
    var manager = CreateManagerWithGuidance(
        new TestGuidance(50, _ => "first supplied"),
        new TestGuidance(50, _ => "second supplied"));

    StartingContext context = manager.Create(
        new(SessionKind.Primary, ModelFamily.Other, null, null), default);

    string[] instructions = context.Instructions.Select(message => message.Text).ToArray();

    Assert.Equal(new[] { "first supplied", "second supplied" },
        instructions.Where(text => text is "first supplied" or "second supplied"));
  }

  [Fact]
  public void AdditionalGuidanceIsSessionScopedAndEvaluatedForEachSession() {
    var manager = CreateManagerWithGuidance(new TestGuidance(
        -1,
        input => input.SessionKind.ToString(),
        SessionKind.Primary));
    var primaryInput = new StartingContextInput(SessionKind.Primary, ModelFamily.Other, null, null);
    var subagentInput = primaryInput with { SessionKind = SessionKind.Subagent };

    StartingContext primary = manager.Create(primaryInput, default);
    StartingContext subagent = manager.Create(subagentInput, default);

    Assert.Contains(primary.Instructions, message => message.Text == "Primary");
    Assert.DoesNotContain(subagent.Instructions, message => message.Text == "Subagent");
  }

  [Fact]
  public void NullAndWhitespaceAdditionalGuidanceIsOmittedAndAllInstructionsAreSystemMessages() {
    var manager = CreateManagerWithGuidance(
        new TestGuidance(-2, _ => null),
        new TestGuidance(-1, _ => " \t "),
        new TestGuidance(10, _ => "additional instruction"));

    StartingContext context = manager.Create(
        new(SessionKind.Primary, ModelFamily.Other, null, null), default);

    Assert.DoesNotContain(context.Instructions, message => string.IsNullOrWhiteSpace(message.Text));
    Assert.Contains(context.Instructions, message => message.Text == "additional instruction");
    Assert.All(context.Instructions, message => Assert.Equal(ChatRole.System, message.Role));
  }

  [Fact]
  public void CancellationFromGuidancePreventsEvaluatingTheNextGuidance() {
    using var source = new CancellationTokenSource();
    bool nextGuidanceEvaluated = false;
    var manager = CreateManagerWithGuidance(
        new TestGuidance(-2, _ => {
          source.Cancel();
          return "cancelling instruction";
        }),
        new TestGuidance(-1, _ => {
          nextGuidanceEvaluated = true;
          return "must not run";
        }));

    Assert.ThrowsAny<OperationCanceledException>(() =>
        manager.Create(new(SessionKind.Primary, ModelFamily.Other, null, null), source.Token));

    Assert.False(nextGuidanceEvaluated);
  }

  [Fact]
  public void GuidanceExceptionsPropagate() {
    var expected = new InvalidOperationException("guidance failed");
    var manager = CreateManagerWithGuidance(new TestGuidance(-1, _ => throw expected));

    InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
        manager.Create(new(SessionKind.Primary, ModelFamily.Other, null, null), default));

    Assert.Same(expected, actual);
  }

  [Fact]
  public void CancellationStopsCompositionBeforeReturningAContext() {
    var manager = CreateManager();
    using var source = new CancellationTokenSource();
    source.Cancel();

    OperationCanceledException exception = Assert.ThrowsAny<OperationCanceledException>(() =>
        manager.Create(new(SessionKind.Primary, ModelFamily.Other, null, null), source.Token));

    Assert.Equal(source.Token, exception.CancellationToken);
  }

  private static StartingContextBuilder CreateManager(params AgentTool[] tools) => new(new ToolCatalog(tools));

  private static StartingContextBuilder CreateManagerWithGuidance(params StartingContextContribution[] guidance) =>
      new(new ToolCatalog([]), guidance);

  private static string InstructionText(StartingContext context) =>
      string.Join("\n", context.Instructions.Select(message => message.Text));

  private sealed class TestGuidance(
      int order,
      Func<StartingContextInput, string?> getText,
      SessionKind? sessionScope = null) : StartingContextContribution {
    public override int Order => order;
    public override bool AppliesTo(StartingContextInput input) =>
        sessionScope is null || input.SessionKind == sessionScope;
    public override string? GetText(StartingContextInput input) => getText(input);
  }

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
