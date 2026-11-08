using System.Text.Json;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Microsoft.IdentityModel.Tokens;

namespace Urd.Core.Providers;

// Production routes are fixed; only offline tests select another issuer/borrowed transport.
internal sealed class ChatGptAuthentication(HttpMessageHandler handler, Uri? issuer = null) {
  internal const string DynamicClientId = "dynamic_agent_client";
  internal const string Resource = "https://api.openai.com/v1";
  private const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
  private readonly Uri authority = issuer ?? new("https://auth.openai.com");

  internal Task<ChatGptSignIn> BeginAsync(
      string hostId, Uri callbackUri, ProviderRegistration? registration, CancellationToken cancellationToken) => SafelyAsync(async () => {
    if (callbackUri.Scheme != "http" || callbackUri.Host != "127.0.0.1" ||
        callbackUri.AbsolutePath != "/auth/callback" || callbackUri.Query.Length != 0 ||
        callbackUri.Fragment.Length != 0 || callbackUri.UserInfo.Length != 0)
      throw new ProviderOperationException(ProviderFailure.SignInRejected);

    HttpClient http = CreateHttpClient();
    try {
      using var discovery = await DiscoverAsync(http, cancellationToken).ConfigureAwait(false);
      string nonce = Duende.IdentityModel.CryptoRandom.CreateUniqueId(32);
      var options = CreateOptions(http, discovery, registration?.ClientId ?? DynamicClientId);
      options.RedirectUri = callbackUri.AbsoluteUri;
      options.IdentityTokenValidator = new ChatGptTokenValidator(nonce, requireNonce: true, registration?.Subject);
      var client = new OidcClient(options);
      var parameters = new Parameters { { "nonce", nonce }, { "ext_agent_host_id", hostId } };
      if (registration is null)
        parameters.Add("agent_name_hint", "Urd");
      var flow = await client.PrepareLoginAsync(parameters, cancellationToken).ConfigureAwait(false);
      if (flow.IsError)
        throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
      return new ChatGptSignIn(http, client, options, flow, nonce, registration);
    } catch {
      http.Dispose();
      throw;
    }
  }, cancellationToken);

  internal static Task<ProviderAuthentication> CompleteAsync(
      ChatGptSignIn attempt, string callbackUrl, CancellationToken cancellationToken) =>
      SafelyAsync(() => attempt.CompleteAsync(callbackUrl, cancellationToken), cancellationToken);

  internal Task<ProviderCredentials> RefreshAsync(
      ProviderRegistration registration, ProviderCredentials credentials, CancellationToken cancellationToken) => SafelyAsync(async () => {
    if (credentials.RequiresSignIn || string.IsNullOrWhiteSpace(credentials.RefreshToken))
      return credentials with { RequiresSignIn = true };

    using HttpClient http = CreateHttpClient();
    using var discovery = await DiscoverAsync(http, cancellationToken).ConfigureAwait(false);
    var options = CreateOptions(http, discovery, registration.ClientId);
    using var response = await SendRefreshAsync(http, new RefreshTokenRequest {
      Address = options.ProviderInformation.TokenEndpoint,
      ClientId = registration.ClientId,
      ClientCredentialStyle = ClientCredentialStyle.PostBody,
      RefreshToken = credentials.RefreshToken,
      Resource = [Resource]
      // Omit scope: refresh the known grant, not the requested initial scopes.
    }, cancellationToken).ConfigureAwait(false);
    if (response.IsError) {
      if (response.ErrorType == ResponseErrorType.Exception)
        throw new ProviderOperationException(ProviderFailure.RefreshUncertain);
      if (response.ErrorType == ResponseErrorType.Protocol && response.Error == "invalid_grant")
        return credentials with { RequiresSignIn = true };
      throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
    }
    if (string.IsNullOrWhiteSpace(response.AccessToken) || response.ExpiresIn <= 0 ||
        !string.Equals(response.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
      throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);

    if (response.IdentityToken is not null) {
      var validator = new ChatGptTokenValidator(credentials.Nonce, requireNonce: false, registration.Subject);
      var validation = await validator.ValidateAsync(response.IdentityToken, options, cancellationToken).ConfigureAwait(false);
      if (validation.IsError)
        throw new ProviderOperationException(ProviderFailure.SignInRejected);
    }
    return credentials with {
      AccessToken = response.AccessToken,
      RefreshToken = string.IsNullOrEmpty(response.RefreshToken) ? credentials.RefreshToken : response.RefreshToken,
      IdToken = response.IdentityToken ?? credentials.IdToken,
      ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn),
      Scopes = response.Scope is null ? credentials.Scopes : ReadScopes(response.Scope)
    };
  }, cancellationToken);

