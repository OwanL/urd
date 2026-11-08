namespace Urd.Core.Providers;

// Backend-private tokens; never return or log this value.
internal sealed record ProviderCredentials(
    string? ApiKey, string? AccessToken, string? RefreshToken, string? IdToken,
    DateTimeOffset? ExpiresAt, string[] Scopes, bool RequiresSignIn = false, string? Nonce = null) {
  public override string ToString() => "[provider credentials]";
}

// Validated account registration retained independently of removable tokens.
internal sealed record ProviderRegistration(string ClientId, string Subject, string? Email);

// A validated sign-in result awaiting a guarded persistence decision.
internal sealed record ProviderAuthentication(
    ProviderRegistration Registration, ProviderCredentials Credentials) {
  public override string ToString() => "[provider authentication]";
}

// Credential presence and account metadata, not proof of successful inference.
public sealed record ProviderCredentialStatus(
    Guid ConnectionId, bool HasCredentials, bool RequiresSignIn, string? AccountLabel,
    DateTimeOffset? ExpiresAt, bool? PlanAccessGranted, bool IsDisconnecting);

// Local deletion and remote revocation are distinct outcomes.
public sealed record ProviderDisconnectResult(
    Guid ConnectionId, bool LocalCredentialsCleared, bool? RemoteRevocationConfirmed);
