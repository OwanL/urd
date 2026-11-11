namespace Urd.Core.Providers;

// Manager-owned lifetime of one sign-in, including its callback and final cleanup.
internal sealed class ProviderSignInAttempt(ProviderState state) {
  internal Guid Id { get; } = Guid.NewGuid();
  internal ProviderState InitialState { get; } = state;
  internal ProviderCancellation Cancellation { get; } = new(TimeSpan.FromMinutes(10));
  internal TaskCompletionSource<ProviderSignIn> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
  internal TaskCompletionSource<string> Callback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
  internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
  internal DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(10);
  internal ChatGptSignIn? Flow { get; set; }
  internal ProviderSignInStatus Status { get; set; } = ProviderSignInStatus.Pending;
  internal string? Error { get; set; }
  internal bool Cancelled { get; set; }

  internal ProviderSignIn Snapshot() => new(Id, InitialState.Connection.Id, Status,
      Status == ProviderSignInStatus.Pending ? Flow?.AuthorizationUrl : null, ExpiresAt, Error);
}
