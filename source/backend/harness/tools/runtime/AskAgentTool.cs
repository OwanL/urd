using System.Text.Json;
using Urd.Core.Sessions;

namespace Urd.Core.Tools;

// Asks one live agent a question and settles with its answer. The recipient is an exact final
// title from list_agents, or the parent alias. With wake false the ask runs in the background
// and may never be answered; with wake true it blocks unless background is requested.
internal sealed class AskAgentTool : HarnessTool {
  public override string Name => "ask_agent";
  public override ToolRelevance RelevantWhen => ToolRelevance.Always;
  public override string Description => "Ask one live agent a question and receive its answer as this ask's result.";
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {
        "recipient": { "type": "string", "description": "The agent's exact final title, or 'parent'." },
        "prompt": { "type": "string" },
        "wake": { "type": "boolean", "description": "Whether the question should wake the recipient. False sends an opportunistic background ask that may never be answered." }
      },
      "required": ["recipient", "prompt"],
      "additionalProperties": false
    }
    """);

  // wake false can never block the caller, so the ask is claimed as background work before any
  // scheduling decision strips the generic background option.
  internal override bool ForcesBackground(JsonElement arguments) =>
      arguments.TryGetProperty("wake", out JsonElement wake) && wake.ValueKind == JsonValueKind.False;

  public override async Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context, CancellationToken cancellation) {
    string recipient = arguments.GetProperty("recipient").GetString()
        ?? throw new ArgumentException("Recipient is required.");
    string prompt = arguments.GetProperty("prompt").GetString()
        ?? throw new ArgumentException("Prompt is required.");
    bool wake = ReadWake(arguments);
    AgentQuestionResult result = await context.Sessions.AskAgentAsync(context, recipient, prompt, wake, cancellation);
    return result.Error is { } error
        ? new ToolOutput(error, [], IsError: true)
        : new ToolOutput(result.Answer ?? "The question was answered without an answer.", []);
  }

  private static bool ReadWake(JsonElement arguments) {
    if (!arguments.TryGetProperty("wake", out JsonElement wake))
      return true;
    return wake.ValueKind switch {
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => throw new ArgumentException("The wake option must be a boolean.")
    };
  }
}
