using Urd.Core.MarkdownRendering;

namespace Urd.Server;

// One SSE page owns each delivered lease until its client releases it or the page closes.
internal sealed class MarkdownPageLeases {
  private readonly object gate = new();
  private readonly Dictionary<Guid, OpenLease> leases = new();
  private bool closed;

  internal async Task<MessageMarkdownLease> OpenAsync(Guid token,
      Func<CancellationToken, Task<MessageMarkdownLease>> open, CancellationToken requestCancellation) {
    ArgumentNullException.ThrowIfNull(open);
    requestCancellation.ThrowIfCancellationRequested();
    if (token == Guid.Empty)
      throw new ArgumentException("A Markdown lease token is required.", nameof(token));

    OpenLease pending;
    lock (gate) {
      if (closed)
        throw new MarkdownUnavailableException();
      if (leases.ContainsKey(token))
        throw new DuplicateTokenException();
      pending = new(this, token);
      leases.Add(token, pending);
    }

    CancellationToken cancellation = pending.Cancellation;
    try {
      CancellationTokenRegistration registration = requestCancellation.UnsafeRegister(
          static state => ((OpenLease)state!).CancelFromRequest(), pending);
      pending.SetRequestCancellation(registration);
      cancellation.ThrowIfCancellationRequested();
      MessageMarkdownLease lease = await (open(cancellation)
          ?? throw new InvalidOperationException("The Markdown lease opener returned no task."))
          .ConfigureAwait(false);

      bool attached;
      lock (gate) {
        attached = !closed && !cancellation.IsCancellationRequested
            && leases.TryGetValue(token, out OpenLease? current) && ReferenceEquals(current, pending);
        if (attached)
          pending.Lease = lease;
        else if (leases.TryGetValue(token, out OpenLease? retained) && ReferenceEquals(retained, pending))
          leases.Remove(token);
      }

      if (!attached) {
        lease.Dispose();
        throw new OperationCanceledException(cancellation);
      }

      return lease;
    } catch {
      RemovePending(token, pending);
      throw;
    } finally {
      pending.Dispose();
    }
  }

  // Run the callback under the registry gate so release and page close cannot
  // dispose the lease between resolving its token and completing the read.
  internal T Read<T>(Guid token, Func<MessageMarkdownLease, T> read) {
    ArgumentNullException.ThrowIfNull(read);
    lock (gate) {
      if (!leases.TryGetValue(token, out OpenLease? current) || current.Lease is not { } lease)
        throw new MarkdownUnavailableException();
      return read(lease);
    }
  }

  internal void Release(Guid token) {
    OpenLease? current;
    lock (gate) {
      if (!leases.Remove(token, out current))
        return;
    }

    current.StopRequestCancellation();
    if (current.Lease is { } lease)
      lease.Dispose();
    else
      current.Cancel();
  }

  internal void Close() {
    OpenLease[] current;
    lock (gate) {
      if (closed)
        return;
      closed = true;
      current = leases.Values.ToArray();
      leases.Clear();
    }

    foreach (OpenLease pending in current) {
      pending.StopRequestCancellation();
      if (pending.Lease is { } lease)
        lease.Dispose();
      else
        pending.Cancel();
    }
  }

  private void RemovePending(Guid token, OpenLease pending) {
    lock (gate) {
      if (leases.TryGetValue(token, out OpenLease? current)
          && ReferenceEquals(current, pending) && current.Lease is null)
        leases.Remove(token);
    }
  }

  private sealed class OpenLease(MarkdownPageLeases owner, Guid token) : IDisposable {
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

    internal void CancelFromRequest() => owner.CancelPending(token, this);

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

  private void CancelPending(Guid token, OpenLease pending) {
    bool removed = false;
    lock (gate) {
      if (leases.TryGetValue(token, out OpenLease? current)
          && ReferenceEquals(current, pending) && current.Lease is null) {
        leases.Remove(token);
        removed = true;
      }
    }

    if (!removed)
      return;
    pending.StopRequestCancellation();
    pending.Cancel();
  }

  internal sealed class DuplicateTokenException()
      : InvalidOperationException("The Markdown lease token is already active on this page.");
}
