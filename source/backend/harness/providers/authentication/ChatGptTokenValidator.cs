using System.Security.Claims;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Results;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Urd.Core.Providers;

// OidcClient permits absent identity tokens and does not supply our route's nonce binding.
internal sealed class ChatGptTokenValidator(string? nonce, bool requireNonce, string? subject = null) : IIdentityTokenValidator {
  public async Task<IdentityTokenValidationResult> ValidateAsync(
      string identityToken, OidcClientOptions options, CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    try {
      var keys = new JsonWebKeySet(options.ProviderInformation.KeySet.RawData).GetSigningKeys();
      var validation = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(identityToken, new TokenValidationParameters {
        ValidateIssuer = true,
        ValidIssuer = options.Authority,
        ValidateAudience = true,
        ValidAudience = options.ClientId,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKeys = keys,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        RequireExpirationTime = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
      }).ConfigureAwait(false);
      if (!validation.IsValid)
        return Rejected();

      var identity = validation.ClaimsIdentity;
      string? tokenSubject = identity.FindFirst("sub")?.Value;
      string? tokenNonce = identity.FindFirst("nonce")?.Value;
      string? authorizedParty = identity.FindFirst("azp")?.Value;
      var jwt = (JsonWebToken)validation.SecurityToken;
      if (string.IsNullOrWhiteSpace(tokenSubject) || (subject is not null && tokenSubject != subject) ||
          (requireNonce && tokenNonce is null) ||
          (tokenNonce is not null && (nonce is null || !string.Equals(tokenNonce, nonce, StringComparison.Ordinal))) ||
          (authorizedParty is not null && authorizedParty != options.ClientId) ||
          (jwt.Audiences.Skip(1).Any() && authorizedParty != options.ClientId))
        return Rejected();

      return new() { User = new ClaimsPrincipal(identity), SignatureAlgorithm = jwt.Alg };
    } catch (Exception exception) when (exception is ArgumentException or SecurityTokenException or System.Text.Json.JsonException) {
      // Library exceptions may contain the raw token; only a fixed error crosses this boundary.
      return Rejected();
    }
  }

  private static IdentityTokenValidationResult Rejected() => new() { Error = "Identity token rejected." };
}
