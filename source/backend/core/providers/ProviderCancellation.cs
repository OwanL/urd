namespace Urd.Core.Providers;

// Routes all cancellation origins through one retained callback-drain task.
internal sealed class ProviderCancellation : IDisposable {
  private readonly CancellationTokenSource source = new();
  private readonly object sync = new();
  private readonly CancellationTokenRegistration caller;
  private readonly Timer deadline;
  private Task? cancellation;

  internal ProviderCancellation(TimeSpan timeout, CancellationToken callerToken = default) {
    caller = callerToken.UnsafeRegister(static state => ((ProviderCancellation)state!).CancelAsync(), this);
    deadline = new Timer(static state => ((ProviderCancellation)state!).CancelAsync(), this,
        timeout, Timeout.InfiniteTimeSpan);
  }

  internal CancellationToken Token => source.Token;
  internal bool IsCancellationRequested => source.IsCancellationRequested;

  internal Task CancelAsync() {
    lock (sync)
      return cancellation ??= source.CancelAsync();
  }

  // The operation owner awaits CancelAsync before disposing or relinquishing ownership.
  public void Dispose() {
    deadline.Dispose();
    caller.Dispose();
    source.Dispose();
  }
}
