using System.Text.Json;

namespace Urd.Core.Tools;

internal sealed class CancelPendingCallsTool(PendingToolCalls pending) : AgentTool {
  public override string Name => "cancel_pending_calls";
  public override string Description => """
    Request cancellation of all pending tool calls except this call.
    This acknowledges requests, not completed cleanup or rollback; calls may already have had partial effects.
    """;
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {},
      "additionalProperties": false
    }
    """);

  public override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    int count = pending.RequestCancelAll(context.RequestId, context.CallId);
    return Task.FromResult(new ToolOutput($"Cancellation requested for {count} pending tool call(s). This is not cleanup completion or rollback.", [],
      JsonSerializer.SerializeToElement(new { cancellationRequested = count })));
  }
}
