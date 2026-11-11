namespace Urd.Core.Providers;

// Tracks one owned provider request through cancellation and cleanup, without durable execution state.
internal sealed class ProviderRequest(Guid connectionId, CancellationToken cancellationToken, TimeSpan? timeout = null) {
  internal Guid ConnectionId { get; } = connectionId;
  internal ProviderCancellation Cancellation { get; } = new(timeout ?? TimeSpan.FromMinutes(2), cancellationToken);
  internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

}
