using System.Net;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

public sealed class LanHostingTests {
  private static readonly IPAddress Address = IPAddress.Parse("192.168.1.10");

  [Fact]
  public async Task EnablePublishesAndPersistsAndShutdownKeepsPreference() {
    var access = new LanAccess();
    bool saved = true;
    var listener = new Listener();
    await using (var hosting = new LanHosting(access, 1234, () => [Address],
        _ => Task.FromResult<IAsyncDisposable>(listener), enabled => saved = enabled)) {
      // The caller restores a saved choice using the same serialized transition.
      await hosting.SetEnabledAsync(saved);
      Assert.True(hosting.Enabled);
      Assert.Equal(new[] { "http://192.168.1.10:1234" }, hosting.Origins);
      Assert.True(saved);
    }
    Assert.True(saved);
    Assert.True(listener.Disposed);
    Assert.False(access.Snapshot.Enabled);
  }

  [Fact]
  public async Task FailedEnableNeverLeavesAdmissionOrListenerEnabled() {
    var listener = new Listener();
    var access = new LanAccess();
    await using var hosting = new LanHosting(access, 1234, () => [Address],
        _ => Task.FromResult<IAsyncDisposable>(listener), _ => throw new InvalidOperationException("save failed"));
    await Assert.ThrowsAsync<InvalidOperationException>(() => hosting.SetEnabledAsync(true));
    Assert.False(hosting.Enabled);
    Assert.True(listener.Disposed);
  }

  [Fact]
  public async Task BindFailureDoesNotPublishOrPersist() {
    bool persisted = false;
    await using var hosting = new LanHosting(new LanAccess(), 1234, () => [Address],
        _ => Task.FromException<IAsyncDisposable>(new InvalidOperationException("bind failed")), _ => persisted = true);
    await Assert.ThrowsAsync<InvalidOperationException>(() => hosting.SetEnabledAsync(true));
    Assert.False(hosting.Enabled);
    Assert.False(persisted);
  }

  [Fact]
  public async Task FailedDisableStillClosesListener() {
    var listener = new Listener();
    await using var hosting = new LanHosting(new LanAccess(), 1234, () => [Address],
        _ => Task.FromResult<IAsyncDisposable>(listener), value => {
          if (!value)
            throw new InvalidOperationException("save failed");
        });
    await hosting.SetEnabledAsync(true);
    await Assert.ThrowsAsync<InvalidOperationException>(() => hosting.SetEnabledAsync(false));
    Assert.False(hosting.Enabled);
    Assert.True(listener.Disposed);
  }

  [Fact]
  public async Task NoAddressesDoesNotStartOrPersist() {
    await using var hosting = new LanHosting(new LanAccess(), 1234, () => [],
        _ => throw new InvalidOperationException("unexpected start"), _ => throw new InvalidOperationException("unexpected save"));
    var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => hosting.SetEnabledAsync(true));
    Assert.Equal("No private IPv4 addresses are available for LAN hosting.", failure.Message);
  }

  [Fact]
  public async Task DisableWaitsForPendingEnableAndClosesAdmissionBeforeDisposal() {
    var started = new TaskCompletionSource<IAsyncDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
    var access = new LanAccess();
    bool saved = false;
    var listener = new Listener(() => Assert.False(access.Snapshot.Enabled));
    await using var hosting = new LanHosting(access, 1234, () => [Address], _ => started.Task, value => saved = value);
    Task enable = hosting.SetEnabledAsync(true);
    Task disable = hosting.SetEnabledAsync(false);
    started.SetResult(listener);
    await Task.WhenAll(enable, disable);
    Assert.False(hosting.Enabled);
    Assert.False(saved);
    Assert.True(listener.Disposed);
  }

  private sealed class Listener(Action? onDispose = null) : IAsyncDisposable {
    public bool Disposed { get; private set; }
    public ValueTask DisposeAsync() {
      onDispose?.Invoke();
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }
}
