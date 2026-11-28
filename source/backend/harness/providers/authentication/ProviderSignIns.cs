using Urd.Core.Concurrency;
using Urd.Core.Storage;

namespace Urd.Core.Providers;

// Coordinates ephemeral sign-in attempts using the manager's shared admission guard.
internal sealed class ProviderSignIns(
    ProviderStorage storage, ChatGptAuthentication authentication, AsyncLock gate, Func<Uri> callbackUri,
    Func<Guid, Task> cancelRequests, GitHubCopilotAuthentication copilot,
    Func<Guid, CancellationToken, Task<ProviderModelCatalog>> refreshCopilotCatalog) {
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
      if (state.Connection.Route == ProviderRoute.GitHubCopilot && !copilot.IsConfigured)
        throw new ProviderOperationException(ProviderFailure.CopilotNotConfigured);

      if (!attempts.TryGetValue(connectionId, out attempt!) || attempt.Finished.Task.IsCompleted) {
        attempt = new(state);
        attempts[connectionId] = attempt;
        start = true;
      }
    }

    if (start)
      _ = RunAsync(attempt);
    ProviderSignIn ready = await attempt.Ready.Task;
    if (!attempt.IsCopilot)
      return ready;

    using var held = await gate.AcquireAsync();
    return attempt.Snapshot();
  }

  internal async Task<ProviderSignIn> GetAsync(Guid attemptId, CancellationToken cancellationToken) {
    using var held = await gate.AcquireAsync(cancellationToken);
    return Find(attemptId).Snapshot();
  }

  internal async Task<ProviderSignIn> CompleteAsync(string oauthState, string callbackUrl) {
    ProviderSignInAttempt attempt;
    using (await gate.AcquireAsync()) {
      ThrowIfStopping();
      attempt = attempts.Values.SingleOrDefault(candidate =>
          candidate.Flow is not null && string.Equals(candidate.Flow.State, oauthState, StringComparison.Ordinal))
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
      ? CancelAndWaitAsync(attempt) : Task.CompletedTask;

  internal void Forget(Guid connectionId) => attempts.Remove(connectionId);

  internal Task Shutdown() {
    stopping = true;
    return Task.WhenAll(attempts.Values.Select(CancelAndWaitAsync));
  }

  private async Task RunAsync(ProviderSignInAttempt attempt) {
    CancellationToken token = attempt.Cancellation.Token;
    Guid connectionId = attempt.ConnectionId;
    try {
      ProviderAuthentication? result = attempt.IsCopilot
          ? await SignInWithCopilotAsync(attempt, token) : await SignInWithChatGptAsync(attempt, token);
      Task cancelled;
      using (await gate.AcquireAsync()) {
        token.ThrowIfCancellationRequested();
        if (result is null || (attempt.IsCopilot && copilot.IsExpired(attempt.ExpiresAt))) {
          attempt.Status = ProviderSignInStatus.Expired;
          return;
        }
        if (stopping || attempt.Status != (attempt.IsCopilot ? ProviderSignInStatus.Pending : ProviderSignInStatus.Completing))
          throw new OperationCanceledException(token);
        storage.SetCredentials(connectionId, attempt.Generation, result.Registration, result.Credentials);
        attempt.CredentialsSaved = true;
        attempt.Status = attempt.IsCopilot ? ProviderSignInStatus.Completing : ProviderSignInStatus.Completed;
        cancelled = cancelRequests(connectionId);
      }
      await cancelled;
      if (attempt.IsCopilot) {
        token.ThrowIfCancellationRequested();
        await refreshCopilotCatalog(connectionId, token);
        using var held = await gate.AcquireAsync();
        attempt.Status = ProviderSignInStatus.Completed;
      }
    } catch (OperationCanceledException) when (token.IsCancellationRequested || stopping) {
      using var held = await gate.AcquireAsync();
      if (attempt.IsCopilot && attempt.CredentialsSaved)
        attempt.Status = ProviderSignInStatus.Completed;
      else
        attempt.Status = attempt.Cancelled ? ProviderSignInStatus.Cancelled : ProviderSignInStatus.Expired;
    } catch (Exception failure) {
      using var held = await gate.AcquireAsync();
      if (attempt.IsCopilot && attempt.CredentialsSaved) {
        attempt.Status = ProviderSignInStatus.Completed;
        attempt.CatalogRefreshFailed = !token.IsCancellationRequested;
      } else {
        attempt.Status = ProviderSignInStatus.Failed;
        attempt.Error = failure is ProviderOperationException providerFailure
            ? providerFailure.Failure.ToString() : "SignInFailed";
      }
    } finally {
      await FinishAsync(attempt);
    }
  }

  private async Task<ProviderAuthentication?> SignInWithCopilotAsync(ProviderSignInAttempt attempt, CancellationToken token) {
    GitHubCopilotDeviceFlow flow = await copilot.BeginAsync(token);
    using (await gate.AcquireAsync(token)) {
      token.ThrowIfCancellationRequested();
      attempt.ExpiresAt = flow.ExpiresAt;
      attempt.VerificationUrl = flow.VerificationUrl;
      attempt.UserCode = flow.UserCode;
      if (!copilot.IsExpired(flow.ExpiresAt))
        attempt.Ready.TrySetResult(attempt.Snapshot());
    }
    return await copilot.CompleteAsync(flow, attempt.Registration, token);
  }

  private async Task<ProviderAuthentication> SignInWithChatGptAsync(ProviderSignInAttempt attempt, CancellationToken token) {
    string hostId;
    using (await gate.AcquireAsync(token))
      hostId = storage.GetOrCreateHostId();
    ChatGptSignIn flow = await authentication.BeginAsync(hostId, callbackUri(), attempt.Registration, token);
    // Even if cancellation won, hand the flow to the attempt so cleanup will dispose its transport.
    using (await gate.AcquireAsync(CancellationToken.None)) {
      attempt.Flow = flow;
      attempt.ExpiresAt = flow.ExpiresAt;
      attempt.Ready.TrySetResult(attempt.Snapshot());
    }
    string callback = await attempt.Callback.Task.WaitAsync(token);
    return await ChatGptAuthentication.CompleteAsync(flow, callback, token);
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
      if (!(attempt.IsCopilot && attempt.CredentialsSaved)) {
        attempt.Status = ProviderSignInStatus.Failed;
        attempt.Error = "SignInCleanupFailed";
      }
    } finally {
      // A completed attempt is retained only until replacement or connection removal.
      using var held = await gate.AcquireAsync();
      attempt.Flow = null;
      attempt.VerificationUrl = null;
      attempt.UserCode = null;
      attempt.Ready.TrySetResult(attempt.Snapshot());
      attempt.Cancellation.Dispose();
      attempt.Finished.TrySetResult();
    }
  }

  private ProviderSignInAttempt Find(Guid id) => attempts.Values.FirstOrDefault(attempt => attempt.Id == id)
      ?? throw new KeyNotFoundException("The sign-in attempt was not found.");

  private static Task CancelAndWaitAsync(ProviderSignInAttempt attempt) =>
      Task.WhenAll(Cancel(attempt), attempt.Finished.Task);

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
