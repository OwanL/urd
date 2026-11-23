using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.Authentication;
using Urd.Core.Harness.Mcp;
using Urd.Core.Storage;

namespace Urd.Server;

internal sealed partial class McpConnections : IRemoteMcpTools {
  private readonly UrdStore store;
  private readonly Func<Uri> callbackUri;
  private readonly Lazy<McpSecretProtection> secretProtection;
  private McpSecretProtection Secrets => secretProtection.Value;
  private readonly object gate = new();
  private readonly Dictionary<Guid, Operation> operations = [];
  private const int MaxOperations = 16;
  private bool stopping;
  private readonly HashSet<Operation> running = [];

  internal McpConnections(UrdStore store, Func<Uri> callbackUri, string dataRoot) {
    this.store = store;
    this.callbackUri = callbackUri;
    secretProtection = new(() => new McpSecretProtection(dataRoot));
  }

  internal object List() => store.McpServers.List().Select(server => new {
    server.Id, server.Name, server.Url, server.Authentication, server.Tools
  }).ToArray();

  internal Guid Start(McpConnectRequest request) {
    if (!RemoteMcpProbe.TryParseEndpoint(request.Url, out Uri? endpoint))
      throw new ArgumentException("Use HTTPS, or HTTP on loopback, without credentials, a query or a fragment.");
    var auth = ParseAuthentication(request.Authentication);
    if (auth != RemoteMcpAuthentication.Automatic && (string.IsNullOrWhiteSpace(request.Credential) ||
        request.Credential.Length > 8192 || request.Credential.Any(char.IsControl)))
      throw new ArgumentException("Invalid credential.");
    if (auth == RemoteMcpAuthentication.CustomHeader && string.IsNullOrWhiteSpace(request.HeaderName))
      throw new ArgumentException("Header required.");
    return Begin(new(Guid.NewGuid(), endpoint.Host, endpoint.AbsoluteUri, request.Authentication, [],
      auth == RemoteMcpAuthentication.CustomHeader ? request.HeaderName : null,
      auth == RemoteMcpAuthentication.Automatic ? null : Secrets.Protect(request.Credential!), null), false);
  }

  internal Guid Reconnect(Guid id) {
    var server = store.McpServers.Find(id) ?? throw new KeyNotFoundException();
    return Begin(server, true);
  }

  private Guid Begin(SavedMcpServer server, bool existing) {
    lock (gate) {
      foreach (var id in operations.Where(pair => pair.Value.FinishedAt < DateTimeOffset.UtcNow.AddMinutes(-10)).Select(pair => pair.Key).ToArray())
        operations.Remove(id);
      if (operations.Count >= MaxOperations) {
        var oldest = operations.Values.Where(op => op.FinishedAt is not null).OrderBy(op => op.FinishedAt).FirstOrDefault();
        if (oldest is not null) operations.Remove(oldest.Id);
      }
      if (stopping || running.Count >= MaxOperations || operations.Count >= MaxOperations || running.Any(op => op.ServerId == server.Id))
        throw new InvalidOperationException("Too many connections in progress.");
      var operation = new Operation(server.Id);
      operations.Add(operation.Id, operation);
      running.Add(operation);
      operation.Work = Task.Run(() => RunAsync(operation, server, existing));
      return operation.Id;
    }
  }

  internal object? Get(Guid id) {
    lock (gate) {
      if (!operations.TryGetValue(id, out var op)) return null;
      return new { op.State, op.AuthorizationUrl, op.Message };
    }
  }

  internal bool Cancel(Guid id) {
    lock (gate) {
      if (!operations.Remove(id, out var op)) return false;
      if (running.Contains(op)) _ = op.Stop.CancelAsync();
      return true;
    }
  }

  internal bool Remove(Guid id) {
    lock (gate) {
      foreach (var op in running.Where(op => op.ServerId == id))
        _ = op.Stop.CancelAsync();
      return store.McpServers.Remove(id);
    }
  }

  internal Task ShutdownAsync() {
    lock (gate) {
      stopping = true;
      foreach (Operation op in running)
        _ = op.Stop.CancelAsync();
      return Task.WhenAll(running.Select(op => op.Work!));
    }
  }

  internal bool CompleteCallback(string state, string code, string? iss) {
    lock (gate) {
      var op = operations.Values.FirstOrDefault(item => item.State == "signIn" && item.ExpectedState == state);
      if (op?.Callback is null || op.Stop.IsCancellationRequested) return false;
      op.ExpectedState = null;
      return op.Callback.TrySetResult(new AuthorizationResult { Code = code, State = state, Iss = iss });
    }
  }

