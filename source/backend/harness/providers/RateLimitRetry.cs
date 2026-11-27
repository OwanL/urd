namespace Urd.Core.Providers;

// One rejected exchange's retry window. Elapsed time uses the monotonic clock, not the UI timestamp.
internal sealed class RateLimitRetry(TimeProvider clock) {
  internal const string StatusProperty = "urd.rate_limit_retry_status";
  private static readonly TimeSpan retryWindow = TimeSpan.FromMinutes(5);
  private readonly long startedAt = clock.GetTimestamp();
  private int backoffSeconds = 1;

  private TimeSpan Remaining => retryWindow - clock.GetElapsedTime(startedAt);

  internal void CheckDeadline() {
    if (Remaining <= TimeSpan.Zero)
      throw WindowEnded();
  }

  internal async Task WaitAsync(TimeSpan? retryAfter, Func<DateTimeOffset?, Task>? publish,
      CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    TimeSpan delay = TimeSpan.FromSeconds(backoffSeconds * (0.5 + Random.Shared.NextDouble() * 0.5));
    backoffSeconds = Math.Min(backoffSeconds * 2, 15);
    if (retryAfter > delay)
      delay = retryAfter.Value;
    if (delay >= Remaining)
      throw WindowEnded();

    long waitingSince = clock.GetTimestamp();
    try {
      if (publish is not null)
        await publish(clock.GetUtcNow() + delay);
      TimeSpan waitingLeft = delay - clock.GetElapsedTime(waitingSince);
      if (waitingLeft > TimeSpan.Zero)
        await Task.Delay(waitingLeft, clock, cancellation);
    } finally {
      if (publish is not null)
        await publish(null);
    }
    cancellation.ThrowIfCancellationRequested();
    CheckDeadline();
  }

  private static ProviderOperationException WindowEnded() => new(ProviderFailure.RateLimited,
      "Urd stopped retrying because another attempt would exceed the five-minute retry window.");
}
