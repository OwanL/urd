using Microsoft.Playwright;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class PlaywrightProfileTests {
  [Fact]
  public async Task HandoffKeepsOwnershipAndClosesEachModeBeforeStartingNext() {
    var launcher = new FakeLauncher();
    var profile = new PlaywrightProfile("profile-root", launcher);
    await using var lease = await profile.AcquireAsync(CancellationToken.None);
    await lease.WithAutomationAsync((_, _) => Task.FromResult(0), CancellationToken.None);
    var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<bool> handoff = lease.HandoffAsync(resume.Task, CancellationToken.None);
    Assert.Equal(new[] { "start:True", "close:True", "start:False" }, launcher.Events);
    using var cancelled = new CancellationTokenSource();
    Task<PlaywrightProfileLease> competing = profile.AcquireAsync(cancelled.Token);
    Task<int> automation = lease.WithAutomationAsync((_, _) => Task.FromResult(1), CancellationToken.None);
    Assert.False(competing.IsCompleted);
    Assert.False(automation.IsCompleted);
    await cancelled.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => competing);
    resume.SetResult();
    Assert.True(await handoff);
    Assert.Equal(1, await automation);
    Assert.Equal(new[] { "start:True", "close:True", "start:False", "close:False", "start:True" }, launcher.Events);
    Assert.Single(launcher.Paths.Distinct());
  }

  [Fact]
  public async Task CancelOrWindowCloseEndsHandoffWithoutResuming() {
    var launcher = new FakeLauncher();
    var profile = new PlaywrightProfile("profile-root", launcher);
    await using (var lease = await profile.AcquireAsync(CancellationToken.None)) {
      using var cancellation = new CancellationTokenSource();
      Task<bool> handoff = lease.HandoffAsync(new TaskCompletionSource().Task, cancellation.Token);
      await cancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handoff);
      await Assert.ThrowsAsync<InvalidOperationException>(() => lease.WithAutomationAsync((_, _) => Task.FromResult(0), CancellationToken.None));
      Assert.Equal(new[] { "start:False", "close:False" }, launcher.Events);
    }
    await using (var lease = await profile.AcquireAsync(CancellationToken.None)) {
      Task<bool> handoff = lease.HandoffAsync(new TaskCompletionSource().Task, CancellationToken.None);
      Task<int> pending = lease.WithAutomationAsync((_, _) => Task.FromResult(0), CancellationToken.None);
      launcher.Current!.WindowClosed();
      Assert.False(await handoff);
      await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
      Assert.Equal("close:False", launcher.Events[^1]);
    }
  }

  [Fact]
  public async Task CancellationBeforeBrowserLaunchAllowsNextOwner() {
    using var cancelled = new CancellationTokenSource();
    var launcher = new FakeLauncher { BeforeLaunch = cancelled.Cancel };
    var profile = new PlaywrightProfile("profile-root", launcher);
    await using (var lease = await profile.AcquireAsync(CancellationToken.None)) {
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        lease.HandoffAsync(Task.CompletedTask, cancelled.Token));
      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        lease.WithAutomationAsync((_, _) => Task.FromResult(0), CancellationToken.None));
    }
    Assert.Empty(launcher.Events);
    launcher.BeforeLaunch = null;
    await using var replacement = await profile.AcquireAsync(CancellationToken.None);
    await replacement.WithAutomationAsync((_, _) => Task.FromResult(0), CancellationToken.None);
    Assert.Equal(new[] { "start:True" }, launcher.Events);
  }

  [Fact]
  public async Task FailedClosePoisonsOwner() {
    var launcher = new FakeLauncher { FailClose = true };
    var profile = new PlaywrightProfile("profile-root", launcher);
    var lease = await profile.AcquireAsync(CancellationToken.None);
    await lease.WithAutomationAsync((_, _) => Task.FromResult(0), CancellationToken.None);
    await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.DisposeAsync());
    await Assert.ThrowsAsync<InvalidOperationException>(() => profile.AcquireAsync(CancellationToken.None));
    Assert.Equal(new[] { "start:True", "close:True" }, launcher.Events);
  }

  private sealed class FakeLauncher : IPlaywrightProfileLauncher {
    public List<string> Events { get; } = [];
    public List<string> Paths { get; } = [];
    public FakeBrowser? Current { get; private set; }
    public bool FailClose { get; set; }
    public Action? BeforeLaunch { get; set; }

    public Task<IPlaywrightProfileBrowser> LaunchAsync(string profilePath, bool headless, CancellationToken cancellation) {
      BeforeLaunch?.Invoke();
      cancellation.ThrowIfCancellationRequested();
      Paths.Add(profilePath);
      Events.Add($"start:{headless}");
      Current = new FakeBrowser(this, headless);
      return Task.FromResult<IPlaywrightProfileBrowser>(Current);
    }
  }

  private sealed class FakeBrowser(FakeLauncher launcher, bool headless) : IPlaywrightProfileBrowser {
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IBrowserContext Context => null!;
    public Task Closed => closed.Task;
    public void WindowClosed() => closed.TrySetResult();
    public Task CloseAsync() {
      launcher.Events.Add($"close:{headless}");
      if (launcher.FailClose)
        throw new InvalidOperationException("close failed");
      closed.TrySetResult();
      return Task.CompletedTask;
    }
  }
}
