namespace Urd.Core.Providers;

// An ephemeral sign-in attempt; no credential tokens appear in this public view.
public sealed record ProviderSignIn(
    Guid Id, Guid ConnectionId, ProviderSignInStatus Status, Uri? AuthorizationUrl,
    DateTimeOffset ExpiresAt, string? Error);

public enum ProviderSignInStatus { Pending, Completing, Completed, Cancelled, Expired, Failed }
