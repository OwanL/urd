using System.Runtime.Versioning;
using System.Text.Json;

namespace Urd.Core.Tools;

// Runs one finite shell call; a completed shell may leave servers owned by the chat.
[SupportedOSPlatform("windows")]
internal sealed class BashTool(ChatProcesses processes, GitBash bash, IReadOnlyDictionary<string, string> environment) : AgentTool {
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

  public override string Name => "bash";
  public override string Description => """
    Execute a Git Bash command in the current working directory. Returns stdout and stderr.
    Output is truncated to the last 2000 lines or 50KB, whichever comes first.
    If truncated, full output is saved to a temp file.
    Optionally provide a timeout in seconds; there is no default timeout.
    """;
  public override JsonElement ParametersSchema => schema;

  public override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) =>
      command.RunAsync(arguments, context, cancellation);
}
