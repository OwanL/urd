using System.Text.Json;
using Urd.Core.Context;

namespace Urd.Core.Tools;

internal sealed class SubagentTool : HarnessTool {
  public override string Name => "subagent";
  public override ToolRelevance RelevantWhen => ToolRelevance.Always;
  public override string Description => "Delegate a self-contained task to a fresh agent and return its final report.";
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {
        "bucket": { "type": "string", "enum": ["small", "medium", "large"] },
        "context": { "type": "string", "enum": ["none", "user", "recent"] },
        "task": { "type": "string" }
      },
      "required": ["bucket", "context", "task"],
      "additionalProperties": false
    }
    """);

  public override async Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context, CancellationToken cancellation) {
    string bucket = arguments.GetProperty("bucket").GetString() ?? throw new ArgumentException("Bucket is required.");
    SubagentContextMode mode = arguments.GetProperty("context").GetString() switch {
      "none" => SubagentContextMode.None,
      "user" => SubagentContextMode.User,
      "recent" => SubagentContextMode.Recent,
      _ => throw new ArgumentException("Unknown subagent context; expected none, user, or recent.")
    };
    string task = arguments.GetProperty("task").GetString() ?? throw new ArgumentException("Task is required.");
    SubagentRunResult result = await context.Sessions.RunSubagentAsync(context, new(bucket, mode, task), cancellation);
    return result.Error is { } error
        ? new ToolOutput(error, [], IsError: true)
        : new ToolOutput(result.Report ?? "The subagent task produced no report.", [], IsError: result.Report is null);
  }
}
