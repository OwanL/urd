using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Closes an owner's pages after it goes idle and retires the browser once no pages remain.
internal sealed partial class BrowserHost {
  private static readonly TimeSpan OwnerIdleAfter = TimeSpan.FromMinutes(30);
  private static readonly TimeSpan BrowserIdleAfter = TimeSpan.FromMinutes(5);
  private static readonly TimeSpan PendingRecheck = TimeSpan.FromMinutes(1);

  // One background sweep decides idle evictions and the browser exit. It never holds the gate
  // across an await, so agent interactions and page operations are never blocked by it.
  private async Task SweepAsync() {
    while (true) {
      Task? countdown;
      Task wakeTask;
      lock (gate) {
        if (disposed)
          return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset? deadline = NextDeadline();
        wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wakeTask = wake.Task;
        if (deadline is { } due) {
          TimeSpan wait = due - now;
          countdown = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, CancellationToken.None);
        } else {
          countdown = null;
        }
      }
      if (countdown is null) {
        await wakeTask;
        continue;
      }
      if (await Task.WhenAny(countdown, wakeTask) != wakeTask)
        await SweepOnce();
    }
  }

  // Caller holds the gate. The nearest owner idle eviction or the browser exit countdown.
  private DateTimeOffset? NextDeadline() {
    DateTimeOffset? deadline = null;
    foreach (OwnerEntry entry in owners.Values) {
      if (entry.Pages == 0)
        continue;
      DateTimeOffset due = entry.DeferredUntil ?? entry.LastActivity + OwnerIdleAfter;
      if (deadline is not { } best || due < best)
        deadline = due;
    }
    if (launch is not null && emptySince is { } empty) {
      DateTimeOffset due = empty + BrowserIdleAfter;
      if (deadline is not { } best || due < best)
        deadline = due;
    }
    return deadline;
  }

  private static bool IsIdleDue(OwnerEntry entry, DateTimeOffset now) =>
      (entry.DeferredUntil ?? entry.LastActivity + OwnerIdleAfter) <= now;

  private async Task SweepOnce() {
    Dictionary<Guid, OwnerEntry> expired = [];
    lock (gate) {
      if (disposed)
        return;
      DateTimeOffset now = DateTimeOffset.UtcNow;
      foreach ((Guid owner, OwnerEntry entry) in owners) {
        if (entry.Pages == 0 || !IsIdleDue(entry, now))
          continue;
        if (entry.Active > 0) {
          // Never close under an in-flight agent interaction; recheck soon.
          entry.DeferredUntil = now + PendingRecheck;
          Signal();
          continue;
        }
        expired.Add(owner, entry);
      }
    }
    foreach ((Guid owner, OwnerEntry entry) in expired) {
      bool pending;
      try {
        pending = await hasPendingQuestion(owner);
      } catch (Exception) {
        // Fail safe: treat an unknown answer as a pending question.
        pending = true;
      }
      IPage[] idlePages;
      lock (gate) {
        if (disposed)
          return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!owners.TryGetValue(owner, out OwnerEntry? current) || current != entry || !IsIdleDue(current, now))
          continue;
        if (pending || current.Active > 0) {
          // A pending question or active operation keeps the owner's pages; recheck soon.
          current.DeferredUntil = now + PendingRecheck;
          Signal();
          continue;
        }
        idlePages = RemoveAllPages(owner, current);
      }
      await CloseRemovedPagesAsync(owner, idlePages);
    }
    lock (gate) {
      if (disposed)
        return;
      DateTimeOffset now = DateTimeOffset.UtcNow;
      if (launch is not null && emptySince is { } empty && now - empty >= BrowserIdleAfter)
        Retire();
    }
  }
}
