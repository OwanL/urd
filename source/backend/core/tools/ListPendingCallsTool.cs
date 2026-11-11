using System.Text.Json;

namespace Urd.Core.Tools;

internal sealed class ListPendingCallsTool(PendingToolCalls pending) : AgentTool {
  public override string Name => "list_pending_calls";
  public override string Description => """
    List pending tool calls, including their identities, start times and cancellation-request status.
    """;
  public override JsonElement ParametersSchema => schema;

  private static readonly JsonElement schema = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {},
      "additionalProperties": false
    }
    """);

  public override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    JsonElement calls = JsonSerializer.SerializeToElement(pending.List(), JsonSerializerOptions.Web);
    return Task.FromResult(new ToolOutput(calls.GetRawText(), [], calls));
  }
}
