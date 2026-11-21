using System.Text.Json;

namespace Urd.Core.Harness.Mcp;

public sealed record RemoteMcpToolDefinition(string Name, string? Title, string? Description, JsonElement InputSchema);
public sealed record RemoteMcpToolResult(string Text, bool IsError = false);
