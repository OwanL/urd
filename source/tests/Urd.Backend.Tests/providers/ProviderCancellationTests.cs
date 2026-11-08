using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

// Cancellation callbacks remain owned work even after the provider HTTP operation has unwound.
public sealed class ProviderCancellationTests {
  [Theory]
  [InlineData("remove")]
  [InlineData("settings")]
  [InlineData("caller")]
  public async Task ShutdownRetainsCallbackDrainAfterAnotherCancellationOriginAndHttpUnwind(string origin) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var caller = new CancellationTokenSource();
    var entered = Signal();
    var httpUnwound = Signal();
    var callbackEntered = Signal();
    var callbackRelease = Signal();
    var callbackFinished = Signal();
    CancellationTokenRegistration registration = default;
    var handler = new ControlledProviderHandler(async (_, token) => {
      // Retain registration outside this operation: disposing it here would accidentally
      // make HTTP cleanup itself join the callback and hide the manager's ownership bug.
      registration = token.Register(() => HoldCallback(callbackEntered, callbackRelease, callbackFinished));
      entered.TrySetResult();
      try {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("The controlled provider operation must be cancelled.");
      } finally {
        httpUnwound.TrySetResult();
      }
    });
    var manager = new ProviderManager(store, () => new Uri("http://127.0.0.1:54321/auth/callback"), handler);
    Task? operation = null;
    Task? trigger = null;
    Task? shutdown = null;
    Task? repeated = null;
    try {
      var settings = new CustomProviderSettings("Fixture", LlmApi.OpenAIResponses,
          new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.None);
      ProviderConnection connection = await manager.AddCustomAsync(settings);
      operation = manager.TestModelAsync(connection.Id, "fixture-model", caller.Token);
      await entered.Task.WaitAsync(TestTimeout);
      trigger = origin switch {
        "remove" => manager.RemoveAsync(connection.Id),
        "settings" => manager.UpdateCustomAsync(connection.Id,
            settings with { Endpoint = new Uri("https://replacement.invalid/v1/") }),
        _ => caller.CancelAsync()
      };
      await Task.WhenAll(callbackEntered.Task, httpUnwound.Task).WaitAsync(TestTimeout);
      shutdown = manager.ShutdownAsync();
      repeated = manager.ShutdownAsync();

      Assert.False(operation.IsCompleted);
      Assert.False(shutdown.IsCompleted);
      Assert.False(repeated.IsCompleted);
      Assert.False(callbackFinished.Task.IsCompleted);
      Assert.Equal(0, handler.DisposeCalls);
      Assert.Throws<InvalidOperationException>(() => manager.Dispose());

      callbackRelease.TrySetResult();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TestTimeout));
      await Task.WhenAll(trigger, shutdown, repeated).WaitAsync(TestTimeout);
      Assert.True(callbackFinished.Task.IsCompletedSuccessfully);
      Assert.Single(handler.Requests);
      manager.Dispose();
      Assert.Equal(1, handler.DisposeCalls);
    } finally {
      callbackRelease.TrySetResult();
      Task cleanup = manager.ShutdownAsync();
      await ObserveAsync(operation, trigger, shutdown, repeated);
      await cleanup.WaitAsync(TestTimeout);
      await registration.DisposeAsync();
      manager.Dispose();
    }
  }

  [Fact]
  public async Task RepeatedExplicitCancellationJoinsTheOriginalHeldCallback() {
    using var cancellation = new ProviderCancellation(Timeout.InfiniteTimeSpan);
    var entered = Signal();
    var release = Signal();
    var finished = Signal();
    using var registration = cancellation.Token.Register(() => HoldCallback(entered, release, finished));
    Task? first = null;
    Task? repeated = null;
    try {
      first = cancellation.CancelAsync();
      await entered.Task.WaitAsync(TestTimeout);
      repeated = cancellation.CancelAsync();
      Assert.True(cancellation.IsCancellationRequested);
      Assert.True(cancellation.Token.IsCancellationRequested);
      Assert.False(first.IsCompleted);
      Assert.False(repeated.IsCompleted);
      Assert.False(finished.Task.IsCompleted);
      release.TrySetResult();
      await Task.WhenAll(first, repeated).WaitAsync(TestTimeout);
      Assert.True(finished.Task.IsCompletedSuccessfully);
    } finally {
      release.TrySetResult();
      await ObserveAsync(first, repeated);
      await cancellation.CancelAsync().WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task TimerOriginRetainsTheCallbackDrainForLaterCancellationWaiters() {
    using var cancellation = new ProviderCancellation(TimeSpan.FromSeconds(1));
    var entered = Signal();
    var release = Signal();
    var finished = Signal();
    CancellationTokenRegistration registration = default;
    // Register off-thread so even a deadline racing registration can be released by finally.
    Task registering = Task.Run(() => {
      registration = cancellation.Token.Register(() => HoldCallback(entered, release, finished));
    });
    Task? first = null;
    Task? repeated = null;
    try {
      await entered.Task.WaitAsync(TestTimeout); // The actual deadline, not an explicit CancelAsync, starts cancellation.
      first = cancellation.CancelAsync();
      repeated = cancellation.CancelAsync();
      Assert.True(cancellation.IsCancellationRequested);
      Assert.False(first.IsCompleted);
      Assert.False(repeated.IsCompleted);
      Assert.False(finished.Task.IsCompleted);
      release.TrySetResult();
      await Task.WhenAll(first, repeated, registering).WaitAsync(TestTimeout);
      Assert.True(finished.Task.IsCompletedSuccessfully);
    } finally {
      release.TrySetResult();
      await ObserveAsync(first, repeated, registering);
      await cancellation.CancelAsync().WaitAsync(TestTimeout);
      await registration.DisposeAsync();
    }
  }

  private static void HoldCallback(TaskCompletionSource entered, TaskCompletionSource release, TaskCompletionSource finished) {
    entered.TrySetResult();
    try {
      release.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
    } finally {
      finished.TrySetResult();
    }
  }

  private static async Task ObserveAsync(params Task?[] operations) {
    Task joined = Task.WhenAll(operations.OfType<Task>());
    try {
      await joined.WaitAsync(TestTimeout);
    } catch when (joined.IsCompleted) {
      // Observe cancellation/failure while preserving the test's original assertion.
      // A timeout is not a join and must still fail cleanup.
    }
  }
}
