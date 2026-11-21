using System.Text.Json;
using Urd.Core.Harness.Mcp;

namespace Urd.Core.Tools;

// Produces a proposal for the user to review; it never connects or saves settings.
internal sealed class ProposeMcpConnectionTool : AgentTool {
  public override string Name => "propose_mcp_connection";
  public override ToolRelevance RelevantWhen => ToolRelevance.When("Use when proposing a remote MCP connection for the user to review.");
  public override string Description => "Propose a remote MCP endpoint for the user to review. Does not connect or save it. Never include credentials.";
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {
        "name": { "type": "string", "minLength": 1, "maxLength": 200 },
        "url": { "type": "string", "minLength": 1, "maxLength": 2048, "description": "HTTPS endpoint, or HTTP on loopback. No query, fragment or credentials." }
      },
      "required": ["name", "url"],
      "additionalProperties": false
    }
    """);

  public override Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    if (arguments.ValueKind != JsonValueKind.Object)
      throw new ArgumentException("Arguments must be an object.");
    string? name = null, url = null;
    foreach (JsonProperty property in arguments.EnumerateObject()) {
      if (property.Value.ValueKind != JsonValueKind.String)
        throw new ArgumentException("Proposal fields must be strings.");
      switch (property.Name) {
        case "name" when name is null: name = property.Value.GetString(); break;
        case "url" when url is null: url = property.Value.GetString(); break;
        default: throw new ArgumentException("Unknown or duplicate proposal field.");
      }
    }
    if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name != name.Trim())
      throw new ArgumentException("Name must be nonblank, trimmed and at most 200 characters.");
    if (!RemoteMcpProbe.TryParseEndpoint(url, out Uri? endpoint))
      throw new ArgumentException("URL must be HTTPS or loopback HTTP, without credentials, query or fragment.");

    JsonElement details = JsonSerializer.SerializeToElement(new { name, url = endpoint.AbsoluteUri });
    return Task.FromResult(new ToolOutput("MCP connection proposed for user review; not connected or saved.", [], details));
  }

}
