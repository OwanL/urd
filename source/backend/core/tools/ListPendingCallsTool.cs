using System.Text.Json;

namespace Urd.Core.Tools;

internal sealed class ListPendingCallsTool(PendingToolCalls pending) : AgentTool {
  private static readonly JsonElement schema = JsonSerializer.SerializeToElement(new {
    type = "object", properties = new { }, additionalProperties = false
  });

  public override string Name => "list_pending_calls";
  public override string Description => "List pending tool calls, including their identities, start times and cancellation-request status.";
  public override JsonElement ParametersSchema => schema;

  public override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    JsonElement calls = JsonSerializer.SerializeToElement(pending.List(), JsonSerializerOptions.Web);
    return Task.FromResult(new ToolOutput(calls.GetRawText(), [], calls));
  }
}