  private async Task RunAsync(Operation op, SavedMcpServer server, bool existing) {
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(op.Stop.Token);
    deadline.CancelAfter(TimeSpan.FromMinutes(5));
    var mutex = ServerMutex(server.Id);
    bool entered = false;
    try {
      await mutex.WaitAsync(deadline.Token);
      entered = true;
      if (existing)
        server = store.McpServers.Find(server.Id) ?? throw new KeyNotFoundException();
      var saved = server.OAuthTokens is null ? null : JsonSerializer.Deserialize<TokenContainer>(Secrets.Unprotect(server.OAuthTokens));
      var cache = new TokenCache(saved, existing ? PersistRefreshedTokens : null);
      void PersistRefreshedTokens(TokenContainer refreshed) {
        string protectedTokens = Secrets.Protect(JsonSerializer.Serialize(refreshed));
        lock (gate) {
          // Updating only this field prevents a refresh from restoring a removed server.
          store.McpServers.SaveOAuthTokens(server.Id, protectedTokens);
        }
      }
      var result = await RemoteMcpProbe.ProbeAsync(CreateOptions(server, new ClientOAuthOptions {
          RedirectUri = OAuthRedirect(), TokenCache = cache,
          AuthorizationCallbackHandler = async (context, ct) => {
            var query = QueryHelpers.ParseQuery(context.AuthorizationUri.Query);
            if (!query.TryGetValue("state", out var state) || state.Count != 1 || string.IsNullOrEmpty(state[0]))
              throw new InvalidOperationException("Missing OAuth state.");
            var completion = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) {
              ct.ThrowIfCancellationRequested();
              op.ExpectedState = state[0]; op.Callback = completion;
              op.AuthorizationUrl = context.AuthorizationUri.AbsoluteUri; op.State = "signIn";
            }
            var response = await completion.Task.WaitAsync(ct);
            lock (gate) {
            op.State = "connecting";
            op.AuthorizationUrl = null;
            op.Callback = null;
          }
            return response;
          }
      }), deadline.Token);
      string? tokens = null;
      var token = await cache.GetTokensAsync(deadline.Token);
      if (token is not null) tokens = Secrets.Protect(JsonSerializer.Serialize(token));
      lock (gate) {
        deadline.Token.ThrowIfCancellationRequested();
        if (!result.Success) { Fail(op); return; }
        var updated = server with { Name = string.IsNullOrWhiteSpace(result.DisplayName) ? server.Name : result.DisplayName,
          Tools = result.Tools.Select(tool => tool.Name).ToArray(), OAuthTokens = tokens };
        if (!store.McpServers.Save(updated, existing)) { Fail(op); return; }
        op.State = "connected"; op.AuthorizationUrl = null; op.FinishedAt = DateTimeOffset.UtcNow;
      }
    } catch (Exception) {
      lock (gate) { Fail(op); }
    } finally {
      if (entered)
        mutex.Release();
      lock (gate) {
        running.Remove(op);
        // The linked deadline unregisters at method exit; leave the operation source for GC.
      }
    }
  }

  private static void Fail(Operation op) {
    op.State = "failed"; op.AuthorizationUrl = null; op.ExpectedState = null; op.Callback = null;
    op.Message = "Unable to connect to the MCP server."; op.FinishedAt = DateTimeOffset.UtcNow;
  }

  private Uri OAuthRedirect() {
    var redirect = callbackUri();
    if (!redirect.IsAbsoluteUri || redirect.Scheme != Uri.UriSchemeHttp || redirect.Host != "127.0.0.1" ||
        redirect.AbsolutePath != "/mcp/auth/callback" || !string.IsNullOrEmpty(redirect.Query) ||
        !string.IsNullOrEmpty(redirect.Fragment) || !string.IsNullOrEmpty(redirect.UserInfo))
      throw new InvalidOperationException("Invalid callback URI.");
    return redirect;
  }

  private RemoteMcpProbeOptions CreateOptions(SavedMcpServer server, ClientOAuthOptions oauth) => new() {
    Endpoint = new Uri(server.Url), Authentication = ParseAuthentication(server.Authentication),
    Secret = server.Credential is null ? null : Secrets.Unprotect(server.Credential), HeaderName = server.HeaderName,
    OAuth = oauth
  };

  private static RemoteMcpAuthentication ParseAuthentication(string value) => value switch {
    "automatic" => RemoteMcpAuthentication.Automatic,
    "bearerToken" => RemoteMcpAuthentication.Bearer,
    "customHeader" => RemoteMcpAuthentication.CustomHeader,
    _ => throw new ArgumentException("Invalid authentication.")
  };

  private sealed class TokenCache(TokenContainer? initial, Action<TokenContainer>? store) : ITokenCache {
    private TokenContainer? cached = initial;
    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken) {
      store?.Invoke(tokens);
      Volatile.Write(ref cached, tokens);
      return ValueTask.CompletedTask;
    }
    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) =>
      ValueTask.FromResult(Volatile.Read(ref cached));
  }

  private sealed class Operation(Guid serverId) {
    internal Guid Id { get; } = Guid.NewGuid();
    internal Guid ServerId { get; } = serverId;
    internal CancellationTokenSource Stop { get; } = new();
    internal Task? Work { get; set; }
    internal string State { get; set; } = "connecting";
    internal string? AuthorizationUrl { get; set; }
    internal string? Message { get; set; }
    internal string? ExpectedState { get; set; }
    internal TaskCompletionSource<AuthorizationResult>? Callback { get; set; }
    internal DateTimeOffset? FinishedAt { get; set; }
  }
}
