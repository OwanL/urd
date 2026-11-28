namespace Urd.Core.Providers;

internal static class ServiceUnavailableRetry {
  private static readonly TimeSpan maximumDelay = TimeSpan.FromSeconds(2);

  internal static bool CanRetry(ProviderOperationException failure) =>
      failure.IsPreStreamServiceUnavailable && failure.RetryAfter.GetValueOrDefault() <= maximumDelay;

  internal static Task WaitAsync(TimeSpan? retryAfter, TimeProvider clock, CancellationToken cancellation) {
    TimeSpan delay = TimeSpan.FromMilliseconds(Random.Shared.Next(500, 1000));
    return Task.Delay(retryAfter > delay ? retryAfter.Value : delay, clock, cancellation);
  }

  internal static async Task<T> RunOnceAsync<T>(Func<Task<T>> attempt, TimeProvider clock, CancellationToken cancellation) {
    try {
      return await attempt();
    } catch (ProviderOperationException failure) when (CanRetry(failure)) {
      await WaitAsync(failure.RetryAfter, clock, cancellation);
    }
    cancellation.ThrowIfCancellationRequested();
    return await attempt();
  }
}
