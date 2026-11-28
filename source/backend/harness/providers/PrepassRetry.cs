namespace Urd.Core.Providers;

internal static class PrepassRetry {
  internal const string PropertyName = "urd.prepass_retry";
  private static readonly TimeSpan maximumDelay = TimeSpan.FromSeconds(2);

  internal static bool CanRetry(ProviderOperationException failure) =>
      failure.IsPreStreamServiceUnavailable && failure.RetryAfter.GetValueOrDefault() <= maximumDelay;

  internal static Task WaitAsync(TimeSpan? retryAfter, TimeProvider clock, CancellationToken cancellation) {
    TimeSpan delay = TimeSpan.FromMilliseconds(Random.Shared.Next(500, 1000));
    return Task.Delay(retryAfter > delay ? retryAfter.Value : delay, clock, cancellation);
  }
}
