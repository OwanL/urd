using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

public sealed class ProviderCancellationTests {
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task DisconnectAndShutdownJoinCancellationCallbacks(bool disconnect) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var httpUnwound = Signal();
    var callbackEntered = Signal();
    var callbackRelease = Signal();
    var callbackFinished = Signal();
    CancellationTokenRegistration registration = default;
    var handler = new ControlledProviderHandler(async (_, token) => {
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
    Task? lifecycle = null;
    Task? shutdown = null;
    Task? repeated = null;
    try {
      var settings = new CustomProviderSettings("Fixture", LlmApi.OpenAIResponses,
          new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.None);
      ProviderConnection connection = await manager.AddCustomAsync(settings);
      operation = manager.TestModelAsync(connection.Id, "fixture-model");
      await entered.Task.WaitAsync(TestTimeout);
      Task<ProviderDisconnectResult>? disconnecting = null;
      if (disconnect) {
        disconnecting = manager.DisconnectAsync(connection.Id);
        lifecycle = disconnecting;
      } else {
        lifecycle = manager.ShutdownAsync();
      }
      await Task.WhenAll(callbackEntered.Task, httpUnwound.Task).WaitAsync(TestTimeout);
      shutdown = manager.ShutdownAsync();
      repeated = manager.ShutdownAsync();

      Assert.False(operation.IsCompleted);
      Assert.False(lifecycle.IsCompleted);
      Assert.False(shutdown.IsCompleted);
      Assert.False(repeated.IsCompleted);
      Assert.False(callbackFinished.Task.IsCompleted);
      Assert.Equal(0, handler.DisposeCalls);
      Assert.Throws<InvalidOperationException>(() => manager.Dispose());

      callbackRelease.TrySetResult();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TestTimeout));
      if (disconnecting is not null)
        Assert.True((await disconnecting.WaitAsync(TestTimeout)).LocalCredentialsCleared);
      await Task.WhenAll(lifecycle, shutdown, repeated).WaitAsync(TestTimeout);
      Assert.True(callbackFinished.Task.IsCompletedSuccessfully);
      Assert.Single(handler.Requests);
      manager.Dispose();
      Assert.Equal(1, handler.DisposeCalls);
    } finally {
      callbackRelease.TrySetResult();
      Task cleanup = manager.ShutdownAsync();
      await ObserveAsync(operation, lifecycle, shutdown, repeated);
      await cleanup.WaitAsync(TestTimeout);
      await registration.DisposeAsync();
      manager.Dispose();
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
      // Observe cancellation/failure without mistaking a timeout for a join.
    }
  }
}
