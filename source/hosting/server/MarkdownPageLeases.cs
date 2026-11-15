using Urd.Core.MarkdownRendering;

namespace Urd.Server;

// One SSE page owns each delivered lease until its client releases it or the page closes.
internal sealed class MarkdownPageLeases {
  private readonly object leasesLock = new();
  private readonly Dictionary<Guid, LeaseRegistration> leases = new();
  private bool closed;

  internal async Task<MessageMarkdownLease> OpenAsync(Guid leaseId,
      Func<CancellationToken, Task<MessageMarkdownLease>> open, CancellationToken requestCancellation) {
    ArgumentNullException.ThrowIfNull(open);
    requestCancellation.ThrowIfCancellationRequested();
    if (leaseId == Guid.Empty)
      throw new ArgumentException("A Markdown lease ID is required.", nameof(leaseId));

    LeaseRegistration pending;
    lock (leasesLock) {
      if (closed)
        throw new MarkdownUnavailableException();
      if (leases.ContainsKey(leaseId))
        throw new DuplicateLeaseIdException();
      pending = new(this, leaseId);
      leases.Add(leaseId, pending);
    }

    CancellationToken cancellation = pending.Cancellation;
    try {
      CancellationTokenRegistration registration = requestCancellation.UnsafeRegister(
          static state => ((LeaseRegistration)state!).CancelFromRequest(), pending);
      pending.SetRequestCancellation(registration);
      cancellation.ThrowIfCancellationRequested();
      MessageMarkdownLease lease = await (open(cancellation)
          ?? throw new InvalidOperationException("The Markdown lease opener returned no task."))
          .ConfigureAwait(false);

      if (!TryAttachOpenedLease(leaseId, pending, lease, cancellation)) {
        lease.Dispose();
        throw new OperationCanceledException(cancellation);
      }

      return lease;
    } catch {
      RemovePending(leaseId, pending);
      throw;
    } finally {
      pending.Dispose();
    }
  }

  // Run the callback under the leases lock so release and page close cannot
  // dispose the lease between resolving its ID and completing the read.
  internal T Read<T>(Guid leaseId, Func<MessageMarkdownLease, T> read) {
    ArgumentNullException.ThrowIfNull(read);
    lock (leasesLock) {
      if (!leases.TryGetValue(leaseId, out LeaseRegistration? current) || current.Lease is not { } lease)
        throw new MarkdownUnavailableException();
      return read(lease);
    }
  }

  internal void Release(Guid leaseId) {
    LeaseRegistration? current;
    lock (leasesLock) {
      if (!leases.Remove(leaseId, out current))
        return;
    }

    current.StopRequestCancellation();
    if (current.Lease is { } lease)
      lease.Dispose();
    else
      current.Cancel();
  }

  internal void Close() {
    LeaseRegistration[] current;
    lock (leasesLock) {
      if (closed)
        return;
      closed = true;
      current = leases.Values.ToArray();
      leases.Clear();
    }

    foreach (LeaseRegistration pending in current) {
      pending.StopRequestCancellation();
      if (pending.Lease is { } lease)
        lease.Dispose();
      else
        pending.Cancel();
    }
  }

  private bool TryAttachOpenedLease(Guid leaseId, LeaseRegistration pending,
      MessageMarkdownLease lease, CancellationToken cancellation) {
    lock (leasesLock) {
      bool attached = !closed && !cancellation.IsCancellationRequested
          && leases.TryGetValue(leaseId, out LeaseRegistration? current) && ReferenceEquals(current, pending);
      if (attached)
        pending.Lease = lease;
      else if (leases.TryGetValue(leaseId, out LeaseRegistration? retained) && ReferenceEquals(retained, pending))
        leases.Remove(leaseId);
      return attached;
    }
  }

  private void RemovePending(Guid leaseId, LeaseRegistration pending) {
    lock (leasesLock) {
      if (leases.TryGetValue(leaseId, out LeaseRegistration? current)
          && ReferenceEquals(current, pending) && current.Lease is null)
        leases.Remove(leaseId);
    }
  }

  // Tracks an opening lease and owns its request cancellation until attachment completes.
  private sealed class LeaseRegistration(MarkdownPageLeases owner, Guid leaseId) : IDisposable {
    private readonly object cancellationGate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly object registrationGate = new();
    private CancellationTokenRegistration requestCancellation;
    private bool requestCancellationStopped;
    private bool cancellationInProgress;
    private bool finishAfterCancellation;
    private bool finished;

    internal CancellationToken Cancellation => cancellation.Token;
    internal MessageMarkdownLease? Lease { get; set; }

    internal void SetRequestCancellation(CancellationTokenRegistration registration) {
      bool unregister;
      lock (registrationGate) {
        unregister = requestCancellationStopped;
        if (!unregister)
          requestCancellation = registration;
      }
      if (unregister)
        registration.Unregister();
    }

    internal void StopRequestCancellation() {
      CancellationTokenRegistration registration;
      lock (registrationGate) {
        requestCancellationStopped = true;
        registration = requestCancellation;
        requestCancellation = default;
      }
      registration.Unregister();
    }

    internal void CancelFromRequest() => owner.CancelPending(leaseId, this);

    internal void Cancel() {
      lock (cancellationGate) {
        if (finished || cancellationInProgress || cancellation.IsCancellationRequested)
          return;
        cancellationInProgress = true;
      }

      try {
        cancellation.Cancel();
      } catch (AggregateException) {
        // Cancellation still reaches every registered callback; one failed callback
        // must not prevent cleanup of the other leases owned by this page.
      } finally {
        lock (cancellationGate) {
          cancellationInProgress = false;
          if (finishAfterCancellation)
            DisposeCancellation();
        }
      }
    }

    public void Dispose() {
      StopRequestCancellation();
      lock (cancellationGate) {
        if (finished)
          return;
        if (cancellationInProgress) {
          finishAfterCancellation = true;
          return;
        }
        DisposeCancellation();
      }
    }

    private void DisposeCancellation() {
      finished = true;
      cancellation.Dispose();
    }
  }

  private void CancelPending(Guid leaseId, LeaseRegistration pending) {
    bool removed = false;
    lock (leasesLock) {
      if (leases.TryGetValue(leaseId, out LeaseRegistration? current)
          && ReferenceEquals(current, pending) && current.Lease is null) {
        leases.Remove(leaseId);
        removed = true;
      }
    }

    if (!removed)
      return;
    pending.StopRequestCancellation();
    pending.Cancel();
  }

  // Rejects a lease ID already reserved or attached on this page.
  internal sealed class DuplicateLeaseIdException()
      : InvalidOperationException("The Markdown lease ID is already active on this page.");
}
