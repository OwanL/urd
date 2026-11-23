namespace Urd.Core.Sessions;

public abstract partial class SessionBase {
  private DateTimeOffset? snoozedUntil;

  // Caller holds the session lock. An expired deadline never fences work, including after restart.
  private bool IsSnoozed => snoozedUntil > DateTimeOffset.UtcNow;

  internal async Task<SessionSnoozeStatus> SetSnoozeAsync(DateTimeOffset? until, DateTimeOffset? expectedUntil,
      Action ensureManagerOpen, CancellationToken cancellation) {
    using var _ = await sessionLock.AcquireAsync(cancellation);
    ensureManagerOpen();
    cancellation.ThrowIfCancellationRequested();
    ThrowIfClosed();

    DateTimeOffset now = DateTimeOffset.UtcNow;
    bool snoozed = snoozedUntil > now;
    if (until is { } requested) {
      until = DateTimeOffset.FromUnixTimeMilliseconds(requested.ToUnixTimeMilliseconds());
      if (until <= now)
        throw new ArgumentOutOfRangeException(nameof(until), "Snooze must end in the future.");
      if (snoozed)
        return until == snoozedUntil ? SessionSnoozeStatus.Unchanged : SessionSnoozeStatus.Conflict;
      if (!CanSnooze())
        return SessionSnoozeStatus.Conflict;
    } else {
      if (!snoozed)
        return SessionSnoozeStatus.Unchanged;
      if (expectedUntil != snoozedUntil)
        return SessionSnoozeStatus.Conflict;
    }

    store.Sessions.SetSnoozedUntil(Id, until);
    snoozedUntil = until;
    PublishChange();
    return SessionSnoozeStatus.Changed;
  }

  private bool CanSnooze() {
    if (!CanStartWork || CurrentExecution is not null || manualCompaction.QueuedId is not null || Questions.HasPending)
      return false;

    SessionSnapshot snapshot = ReadCurrentSnapshot();
    return snapshot.UnsettledExecutionId is null && !snapshot.AdmissionFenced && !snapshot.StopRequested
        && snapshot.PendingInputs.Count == 0;
  }
}