  internal async Task<bool> RevokeAsync(
      ProviderRegistration registration, ProviderCredentials credentials, CancellationToken cancellationToken) {
    if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
      return false;
    try {
      return await SafelyAsync(async () => {
        using HttpClient http = CreateHttpClient();
        using var discovery = await DiscoverAsync(http, cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(discovery.RevocationEndpoint, UriKind.Absolute, out Uri? endpoint) ||
            endpoint.GetLeftPart(UriPartial.Authority) != authority.GetLeftPart(UriPartial.Authority))
          return false;
        using var response = await http.RevokeTokenAsync(new TokenRevocationRequest {
          Address = endpoint.AbsoluteUri,
          ClientId = registration.ClientId,
          ClientCredentialStyle = ClientCredentialStyle.PostBody,
          Token = credentials.RefreshToken,
          TokenTypeHint = "refresh_token"
        }, cancellationToken).ConfigureAwait(false);
        return !response.IsError;
      }, cancellationToken).ConfigureAwait(false);
    } catch (ProviderOperationException) {
      return false;
    }
  }

  private static async Task<TokenResponse> SendRefreshAsync(
      HttpClient http, RefreshTokenRequest request, CancellationToken cancellationToken) {
    try {
      return await http.RequestRefreshTokenAsync(request, cancellationToken).ConfigureAwait(false);
    } catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException or IOException) {
      // The token endpoint may have rotated its credential before the acknowledgement was lost.
      throw new ProviderOperationException(ProviderFailure.RefreshUncertain);
    }
  }

  internal static string[] ReadScopes(string? scope) =>
      (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();

  private HttpClient CreateHttpClient() => new(handler, disposeHandler: false) {
    Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1024 * 1024
  };

  private async Task<DiscoveryDocumentResponse> DiscoverAsync(HttpClient http, CancellationToken cancellationToken) {
    var discovery = await http.GetDiscoveryDocumentAsync(new DiscoveryDocumentRequest {
      Address = authority.AbsoluteUri
    }, cancellationToken).ConfigureAwait(false);
    if (discovery.IsError || discovery.KeySet is null || discovery.Issuer != authority.AbsoluteUri.TrimEnd('/')) {
      discovery.Dispose();
      throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
    }
    return discovery;
  }

  private OidcClientOptions CreateOptions(HttpClient http, DiscoveryDocumentResponse discovery, string clientId) => new() {
    Authority = authority.AbsoluteUri.TrimEnd('/'),
    ClientId = clientId,
    Scope = Scopes,
    Resource = [Resource],
    ProviderInformation = new() {
      IssuerName = discovery.Issuer,
      KeySet = discovery.KeySet,
      AuthorizeEndpoint = new Uri(authority, "/api/accounts/authorize").AbsoluteUri,
      TokenEndpoint = new Uri(authority, "/api/accounts/oauth/token").AbsoluteUri,
      TokenEndPointAuthenticationMethods = ["none"]
    },
    LoadProfile = false,
    FilterClaims = false,
    DisablePushedAuthorization = true,
    RefreshDiscoveryDocumentForLogin = false,
    RefreshDiscoveryOnSignatureFailure = false,
    TokenClientCredentialStyle = ClientCredentialStyle.PostBody,
    HttpClientFactory = _ => http,
    ClockSkew = TimeSpan.Zero,
    Policy = new() { RequireIdentityTokenSignature = true }
  };

  private static async Task<T> SafelyAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken) {
    try {
      cancellationToken.ThrowIfCancellationRequested();
      return await operation().ConfigureAwait(false);
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
    } catch (Exception exception) when (exception is HttpRequestException or JsonException or
        ArgumentException or InvalidOperationException or SecurityTokenException) {
      throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
    }
  }
}
