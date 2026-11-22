using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Providers;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Context;

public sealed class StartingContextBuilderTests {
  [Fact]
  public void SharedSessionAndDirectoryInstructionsUseFixedOrder() {
    const string workingDirectory = "C:\\workspace\\urd";
    var manager = CreateManager();

    StartingContext primary = manager.Create(
        new(SessionKind.Primary, ModelFamily.Other, workingDirectory), default);
    StartingContext subagent = manager.Create(
        new(SessionKind.Subagent, ModelFamily.Other, null), default);

    Assert.Equal(3, primary.Instructions.Count);
    string shared = primary.Instructions[0].Text;
    Assert.Contains("You are Urd", shared, StringComparison.Ordinal);
    Assert.Contains("Read the full normative contract", shared, StringComparison.Ordinal);
    Assert.Equal(shared, subagent.Instructions[0].Text);

    string primaryGuidance = primary.Instructions[1].Text;
    Assert.Contains("Help the user directly", primaryGuidance, StringComparison.Ordinal);
    Assert.Contains("# Default working guidance", primaryGuidance, StringComparison.Ordinal);
    Assert.Contains("Ask before materially expanding scope.", primaryGuidance, StringComparison.Ordinal);
    Assert.StartsWith($"Working directory: {workingDirectory}\n", primary.Instructions[2].Text, StringComparison.Ordinal);

    Assert.Equal(3, subagent.Instructions.Count);
    Assert.StartsWith("Operating system:", subagent.Instructions[2].Text, StringComparison.Ordinal);
    string subagentGuidance = subagent.Instructions[1].Text;
    Assert.Contains("Stay within its scope", subagentGuidance, StringComparison.Ordinal);
    Assert.Contains("Start with a plain-language outcome sentence", subagentGuidance, StringComparison.Ordinal);
    Assert.DoesNotContain(subagent.Instructions, message => message.Text.StartsWith("Working directory:", StringComparison.Ordinal));
    Assert.All(primary.Instructions.Concat(subagent.Instructions), message => Assert.Equal(ChatRole.System, message.Role));
  }

  [Fact]
  public void EnvironmentIncludesDirectoryOperatingSystemAndLocalSnapshotTime() {
    var now = new DateTimeOffset(2026, 11, 22, 21, 58, 0, TimeSpan.Zero);
    var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Zone", TimeSpan.FromHours(13), "Test", "Test");
    var manager = new StartingContextBuilder(new ToolCatalog([]), timeProvider: new FixedTimeProvider(now, zone));

    StartingContext context = manager.Create(new(SessionKind.Primary, ModelFamily.Other, "C:\\workspace\\urd"), default);
    string environment = context.Instructions[2].Text;

    Assert.Contains("Working directory: C:\\workspace\\urd\n", environment, StringComparison.Ordinal);
    Assert.Contains("Operating system: ", environment, StringComparison.Ordinal);
    Assert.Contains("Time: 2026-11-23 10:58 +13:00 (Test/Zone) as of context creation", environment, StringComparison.Ordinal);
  }

  [Fact]
  public void ImportedGuidanceDoesNotClaimUnavailableToolsOrPieFeatures() {
    var manager = CreateManager();
    foreach (SessionKind kind in new[] { SessionKind.Primary, SessionKind.Subagent }) {
      StartingContext context = manager.Create(
          new(kind, ModelFamily.Other, "C:\\workspace\\urd"), default);
      string text = InstructionText(context);

      foreach (string unsupported in new[] { "ask_user", "session_resources", "session_changes", "request_capability", "invoke_tool", "Pie", "Pi runtime" })
        Assert.DoesNotContain(unsupported, text, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void ToolsKeepCatalogOrderAndUseTheExistingBackgroundDeclaration() {
    var manager = CreateManager(new TestTool("first"), new TestTool("second"));

    StartingContext context = manager.Create(
        new(SessionKind.Primary, ModelFamily.Google, null), default);

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
        new(SessionKind.Primary, ModelFamily.Other, null), default);

    Assert.True(Assert.IsAssignableFrom<IList<ChatMessage>>(context.Instructions).IsReadOnly);
    Assert.True(Assert.IsAssignableFrom<IList<AIFunctionDeclaration>>(context.Tools).IsReadOnly);
  }

  [Fact]
  public void CancellationStopsCompositionBeforeReturningAContext() {
    var manager = CreateManager();
    using var source = new CancellationTokenSource();
    source.Cancel();

    OperationCanceledException exception = Assert.ThrowsAny<OperationCanceledException>(() =>
        manager.Create(new(SessionKind.Primary, ModelFamily.Other, null), source.Token));

    Assert.Equal(source.Token, exception.CancellationToken);
  }

  [Fact]
  public void UnsupportedSessionKindIsRejected() {
    var manager = CreateManager();

    Assert.Throws<ArgumentOutOfRangeException>(() =>
        manager.Create(new((SessionKind)int.MaxValue, ModelFamily.Other, null), default));
  }

  private static StartingContextBuilder CreateManager(params AgentTool[] tools) => new(new ToolCatalog(tools));

  private static string InstructionText(StartingContext context) =>
      string.Join("\n", context.Instructions.Select(message => message.Text));

  private sealed class FixedTimeProvider(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider {
    public override DateTimeOffset GetUtcNow() => now;
    public override TimeZoneInfo LocalTimeZone => zone;
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
