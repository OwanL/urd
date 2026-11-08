namespace Urd.Core.Providers;

// Tracks one owned provider request through cancellation and cleanup, without durable execution state.
internal sealed class ProviderRequest(Guid connectionId, CancellationToken cancellationToken) {
  internal Guid ConnectionId { get; } = connectionId;
  internal ProviderCancellation Cancellation { get; } = new(TimeSpan.FromMinutes(2), cancellationToken);
  internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

}
