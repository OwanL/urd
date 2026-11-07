namespace Urd.Backend.Tests.Support;

// Shared timeout and signal helpers for asynchronous tests.
internal static class TestAsync {
  internal static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

  internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
