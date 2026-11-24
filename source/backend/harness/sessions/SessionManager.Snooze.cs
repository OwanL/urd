namespace Urd.Core.Sessions;

public sealed partial class SessionManager {
  public event Action<Guid>? SessionSnoozeChanged;

  public Task<SnoozedSessions> ReadSnoozedAsync(CancellationToken cancellation = default) => registry.RunOpenAsync(() => {
    DateTimeOffset now = DateTimeOffset.UtcNow;
    return new SnoozedSessions(now, store.Sessions.ReadSnoozed(now));
  }, cancellation);

  public async Task<SessionSnoozeStatus> SetSnoozeAsync(Guid sessionId, DateTimeOffset? until, DateTimeOffset? expectedUntil,
      CancellationToken cancellation = default) {
    if (sessionId == Guid.Empty || until.HasValue == expectedUntil.HasValue)
      throw new ArgumentException("Supply a session and either a deadline to snooze or its current deadline to restore.");

    PrimarySession session = await registry.RunOpenAsync(() => FindPrimary(sessionId, rejectChild: false)
        ?? throw new KeyNotFoundException($"Primary session '{sessionId}' does not exist."), cancellation);
    bool notified = false;
    try {
      SessionSnoozeStatus status = await session.SetSnoozeAsync(until, expectedUntil, registry.ThrowIfShuttingDown, id => {
        notified = true;
        Notify(SessionSnoozeChanged, id);
        return CloseBrowserOwnerAsync(id);
      }, cancellation);
      if (status == SessionSnoozeStatus.Changed && until is null)
        Notify(SessionSnoozeChanged, sessionId);
      return status;
    } catch {
      // Cleanup may have rolled back an already-published snooze.
      if (notified)
        Notify(SessionSnoozeChanged, sessionId);
      throw;
    }
  }
}
