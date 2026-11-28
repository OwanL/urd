namespace Urd.Core.Providers;

// Manager-owned lifetime of one sign-in, including its callback and final cleanup.
internal sealed class ProviderSignInAttempt(ProviderState state) {
  internal Guid Id { get; } = Guid.NewGuid();
  internal Guid ConnectionId { get; } = state.Connection.Id;
  internal long Generation { get; } = state.Generation;
  internal ProviderRegistration? Registration { get; } = state.Registration;
  internal bool IsCopilot { get; } = state.Connection.Route == ProviderRoute.GitHubCopilot;
  internal ProviderCancellation Cancellation { get; } = new(state.Connection.Route == ProviderRoute.GitHubCopilot
      ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(10));
  internal TaskCompletionSource<ProviderSignIn> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
  internal TaskCompletionSource<string> Callback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
  internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
  internal DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(10);
  internal ChatGptSignIn? Flow { get; set; }
  internal Uri? VerificationUrl { get; set; }
  internal string? UserCode { get; set; }
  internal ProviderSignInStatus Status { get; set; } = ProviderSignInStatus.Pending;
  internal string? Error { get; set; }
  internal bool Cancelled { get; set; }
  internal bool CredentialsSaved { get; set; }
  internal bool CatalogRefreshFailed { get; set; }

  internal ProviderSignIn Snapshot() => new(Id, ConnectionId, Status,
      Status == ProviderSignInStatus.Pending ? VerificationUrl ?? Flow?.AuthorizationUrl : null, ExpiresAt, Error,
      Status == ProviderSignInStatus.Pending ? UserCode : null, CatalogRefreshFailed);
}
