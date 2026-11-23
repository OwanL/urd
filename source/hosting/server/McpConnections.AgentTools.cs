using System.Text.Json;
using ModelContextProtocol.Authentication;
using Urd.Core.Harness.Mcp;
using Urd.Core.Storage;

namespace Urd.Server;

internal sealed partial class McpConnections {
  private readonly SemaphoreSlim[] serverLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
  private bool lanPaused;
  private int activeAgentOperations;

  private SemaphoreSlim ServerMutex(Guid id) => serverLocks[(int)((uint)id.GetHashCode() % serverLocks.Length)];

  internal IDisposable PauseAgentToolsForLan() {
    lock (gate) {
      if (activeAgentOperations != 0)
        throw new InvalidOperationException("Wait for MCP operations to finish before enabling LAN hosting.");
      lanPaused = true;
      return new LanPause(this);
    }
  }

  public async Task<IReadOnlyList<SavedMcpTool>> DiscoverAsync(CancellationToken cancellation) {
    SavedMcpServer[] servers;
    lock (gate) {
      if (lanPaused || stopping)
        return [];
      servers = store.McpServers.List().ToArray();
    }
    if (servers.Length == 0)
      return [];
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    deadline.CancelAfter(TimeSpan.FromSeconds(5));
    var discovered = new IReadOnlyList<SavedMcpTool>?[servers.Length];
    await Parallel.ForEachAsync(Enumerable.Range(0, servers.Length), new ParallelOptions {
      MaxDegreeOfParallelism = 4, CancellationToken = cancellation
    }, async (index, _) => {
      discovered[index] = await RunAgentOperationAsync<IReadOnlyList<SavedMcpTool>>(servers[index].Id,
          async (server, options, ct) => (await RemoteMcpProbe.DiscoverToolsAsync(options, ct))
              .Select(tool => new SavedMcpTool(server.Id, server.Name, tool)).ToArray(), [], deadline.Token, cancellation);
    });
    return discovered.SelectMany(tools => tools ?? []).ToArray();
  }

  public async Task<RemoteMcpToolResult> CallAsync(Guid serverId, string name, JsonElement arguments, CancellationToken cancellation) {
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    deadline.CancelAfter(TimeSpan.FromMinutes(2));
    return await RunAgentOperationAsync(serverId, (_, options, ct) => RemoteMcpProbe.CallToolAsync(options, name, arguments, ct),
        new RemoteMcpToolResult("MCP tool unavailable. Check the server in Settings → MCP and reconnect if needed.", true),
        deadline.Token, cancellation);
  }

  private async Task<T> RunAgentOperationAsync<T>(Guid id,
      Func<SavedMcpServer, RemoteMcpProbeOptions, CancellationToken, Task<T>> run, T unavailable,
      CancellationToken deadline, CancellationToken cancellation) {
    var mutex = ServerMutex(id);
    try {
      await mutex.WaitAsync(deadline);
      try {
        SavedMcpServer server;
        lock (gate) {
          if (lanPaused || stopping || store.McpServers.Find(id) is not { } saved)
            return unavailable;
          server = saved;
          activeAgentOperations++;
        }
        try {
          return await run(server, CreateAgentOptions(server), deadline);
        } finally {
          lock (gate)
            activeAgentOperations--;
        }
      } finally {
        mutex.Release();
      }
    } catch (Exception) {
      cancellation.ThrowIfCancellationRequested();
      return unavailable;
    }
  }

  private RemoteMcpProbeOptions CreateAgentOptions(SavedMcpServer server) {
    var tokens = server.OAuthTokens is null ? null : JsonSerializer.Deserialize<TokenContainer>(Secrets.Unprotect(server.OAuthTokens));
    var cache = new TokenCache(tokens, refreshed => {
      var protectedTokens = Secrets.Protect(JsonSerializer.Serialize(refreshed));
      lock (gate)
        store.McpServers.SaveOAuthTokens(server.Id, protectedTokens);
    });
    return CreateOptions(server, new ClientOAuthOptions {
      RedirectUri = OAuthRedirect(), TokenCache = cache,
      AuthorizationCallbackHandler = static (_, _) => throw new InvalidOperationException("Reconnect the MCP server in Settings.")
    });
  }

  private sealed class LanPause(McpConnections connections) : IDisposable {
    public void Dispose() {
      lock (connections.gate)
        connections.lanPaused = false;
    }
  }
}
