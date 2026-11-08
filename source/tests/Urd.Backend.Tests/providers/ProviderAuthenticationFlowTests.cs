using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.OpenAIStreams;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

// Composed lifecycle checks reuse validated sign-in rather than inventing stored OAuth credentials.
public sealed class ProviderAuthenticationFlowTests {
  [Fact]
  public async Task RepeatedPendingSignInAndOldCancellationCannotReplaceCredentialsOrCancelANewAttempt() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var identity = new FakeOpenAIIdentity();
    var catalog = new ScriptedOpenAIHandler("{\"models\":[]}", "application/json");
    var responses = new ScriptedOpenAIHandler(ResponseStream());
    var manager = new ProviderManager(store, () => FakeOpenAIIdentity.Callback,
        new ChatGptProviderTransport(identity, catalog, responses), FakeOpenAIIdentity.Issuer);
    try {
      ProviderConnection connection = await manager.AddChatGptAsync("Plan");
      await SignInAsync(manager, identity, connection.Id);
      StoredProviderRow original = Assert.Single(StoredProviders.Read(database));
      Assert.NotNull(original.Credentials);

      ProviderSignIn pending = await manager.BeginSignInAsync(connection.Id);
      Assert.Equal(ProviderSignInStatus.Pending, pending.Status);
      Assert.Equal(pending, await manager.BeginSignInAsync(connection.Id));
      string oldCallback = await identity.AuthorizeAsync(pending.AuthorizationUrl!);
      ProviderSignIn cancelled = await manager.CancelSignInAsync(pending.Id);
      Assert.Equal(ProviderSignInStatus.Cancelled, cancelled.Status);
      Assert.Equal(original, Assert.Single(StoredProviders.Read(database)));
      Assert.True((await manager.GetCredentialStatusAsync(connection.Id)).HasCredentials);

      ProviderSignIn replacement = await manager.BeginSignInAsync(connection.Id);
      Assert.NotEqual(pending.Id, replacement.Id);
      Assert.NotEqual(State(pending), State(replacement));
      await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.CancelSignInAsync(pending.Id));
      var rejected = await Assert.ThrowsAsync<ProviderOperationException>(() =>
          manager.CompleteSignInAsync(State(pending), oldCallback));
      Assert.Equal(ProviderFailure.SignInRejected, rejected.Failure);
      Assert.Equal(replacement, await manager.GetSignInAsync(replacement.Id));
      Assert.Equal(original, Assert.Single(StoredProviders.Read(database)));
      Assert.Equal(1, identity.CodePosts);
      Assert.Empty(catalog.Requests);
      Assert.Empty(responses.Requests);

