using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.OpenAIStreams;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

// Recovery starts from real validated sign-in; only the synthetic credential's expiry is advanced.
public sealed class ProviderRefreshRecoveryTests {
  [Fact]
  public async Task LostTokenAcknowledgementPersistsSignInFenceAcrossReopenAndRetainsDisconnectToken() {
    using var database = new TemporaryDatabase();
    Guid id;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var identity = new FakeOpenAIIdentity();
      var catalog = Catalog();
      var responses = new ScriptedOpenAIHandler(ResponseStream());
      var manager = Manager(store, identity, catalog, responses);
      try {
        id = (await manager.AddChatGptAsync("Plan")).Id;
        await SignInAsync(manager, identity, id);
        Expire(store, id);
        identity.Fault = "refresh-network";
        await AssertFailureAsync(ProviderFailure.RefreshUncertain, () => manager.RefreshModelCatalogAsync(id));
        Assert.Equal(1, identity.RefreshPosts);
        AssertPersistedRecovery(store, id);
        Assert.True((await manager.GetCredentialStatusAsync(id)).RequiresSignIn);
        await AssertFailureAsync(ProviderFailure.AuthenticationRequired, () => manager.TestModelAsync(id, "fixture-model"));
        Assert.Empty(catalog.Requests);
        Assert.Empty(responses.Requests);
      } finally {
        await CloseAsync(manager);
      }
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    var reopenedIdentity = new FakeOpenAIIdentity();
    var reopenedCatalog = Catalog();
    var reopenedResponses = new ScriptedOpenAIHandler(ResponseStream());
    var reopenedManager = Manager(reopened, reopenedIdentity, reopenedCatalog, reopenedResponses);
    try {
      AssertPersistedRecovery(reopened, id);
      Assert.True((await reopenedManager.GetCredentialStatusAsync(id)).RequiresSignIn);
      await AssertFailureAsync(ProviderFailure.AuthenticationRequired, () => reopenedManager.RefreshModelCatalogAsync(id));
      await AssertFailureAsync(ProviderFailure.AuthenticationRequired, () => reopenedManager.TestModelAsync(id, "fixture-model"));
      Assert.Equal(0, reopenedIdentity.DiscoveryGets);
      Assert.Equal(0, reopenedIdentity.RefreshPosts);
      Assert.Empty(reopenedCatalog.Requests);
      Assert.Empty(reopenedResponses.Requests);
      ProviderDisconnectResult result = await reopenedManager.DisconnectAsync(id).WaitAsync(TestTimeout);
      Assert.True(result.LocalCredentialsCleared);
      Assert.True(result.RemoteRevocationConfirmed);
      Assert.Equal(1, reopenedIdentity.RevocationPosts);
      Assert.Null(reopened.Providers.Get(id).Credentials);
      Assert.NotNull(reopened.Providers.Get(id).Registration);
    } finally {
      await CloseAsync(reopenedManager);
    }
  }

  private static ProviderManager Manager(UrdStore store, FakeOpenAIIdentity identity,
      ScriptedOpenAIHandler catalog, ScriptedOpenAIHandler responses) => new(store, () => FakeOpenAIIdentity.Callback,
          new ChatGptProviderTransport(identity, catalog, responses), FakeOpenAIIdentity.Issuer);

  private static ScriptedOpenAIHandler Catalog() => new("{\"models\":[]}", "application/json");

  private static async Task SignInAsync(ProviderManager manager, FakeOpenAIIdentity identity, Guid id) {
    ProviderSignIn attempt = await manager.BeginSignInAsync(id);
    Assert.NotNull(attempt.AuthorizationUrl);
    string state = FakeOpenAIIdentity.Parse(attempt.AuthorizationUrl.Query)["state"];
    ProviderSignIn result = await manager.CompleteSignInAsync(state, await identity.AuthorizeAsync(attempt.AuthorizationUrl));
    Assert.Equal(ProviderSignInStatus.Completed, result.Status);
  }

  private static void Expire(UrdStore store, Guid id) {
    ProviderState state = store.Providers.Get(id);
    Assert.True(store.Providers.SaveRefresh(id, state.Generation, state.CredentialVersion,
        state.Credentials! with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));
  }

  private static void AssertPersistedRecovery(UrdStore store, Guid id) {
    ProviderState state = store.Providers.Get(id);
    Assert.True(state.Credentials!.RequiresSignIn);
    Assert.Equal("synthetic-refresh-1", state.Credentials.RefreshToken);
    Assert.Equal("synthetic-access-1", state.Credentials.AccessToken);
    Assert.Equal(FakeOpenAIIdentity.ClientId, state.Registration!.ClientId);
    Assert.Equal(FakeOpenAIIdentity.Subject, state.Registration.Subject);
  }

  private static async Task AssertFailureAsync(ProviderFailure expected, Func<Task> operation) {
    var failure = await Assert.ThrowsAsync<ProviderOperationException>(() => operation().WaitAsync(TestTimeout));
    Assert.Equal(expected, failure.Failure);
    Assert.Equal(expected.ToString(), failure.Message);
    Assert.Null(failure.InnerException);
  }

  private static async Task CloseAsync(ProviderManager manager) {
    await manager.ShutdownAsync().WaitAsync(TestTimeout);
    manager.Dispose();
  }
}
