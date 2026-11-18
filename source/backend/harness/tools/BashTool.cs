using System.Runtime.Versioning;
using System.Text.Json;

namespace Urd.Core.Tools;

// Runs one finite shell call; a completed shell may leave servers owned by the chat.
[SupportedOSPlatform("windows")]
internal sealed class BashTool(ChatProcesses processes, GitBash bash, IReadOnlyDictionary<string, string> environment) : AgentTool {
  public override string Name => "bash";
  public override ToolRelevance RelevantWhen => ToolRelevance.Always;
  public override string Description => $"""
    Run a Git Bash command in the working directory. Optional timeout; no default timeout.
    Long output is truncated and saved to a temp file.
    """;
  public override JsonElement ParametersSchema => schema;

  private static readonly JsonElement schema = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {
        "command": { "type": "string", "description": "Bash command to execute" },
        "timeout": { "type": "number", "description": "Timeout in seconds (optional, no default timeout)", "exclusiveMinimum": 0 }
      },
      "required": ["command"]
    }
    """);
  private readonly BashCommand command = new(processes, bash, environment);

  public override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) =>
      command.RunAsync(arguments, context, cancellation);
}