      ProviderSignIn completed = await manager.CompleteSignInAsync(State(replacement),
          await identity.AuthorizeAsync(replacement.AuthorizationUrl!));
      Assert.Equal(ProviderSignInStatus.Completed, completed.Status);
      Assert.Equal(2, identity.CodePosts);
      ProviderState saved = store.Providers.Get(connection.Id);
      Assert.Equal(FakeOpenAIIdentity.ClientId, saved.Registration!.ClientId);
      Assert.Equal(FakeOpenAIIdentity.Subject, saved.Registration.Subject);
      Assert.True(saved.Generation > original.Generation);
      Assert.Empty(StoredSessions.Read(database));
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  [Fact]
  public async Task DisconnectJoinsSharedRefreshAndRevokesRotatedTokenWithoutAllowingLateUse() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var identity = new FakeOpenAIIdentity { ExpectedRevocationToken = "synthetic-refresh-2" };
    var catalog = new ScriptedOpenAIHandler("{\"models\":[]}", "application/json");
    var responses = new ScriptedOpenAIHandler(ResponseStream());
    var held = new HeldRefresh(new ChatGptProviderTransport(identity, catalog, responses));
    var manager = new ProviderManager(store, () => FakeOpenAIIdentity.Callback, held, FakeOpenAIIdentity.Issuer);
    try {
      ProviderConnection connection = await manager.AddChatGptAsync("Plan");
      await SignInAsync(manager, identity, connection.Id);
      ExpireValidatedCredentials(store, connection.Id);
      identity.Fault = "omitted-scope"; // The successful rotated grant still permits both waiting operations.

      Task<ProviderModelCatalog> discovery = manager.RefreshModelCatalogAsync(connection.Id);
      await held.Entered.Task.WaitAsync(TestTimeout);
      Task<ProviderModelTestResult> diagnostic = manager.TestModelAsync(connection.Id, "fixture-model");
      Assert.False(discovery.IsCompleted);
      Assert.False(diagnostic.IsCompleted);
      Assert.Equal(1, identity.RefreshPosts);
      Task<ProviderDisconnectResult> disconnect = manager.DisconnectAsync(connection.Id);
      Assert.True((await manager.GetCredentialStatusAsync(connection.Id)).IsDisconnecting);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.WaitAsync(TestTimeout));
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diagnostic.WaitAsync(TestTimeout));
      Assert.False(disconnect.IsCompleted);
      Assert.Equal(0, identity.RevocationPosts);
      var late = await Assert.ThrowsAsync<ProviderOperationException>(() => manager.TestModelAsync(connection.Id, "fixture-model"));
      Assert.Equal(ProviderFailure.Disconnecting, late.Failure);
      Assert.Empty(catalog.Requests);
      Assert.Empty(responses.Requests);

      held.Release.TrySetResult();
      ProviderDisconnectResult result = await disconnect.WaitAsync(TestTimeout);
      Assert.True(result.LocalCredentialsCleared);
      Assert.True(result.RemoteRevocationConfirmed);
      Assert.Equal(1, identity.RefreshPosts);
      Assert.Equal(1, identity.RevocationPosts); // Fixture requires synthetic-refresh-2, never the stale token.
      ProviderState saved = store.Providers.Get(connection.Id);
      Assert.Null(saved.Credentials);
      Assert.Null(saved.Catalog);
      Assert.False(saved.Disconnecting);
      Assert.NotNull(saved.Registration);
      Assert.Empty(catalog.Requests);
      Assert.Empty(responses.Requests);
      Assert.Empty(StoredSessions.Read(database));
    } finally {
      held.Release.TrySetResult();
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  [Fact]
  public async Task NarrowedRefreshGrantClearsCatalogAndBlocksBothDiscoveryAndDiagnostics() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var identity = new FakeOpenAIIdentity();
    var catalog = new ScriptedOpenAIHandler("{\"models\":[{\"slug\":\"fixture-model\",\"visibility\":\"list\"}]}", "application/json");
    var responses = new ScriptedOpenAIHandler(ResponseStream());
    var manager = new ProviderManager(store, () => FakeOpenAIIdentity.Callback,
        new ChatGptProviderTransport(identity, catalog, responses), FakeOpenAIIdentity.Issuer);
    try {
      ProviderConnection connection = await manager.AddChatGptAsync("Plan");
      await SignInAsync(manager, identity, connection.Id);
      ProviderModelCatalog previous = await manager.RefreshModelCatalogAsync(connection.Id);
      Assert.NotNull(previous.RefreshedAt);
      Assert.Single(previous.Models);
      Assert.Single(catalog.Requests);
      ExpireValidatedCredentials(store, connection.Id);

      var discovery = await Assert.ThrowsAsync<ProviderOperationException>(() => manager.RefreshModelCatalogAsync(connection.Id));
      Assert.Equal(ProviderFailure.PlanAccessDenied, discovery.Failure);
      var diagnostic = await Assert.ThrowsAsync<ProviderOperationException>(() => manager.TestModelAsync(connection.Id, "fixture-model"));
      Assert.Equal(ProviderFailure.PlanAccessDenied, diagnostic.Failure);
      ProviderCredentialStatus status = await manager.GetCredentialStatusAsync(connection.Id);
      Assert.True(status.HasCredentials);
      Assert.False(status.RequiresSignIn);
      Assert.False(status.PlanAccessGranted);
      ProviderState saved = store.Providers.Get(connection.Id);
      Assert.Equal("synthetic-access-2", saved.Credentials!.AccessToken);
      Assert.Equal("synthetic-refresh-2", saved.Credentials.RefreshToken);
      Assert.Equal(new[] { "openid", "resource.invoke" }, saved.Credentials.Scopes);
      ProviderModelCatalog cleared = await manager.GetModelCatalogAsync(connection.Id);
      Assert.Null(cleared.RefreshedAt);
      Assert.Empty(cleared.Models);
      Assert.Equal(1, identity.RefreshPosts);
      Assert.Single(catalog.Requests); // No new API call follows the narrowed grant.
      Assert.Empty(responses.Requests);
      Assert.Empty(StoredSessions.Read(database));
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  private static async Task SignInAsync(ProviderManager manager, FakeOpenAIIdentity identity, Guid connectionId) {
    ProviderSignIn attempt = await manager.BeginSignInAsync(connectionId);
    ProviderSignIn result = await manager.CompleteSignInAsync(State(attempt), await identity.AuthorizeAsync(attempt.AuthorizationUrl!));
    Assert.Equal(ProviderSignInStatus.Completed, result.Status);
  }

  private static string State(ProviderSignIn attempt) => FakeOpenAIIdentity.Parse(attempt.AuthorizationUrl!.Query)["state"];

  private static void ExpireValidatedCredentials(UrdStore store, Guid id) {
    // Advance only the persisted expiry of credentials obtained through the real validated flow.
    ProviderState state = store.Providers.Get(id);
    Assert.True(store.Providers.SaveRefresh(id, state.Generation, state.CredentialVersion,
        state.Credentials! with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));
  }

  private sealed class HeldRefresh(HttpMessageHandler inner) : DelegatingHandler(inner) {
    internal TaskCompletionSource Entered { get; } = Signal();
    internal TaskCompletionSource Release { get; } = Signal();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      bool refresh = request.RequestUri!.AbsolutePath == "/api/accounts/oauth/token" && request.Content is not null &&
          FakeOpenAIIdentity.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).GetValueOrDefault("grant_type") == "refresh_token";
      HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
      if (!refresh)
        return response;
      Entered.TrySetResult(); // The issuer has rotated; persistence and both waiters still await this response.
      try {
        await Release.Task.WaitAsync(cancellationToken);
        return response;
      } catch {
        response.Dispose();
        throw;
      }
    }
  }
}
