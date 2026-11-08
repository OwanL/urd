using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;

namespace Urd.Core.Providers;

// The manager joins callback work before disposing the attempt and its backchannel.
internal sealed class ChatGptSignIn(
    HttpClient http, OidcClient client, OidcClientOptions options, AuthorizeState flow,
    string nonce, ProviderRegistration? registration) : IDisposable {
  private int consumed;

  internal Uri AuthorizationUrl { get; } = new(flow.StartUrl);
  internal string State { get; } = flow.State;
  internal DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow.AddMinutes(10);

  internal async Task<ProviderAuthentication> CompleteAsync(string callbackUrl, CancellationToken cancellationToken) {
    if (Interlocked.Exchange(ref consumed, 1) != 0 || DateTimeOffset.UtcNow >= ExpiresAt)
      throw new ProviderOperationException(ProviderFailure.SignInRejected);

    if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out Uri? callback) ||
        callback.GetLeftPart(UriPartial.Path) != options.RedirectUri || callback.Fragment.Length != 0)
      throw new ProviderOperationException(ProviderFailure.SignInRejected);

    // Check state before interpreting errors or the dynamically issued client ID.
    var response = new AuthorizeResponse(callbackUrl);
    if (!string.Equals(response.State, flow.State, StringComparison.Ordinal) || response.IsError)
      throw new ProviderOperationException(ProviderFailure.SignInRejected);

    string? issuedClient = response.TryGet("client_id");
    if (registration is null) {
      if (string.IsNullOrWhiteSpace(issuedClient) || issuedClient == ChatGptAuthentication.DynamicClientId)
        throw new ProviderOperationException(ProviderFailure.SignInRejected);
      options.ClientId = issuedClient;
    } else if (issuedClient is not null && issuedClient != registration.ClientId) {
      throw new ProviderOperationException(ProviderFailure.SignInRejected);
    }

    var result = await client.ProcessResponseAsync(callbackUrl, flow,
        new() { { "resource", ChatGptAuthentication.Resource } }, cancellationToken).ConfigureAwait(false);
    if (result.IsError || string.IsNullOrWhiteSpace(result.IdentityToken) ||
        string.IsNullOrWhiteSpace(result.AccessToken) || string.IsNullOrWhiteSpace(result.RefreshToken) ||
        result.TokenResponse.ExpiresIn <= 0 ||
        !string.Equals(result.TokenResponse.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
      throw new ProviderOperationException(ProviderFailure.SignInRejected);

    string? subject = result.User.FindFirst("sub")?.Value;
    if (string.IsNullOrWhiteSpace(subject) || (registration is not null && subject != registration.Subject))
      throw new ProviderOperationException(ProviderFailure.SignInRejected);

    return new(new(options.ClientId, subject, result.User.FindFirst("email")?.Value),
        new(null, result.AccessToken, result.RefreshToken, result.IdentityToken,
            result.AccessTokenExpiration, ChatGptAuthentication.ReadScopes(result.TokenResponse.Scope), Nonce: nonce));
  }

  public void Dispose() {
    Interlocked.Exchange(ref consumed, 1);
    http.Dispose();
  }
}
