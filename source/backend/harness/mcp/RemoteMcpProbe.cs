using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace Urd.Core.Harness.Mcp;

public enum RemoteMcpAuthentication { Automatic, Bearer, CustomHeader }

public sealed record RemoteMcpProbeOptions {
  public required Uri Endpoint { get; init; }
  public RemoteMcpAuthentication Authentication { get; init; }
  public string? Secret { get; init; }
  public string? HeaderName { get; init; }
  public ClientOAuthOptions? OAuth { get; init; }
}

public sealed record RemoteMcpTool(string Name, string? Title, string? Description);
public sealed record RemoteMcpProbeResult(bool Success, string? DisplayName, IReadOnlyList<RemoteMcpTool> Tools, string? Error);

public static class RemoteMcpProbe {
  public static async Task<RemoteMcpProbeResult> ProbeAsync(RemoteMcpProbeOptions options, CancellationToken cancellationToken = default) {
    try {
      if (options is null || !SafeAddress(options.Endpoint))
        return Failure();

      var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      switch (options.Authentication) {
        case RemoteMcpAuthentication.Automatic:
          break;
        case RemoteMcpAuthentication.Bearer when !string.IsNullOrWhiteSpace(options.Secret):
          headers.Add("Authorization", new AuthenticationHeaderValue("Bearer", options.Secret).ToString());
          break;
        case RemoteMcpAuthentication.CustomHeader when !string.IsNullOrWhiteSpace(options.HeaderName) && !string.IsNullOrEmpty(options.Secret):
          headers.Add(options.HeaderName, options.Secret);
          break;
        default:
          return Failure();
      }

      var oauth = options.Authentication == RemoteMcpAuthentication.Automatic ? CopyOAuth(options.Endpoint, options.OAuth) : null;
      using var http = new HttpClient(new OriginGuard(options.Endpoint, options.HeaderName) {
        InnerHandler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }
      });
      await using var transport = new HttpClientTransport(new HttpClientTransportOptions {
        Endpoint = options.Endpoint,
        AdditionalHeaders = headers,
        OAuth = oauth,
        TransportMode = HttpTransportMode.AutoDetect
      }, http);
      await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
      var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
      return new(true, client.ServerInfo?.Title ?? client.ServerInfo?.Name,
        tools.Select(tool => new RemoteMcpTool(tool.Name, tool.Title, tool.Description)).ToArray(), null);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw;
    } catch (Exception) {
      return Failure();
    }
  }

  public static bool TryParseEndpoint(string? value, [NotNullWhen(true)] out Uri? endpoint) {
    endpoint = null;
    if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value != value.Trim() ||
        value.IndexOfAny(['?', '#', '\\']) >= 0 || value.Any(char.IsControl) ||
        !Uri.TryCreate(value, UriKind.Absolute, out Uri? candidate) || !SafeAddress(candidate))
      return false;
    int separator = value.IndexOf("://", StringComparison.Ordinal);
    if (separator < 0 || value[(separator + 3)..].Split('/')[0].Contains('@'))
      return false;
    endpoint = candidate;
    return true;
  }

  private static RemoteMcpProbeResult Failure() => new(false, null, [], "Unable to connect to the MCP server.");

  private static bool SafeAddress(Uri? uri) => uri is { IsAbsoluteUri: true, Host.Length: > 0 } &&
    string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
    (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri)));

  private static bool IsLoopback(Uri uri) => uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
    (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

  private static bool SafeOAuthAddress(Uri endpoint, Uri? address) => SafeAddress(address) &&
    (IsLoopback(endpoint) || !IsLoopback(address!));

  private static ClientOAuthOptions? CopyOAuth(Uri endpoint, ClientOAuthOptions? options) {
    if (options is null)
      return null;
    if (options.AuthorizationCallbackHandler is null)
      throw new ArgumentException("OAuth requires a state-validating callback.");
    var callback = options.AuthorizationCallbackHandler;
    return new ClientOAuthOptions {
      RedirectUri = options.RedirectUri,
      ClientId = options.ClientId,
      ClientSecret = options.ClientSecret,
      ClientMetadataDocumentUri = options.ClientMetadataDocumentUri,
      Scopes = options.Scopes,
      ScopeSelector = options.ScopeSelector,
      AuthServerSelector = options.AuthServerSelector,
      DynamicClientRegistration = options.DynamicClientRegistration,
      AdditionalAuthorizationParameters = options.AdditionalAuthorizationParameters,
      TokenCache = options.TokenCache,
      AuthorizationCallbackHandler = (context, ct) => SafeOAuthAddress(endpoint, context.AuthorizationUri)
        ? callback(context, ct) : throw new InvalidOperationException("Unsafe authorization address.")
    };
  }

  private sealed class OriginGuard(Uri endpoint, string? customHeader) : DelegatingHandler {
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      var address = request.RequestUri;
      if (!SafeOAuthAddress(endpoint, address))
        throw new HttpRequestException("Unsafe destination.");
      if (!SameOrigin(endpoint, address!)) {
        if (request.Headers.Authorization?.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) == true ||
            (!string.IsNullOrWhiteSpace(customHeader) && request.Headers.Contains(customHeader)))
          throw new HttpRequestException("Cross-origin credentials are not allowed.");
        // SDK OAuth uses this same client for discovery, registration and token exchange.
        // Legacy SSE may advertise an arbitrary POST URL; JSON-RPC must never follow it.
        if (request.Content?.Headers.ContentType?.MediaType == "application/json") {
          var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
          using var json = JsonDocument.Parse(body);
          if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.TryGetProperty("jsonrpc", out _))
            throw new HttpRequestException("Cross-origin MCP request is not allowed.");
        }
      }
      return await base.SendAsync(request, cancellationToken);
    }

    private static bool SameOrigin(Uri a, Uri b) => a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase) &&
      a.IdnHost.Equals(b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
  }
}
