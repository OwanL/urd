namespace Urd.Core.Providers;

// Routes all cancellation origins through one retained callback-drain task.
internal sealed class ProviderCancellation : IDisposable {
  private readonly CancellationTokenSource source = new();
  private readonly object sync = new();
  private readonly CancellationTokenRegistration caller;
  private readonly Timer deadline;
  private long? deadlineAt;
  private Task? cancellation;

  internal ProviderCancellation(TimeSpan timeout, CancellationToken callerToken = default) {
    caller = callerToken.UnsafeRegister(static state => ((ProviderCancellation)state!).CancelAsync(), this);
    deadline = new Timer(static state => ((ProviderCancellation)state!).CancelIfDue(), this,
        Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    SetDeadline(timeout);
  }

  internal CancellationToken Token => source.Token;
  internal bool IsCancellationRequested => source.IsCancellationRequested;

  internal void SetDeadline(TimeSpan timeout) {
    lock (sync) {
      deadlineAt = timeout == Timeout.InfiniteTimeSpan
          ? null : Environment.TickCount64 + (long)Math.Ceiling(timeout.TotalMilliseconds);
      deadline.Change(timeout, Timeout.InfiniteTimeSpan);
    }
  }

  // Resetting a timer does not revoke a queued callback. Only the current deadline may cancel work.
  private void CancelIfDue() {
    lock (sync) {
      if (deadlineAt is not { } due)
        return;
      long remaining = due - Environment.TickCount64;
      if (remaining > 0) {
        deadline.Change(TimeSpan.FromMilliseconds(remaining), Timeout.InfiniteTimeSpan);
        return;
      }
      _ = CancelAsync();
    }
  }

  internal Task CancelAsync() {
    lock (sync)
      return cancellation ??= source.CancelAsync();
  }

  // The operation owner awaits CancelAsync before disposing or relinquishing ownership.
  public void Dispose() {
    lock (sync) {
      deadlineAt = null;
      deadline.Dispose();
    }
    caller.Dispose();
    source.Dispose();
  }
}
