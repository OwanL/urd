using Urd.Core.Sessions;

namespace Urd.Core.Tools;

// One execution at a time may own live tabs. Only its browser profile survives settlement.
internal sealed class PlaywrightAutomation(SessionManager sessions, string dataRoot, Func<int> hostPort,
    IReadOnlyList<Uri> localOrigins) {
  private readonly PlaywrightProfile profile = new(dataRoot);
  private readonly Uri[] localOrigins = localOrigins.ToArray();
  private readonly Lock gate = new();
  private Guid? execution;
  private PlaywrightRun? run;
  private bool operating;
  private bool lanHosting;

  internal IDisposable PauseForLan() {
    lock (gate) {
      if (operating || execution is not null)
        throw new InvalidOperationException("Finish or stop the browser task before enabling LAN hosting.");
      if (lanHosting)
        throw new InvalidOperationException("Playwright is already paused for LAN hosting.");
      lanHosting = true;
      return new LanPause(this);
    }
  }

  private void ResumeAfterLan() {
    lock (gate)
      lanHosting = false;
  }

  public async Task<ToolOutput> RunAsync(Guid executionId, Func<PlaywrightRun, CancellationToken, Task<ToolOutput>> operation,
      CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    lock (gate) {
      if (lanHosting)
        return new ToolOutput("Playwright is unavailable while unauthenticated LAN hosting is active.", [], IsError: true);
      if (operating || execution is { } owner && owner != executionId)
        return new ToolOutput("The shared browser is busy. Wait for the current browser task to finish.", [], IsError: true);
      operating = true;
    }
    try {
      if (run is null) {
        int blockedPort = hostPort();
        if (blockedPort <= 0)
          return new ToolOutput("The browser is not ready yet.", [], IsError: true);
        run = await OpenRunAsync(blockedPort, cancellation);
        lock (gate)
          execution = executionId;
      }
      return await operation(run, cancellation);
    } finally {
      lock (gate)
        operating = false;
    }
  }

  // Execution settlement calls this only after foreground and background work has joined.
  public async Task ReleaseAsync(Guid executionId) {
    PlaywrightRun? owned;
    lock (gate) {
      if (execution != executionId || operating)
        return;
      owned = run;
      operating = true;
    }
    try {
      if (owned is not null)
        await owned.DisposeAsync();
    } finally {
      lock (gate) {
        run = null;
        execution = null;
        operating = false;
      }
    }
  }

  private async Task<PlaywrightRun> OpenRunAsync(int blockedPort, CancellationToken cancellation) {
    var network = new PlaywrightNetwork(() => blockedPort);
    try {
      if (!localOrigins.All(network.TryAllowNavigation))
        throw new InvalidOperationException("A configured local origin is blocked.");
      var launcher = new PlaywrightProfileLauncher(network.Address);
      PlaywrightProfileLease lease = await profile.TryAcquireAsync(launcher, cancellation)
          ?? throw new InvalidOperationException("The browser profile is busy.");
      return new PlaywrightRun(sessions, lease, network);
    } catch {
      await network.DisposeAsync();
      throw;
    }
  }

  private sealed class LanPause(PlaywrightAutomation automation) : IDisposable {
    private PlaywrightAutomation? owner = automation;

    public void Dispose() => Interlocked.Exchange(ref owner, null)?.ResumeAfterLan();
  }
}
