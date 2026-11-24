namespace Urd.Core.Sessions;

public abstract partial class SessionBase {
  private DateTimeOffset? snoozedUntil;
  private Task? snoozeCleanup;

  // Caller holds the session lock. An expired deadline never fences work, including after restart.
  private bool IsSnoozed => snoozedUntil > DateTimeOffset.UtcNow;

  internal async Task<SessionSnoozeStatus> SetSnoozeAsync(DateTimeOffset? until, DateTimeOffset? expectedUntil,
      Action ensureManagerOpen, Func<Guid, Task> closeBrowserOwner, CancellationToken cancellation) {
    while (true) {
      TaskCompletionSource? cleanup = null;
      Task? pending;
      SessionSnoozeStatus? change = null;
      using (await sessionLock.AcquireAsync(cancellation)) {
        ensureManagerOpen();
        cancellation.ThrowIfCancellationRequested();
        ThrowIfClosed();
        pending = snoozeCleanup;
        if (pending is null) {
          change = SetSnoozeUnderLock(until, expectedUntil);
          if (change == SessionSnoozeStatus.Changed && until is not null)
            cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        } else if (pending.IsFaulted) {
          // Retry a failed cleanup before allowing restore, close or new work.
          cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (cleanup is not null)
          snoozeCleanup = cleanup.Task;
      }

      if (cleanup is not null) {
        await CloseSnoozedBrowserAsync(cleanup, closeBrowserOwner);
        if (change is { } changed)
          return changed;
      } else if (change is { } result) {
        return result;
      } else {
        await pending!.WaitAsync(cancellation);
      }
    }
  }

  private async Task CloseSnoozedBrowserAsync(TaskCompletionSource cleanup, Func<Guid, Task> closeBrowserOwner) {
    try {
      // Cleanup is outside the session lock; the fence prevents restore or new work racing it.
      await closeBrowserOwner(Id);
    } catch (Exception failure) {
      using (await sessionLock.AcquireAsync()) {
        try {
          // A snooze whose browser could not be closed must not strand the session.
          store.Sessions.SetSnoozedUntil(Id, null);
          snoozedUntil = null;
          snoozeCleanup = null;
          PublishChange();
          cleanup.TrySetResult();
        } catch (Exception rollbackFailure) {
          var combined = new AggregateException("Snooze cleanup and rollback failed.", failure, rollbackFailure);
          cleanup.TrySetException(combined);
          _ = cleanup.Task.Exception;
          throw combined;
        }
      }
      throw;
    }
    using (await sessionLock.AcquireAsync()) {
      snoozeCleanup = null;
      if (!IsSnoozed)
        PublishChange();
      cleanup.TrySetResult();
    }
  }

  internal async Task JoinSnoozeCleanupAsync() {
    Task? pending;
    using (await sessionLock.AcquireAsync())
      pending = snoozeCleanup;
    if (pending is not null)
      await pending;
  }

  // Caller holds the session lock.
  private SessionSnoozeStatus SetSnoozeUnderLock(DateTimeOffset? until, DateTimeOffset? expectedUntil) {
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
