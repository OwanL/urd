using System.Text.Json;

namespace Urd.Core.Tools;

// One model-facing tool name and the implementation that interprets its raw arguments.
#pragma warning disable S1694 // Owner-approved tool base shape; not a public interface contract.
internal abstract class AgentTool {
  public abstract string Name { get; }
  public abstract string Description { get; }
  public abstract JsonElement ParametersSchema { get; }
  public abstract Task<ToolOutput> RunAsync(
    JsonElement arguments, ToolContext context, CancellationToken cancellation);
}
#pragma warning restore S1694
