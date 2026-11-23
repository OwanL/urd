using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Urd.Core.Tools;

namespace Urd.Core.Harness.Mcp;

internal sealed class RemoteMcpAgentTool(IRemoteMcpTools remote, SavedMcpTool saved) : AgentTool {
  public override string Name { get; } = ModelName(saved);
  public override ToolRelevance RelevantWhen => ToolRelevance.Always;
  public override string Description { get; } = $"MCP server {Limit(saved.ServerName, 120)}: {Limit(saved.Tool.Description ?? saved.Tool.Title ?? saved.Tool.Name, 880)}";
  public override JsonElement ParametersSchema => saved.Tool.InputSchema;
  internal override bool SupportsBackgroundOption => false;

  internal static bool HasSupportedSchema(JsonElement schema) => schema.ValueKind == JsonValueKind.Object &&
      schema.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object" &&
      (!schema.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind == JsonValueKind.Object);

  public override async Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    RemoteMcpToolResult result = await remote.CallAsync(saved.ServerId, saved.Tool.Name, arguments, cancellation);
    return new(result.Text, [], IsError: result.IsError);
  }

  private static string ModelName(SavedMcpTool saved) {
    string slug = new(saved.Tool.Name.Take(28).Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray());
    string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{saved.ServerId:N}:{saved.Tool.Name}")))[..24];
    return $"mcp_{slug}_{hash}";
  }

  private static string Limit(string value, int max) => value[..Math.Min(value.Length, max)];
}
