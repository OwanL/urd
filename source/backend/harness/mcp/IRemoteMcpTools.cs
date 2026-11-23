using System.Text.Json;

namespace Urd.Core.Harness.Mcp;

public interface IRemoteMcpTools {
  Task<IReadOnlyList<SavedMcpTool>> DiscoverAsync(CancellationToken cancellation);
  Task<RemoteMcpToolResult> CallAsync(Guid serverId, string name, JsonElement arguments, CancellationToken cancellation);
}

public sealed record SavedMcpTool(Guid ServerId, string ServerName, RemoteMcpToolDefinition Tool);
