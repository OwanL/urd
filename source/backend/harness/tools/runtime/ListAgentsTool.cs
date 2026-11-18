using System.Text.Json;
using Urd.Core.Sessions;

namespace Urd.Core.Tools;

internal sealed class ListAgentsTool : HarnessTool {
  public override string Name => "list_agents";
  public override ToolRelevance RelevantWhen => ToolRelevance.Always;
  public override string Description => "List live agents, their states and parent titles. Placeholder titles are not addressable.";
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {},
      "additionalProperties": false
    }
    """);

  public override async Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context, CancellationToken cancellation) {
    IReadOnlyList<LiveAgent> agents = await context.Sessions.ListLiveAgentsAsync(cancellation);
    string text = agents.Count == 0 ? "No live agents." : string.Join("\n", agents.Select(agent =>
        $"title: {FormatTitle(agent.Title)} | state: {agent.State}"
            + (agent.ParentTitle is { } parent ? $" | parent: {FormatTitle(parent)}" : "")));
    return new ToolOutput(text, []);
  }

  private static string FormatTitle(AgentTitle title) =>
      title.IsPlaceholder ? $"{title.Text} [not addressable]" : title.Text;
}
