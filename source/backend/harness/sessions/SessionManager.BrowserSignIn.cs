using Urd.Core.Tools;

namespace Urd.Core.Sessions;

public sealed partial class SessionManager {
  private PlaywrightAutomation? playwright;

  // The host calls this once before publishing any session routes.
  public void ConfigurePlaywright(string dataRoot, Func<int> hostPort, IReadOnlyList<Uri> localOrigins) {
    if (playwright is not null)
      throw new InvalidOperationException("Playwright is already configured.");
    playwright = new PlaywrightAutomation(this, dataRoot, hostPort, localOrigins);
  }

  // LAN and authenticated browsing cannot own this host at the same time.
  public IDisposable? PausePlaywrightForLan() => playwright?.PauseForLan();

  public async Task<BrowserSignInView?> ReadBrowserSignInAsync(Guid sessionId, CancellationToken cancellation = default) {
    PrimarySession session = await registry.RunOpenAsync(() => FindPrimary(sessionId, rejectChild: true)
        ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist."), cancellation);
    return await session.ReadBrowserSignInAsync(cancellation);
  }

  public async Task<bool> ActOnBrowserSignInAsync(Guid sessionId, Guid id, string action,
      CancellationToken cancellation = default) {
    PrimarySession session = await registry.RunOpenAsync(() => FindPrimary(sessionId, rejectChild: true)
        ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist."), cancellation);
    return await session.ActOnBrowserSignInAsync(id, action, cancellation);
  }

  internal async Task<bool> RunBrowserSignInAsync(Guid sessionId, string origin,
      Func<Task, Func<Task>, CancellationToken, Task<bool>> openWindow, CancellationToken cancellation) {
    PrimarySession session = await registry.RunOpenAsync(() => FindPrimary(sessionId, rejectChild: true)
        ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist."), cancellation);
    return await session.RunBrowserSignInAsync(origin, openWindow, cancellation);
  }
}
