using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Tools;

namespace Urd.Backend.Tests.Support;

// Test-only SDK fixture bridge: readable text with the original JSON inspection details.
internal sealed class TestAgentTool : AgentTool {
  private static readonly JsonElement emptySchema = JsonSerializer.SerializeToElement(new { type = "object" });
  private readonly Func<JsonElement, ToolContext, CancellationToken, Task<ToolOutput>> run;

  internal TestAgentTool(string name, Func<JsonElement, ToolContext, CancellationToken, Task<ToolOutput>> run) {
    Name = name;
    this.run = run;
  }

  internal TestAgentTool(AIFunction function) : this(function.Name, async (arguments, _, cancellation) => {
    var values = new AIFunctionArguments(arguments.EnumerateObject()
        .ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal));
    object? result = await function.InvokeAsync(values, cancellation);
    JsonElement details = LlmResponseAccumulator.FreezeJson(result);
    return new ToolOutput(details.ToString(), [], details);
  }) {
    Description = function.Description;
    ParametersSchema = function.JsonSchema;
  }

  public override string Name { get; }
  public override string Description { get; } = "Test tool";
  public override JsonElement ParametersSchema { get; } = emptySchema;

  public override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) =>
      run(arguments, context, cancellation);

  internal static ToolCatalog Catalog(IReadOnlyList<AIFunction>? functions) =>
      new(functions?.Select(function => (AgentTool)new TestAgentTool(function)).ToArray() ?? []);
}
