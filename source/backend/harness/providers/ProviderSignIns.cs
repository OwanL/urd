using Urd.Core.Sessions;
using Urd.Core.Storage;

namespace Urd.Core.Providers;

// Coordinates ephemeral sign-in attempts using the manager's shared admission guard.
internal sealed class ProviderSignIns(
    ProviderStorage storage, ChatGptAuthentication authentication, AsyncLock gate, Func<Uri> callbackUri,
    Func<Guid, Task> cancelRequests) {
  private readonly Dictionary<Guid, ProviderSignInAttempt> attempts = [];
  private bool stopping;

  internal async Task<ProviderSignIn> BeginAsync(Guid connectionId) {
    ProviderSignInAttempt attempt;
    bool start = false;
    using (await gate.AcquireAsync()) {
      ThrowIfStopping();
      ProviderState state = storage.Get(connectionId);
      if (state.Connection.Authentication != AuthenticationMethod.OAuth)
        throw new ArgumentException("This connection does not use subscription sign-in.", nameof(connectionId));
      if (state.Disconnecting)
        throw new ProviderOperationException(ProviderFailure.Disconnecting);

      if (!attempts.TryGetValue(connectionId, out attempt!) || attempt.Finished.Task.IsCompleted) {
        attempt = new(state);
        attempts[connectionId] = attempt;
        start = true;
      }
    }

    if (start)
      _ = RunAsync(attempt);
    return await attempt.Ready.Task;
  }

  internal async Task<ProviderSignIn> GetAsync(Guid attemptId, CancellationToken cancellationToken) {
    using var held = await gate.AcquireAsync(cancellationToken);
    return Find(attemptId).Snapshot();
  }

  internal async Task<ProviderSignIn> CompleteAsync(string state, string callbackUrl) {
    ProviderSignInAttempt attempt;
    using (await gate.AcquireAsync()) {
      ThrowIfStopping();
      attempt = attempts.Values.SingleOrDefault(candidate =>
          candidate.Flow is not null && string.Equals(candidate.Flow.State, state, StringComparison.Ordinal))
          ?? throw new ProviderOperationException(ProviderFailure.SignInRejected);
      if (attempt.Status != ProviderSignInStatus.Pending || attempt.Cancellation.IsCancellationRequested)
        throw new ProviderOperationException(ProviderFailure.SignInRejected);
      attempt.Status = ProviderSignInStatus.Completing;
      attempt.Callback.SetResult(callbackUrl);
    }

    await attempt.Finished.Task;
    return attempt.Snapshot();
  }

  internal async Task<ProviderSignIn> CancelAsync(Guid attemptId) {
    ProviderSignInAttempt attempt;
    Task cancelled;
    using (await gate.AcquireAsync()) {
      attempt = Find(attemptId);
      cancelled = Cancel(attempt);
    }
    await cancelled;
    await attempt.Finished.Task;
    return attempt.Snapshot();
  }

  // Called only while the shared guard is held; CancelAsync runs callbacks asynchronously.
  internal Task CancelConnection(Guid connectionId) => attempts.TryGetValue(connectionId, out var attempt)
      ? Task.WhenAll(Cancel(attempt), attempt.Finished.Task) : Task.CompletedTask;

  internal void Forget(Guid connectionId) => attempts.Remove(connectionId);

  internal Task Shutdown() {
    stopping = true;
    return Task.WhenAll(attempts.Values.Select(attempt => Task.WhenAll(Cancel(attempt), attempt.Finished.Task)));
  }

  private async Task RunAsync(ProviderSignInAttempt attempt) {
    try {
      string hostId;
      using (await gate.AcquireAsync())
        hostId = storage.GetOrCreateHostId();
      ChatGptSignIn flow = await authentication.BeginAsync(hostId, callbackUri(),
          attempt.InitialState.Registration, attempt.Cancellation.Token);
      using (await gate.AcquireAsync()) {
        attempt.Flow = flow;
        attempt.ExpiresAt = flow.ExpiresAt;
        attempt.Ready.TrySetResult(attempt.Snapshot());
      }
      string callback = await attempt.Callback.Task.WaitAsync(attempt.Cancellation.Token);
      ProviderAuthentication result = await ChatGptAuthentication.CompleteAsync(flow, callback, attempt.Cancellation.Token);
      Task cancelled;
      using (await gate.AcquireAsync()) {
        attempt.Cancellation.Token.ThrowIfCancellationRequested();
        if (stopping || attempt.Status != ProviderSignInStatus.Completing)
          throw new OperationCanceledException(attempt.Cancellation.Token);
        storage.SetCredentials(attempt.InitialState.Connection.Id, attempt.InitialState.Generation,
            result.Registration, result.Credentials);
        attempt.Status = ProviderSignInStatus.Completed;
        cancelled = cancelRequests(attempt.InitialState.Connection.Id);
      }
      await cancelled;
    } catch (OperationCanceledException) when (attempt.Cancellation.IsCancellationRequested || stopping) {
      using var held = await gate.AcquireAsync();
      attempt.Status = attempt.Cancelled ? ProviderSignInStatus.Cancelled : ProviderSignInStatus.Expired;
    } catch (Exception failure) {
      using var held = await gate.AcquireAsync();
      attempt.Status = ProviderSignInStatus.Failed;
      attempt.Error = failure is ProviderOperationException providerFailure
          ? providerFailure.Failure.ToString() : "SignInFailed";
    } finally {
      await FinishAsync(attempt);
    }
  }

  private async Task FinishAsync(ProviderSignInAttempt attempt) {
    try {
      try {
        await attempt.Cancellation.CancelAsync();
      } finally {
        attempt.Flow?.Dispose();
      }
    } catch (Exception) {
      using var held = await gate.AcquireAsync();
      attempt.Status = ProviderSignInStatus.Failed;
      attempt.Error = "SignInCleanupFailed";
    } finally {
      // A completed attempt is retained only until replacement or connection removal.
      using var held = await gate.AcquireAsync();
      attempt.Ready.TrySetResult(attempt.Snapshot());
      attempt.Cancellation.Dispose();
      attempt.Finished.TrySetResult();
    }
  }

  private ProviderSignInAttempt Find(Guid id) => attempts.Values.FirstOrDefault(attempt => attempt.Id == id)
      ?? throw new KeyNotFoundException("The sign-in attempt was not found.");

  private static Task Cancel(ProviderSignInAttempt attempt) {
    if (attempt.Finished.Task.IsCompleted || attempt.Status == ProviderSignInStatus.Completed)
      return Task.CompletedTask;
    attempt.Cancelled = true;
    return attempt.Cancellation.CancelAsync();
  }

  private void ThrowIfStopping() {
    if (stopping)
      throw new InvalidOperationException("Provider management is stopping.");
  }
}
