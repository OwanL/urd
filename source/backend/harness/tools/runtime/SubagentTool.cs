using System.Text.Json;

namespace Urd.Core.Tools;

internal sealed class SubagentTool : HarnessTool {
  internal const string ToolName = "subagent";

  public override string Name => ToolName;
  public override ToolRelevance RelevantWhen => ToolRelevance.Always;
  public override string Description => "Delegate a self-contained task to a fresh agent and return its final report.";
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {
        "bucket": { "type": "string", "enum": ["small", "medium", "large"], "description": "Pick the smallest bucket sufficient: cost and latency grow with tier. small: bounded, well-defined work; medium: substantial judgment across interacting concerns; large: difficult reasoning, diagnosis or design." },
        "includeUserInput": { "type": "boolean", "description": "Include the parent’s human messages and answered ask_user questions, with their context and choices, as quoted history." },
        "task": { "type": "string" }
      },
      "required": ["bucket", "includeUserInput", "task"],
      "additionalProperties": false
    }
    """);

  public override async Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context, CancellationToken cancellation) {
    string bucket = arguments.GetProperty("bucket").GetString() ?? throw new ArgumentException("Bucket is required.");
    bool includeUserInput = arguments.GetProperty("includeUserInput").GetBoolean();
    string task = arguments.GetProperty("task").GetString() ?? throw new ArgumentException("Task is required.");
    SubagentRunResult result = await context.Sessions.RunSubagentAsync(context, new(bucket, includeUserInput, task), cancellation);
    return result.Error is { } error
        ? new ToolOutput(error, [], IsError: true)
        : new ToolOutput(result.Report ?? "The subagent task produced no report.", [], IsError: result.Report is null);
  }
}
