using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class ProviderStorageTests {
  private static readonly ProviderRegistration Registration = new("synthetic-client", "synthetic-subject", "test@example.invalid");
  private static readonly DateTimeOffset FetchedAt = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

  [Fact]
  public void ConnectionsHaveIndependentDurableCredentialsAndHostIdentityIsLazy() {
    using var database = new TemporaryDatabase();
    ProviderConnection first = ChatGpt();
    ProviderConnection second = ChatGpt() with { Name = first.Name };
    string hostId;
    StoredProviderRow saved;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Empty(store.Providers.List());
      Assert.Empty(StoredProviders.ReadHostIds(database));
      ProviderState added = store.Providers.Add(first);
      Assert.Equal(0, added.Generation);
      Assert.Equal(0, added.CredentialVersion);
      Assert.Null(added.Credentials);
      store.Providers.Add(second);
      ProviderState state = store.Providers.SetCredentials(first.Id, added.Generation, Registration, Tokens());
      Assert.True(store.Providers.SaveCatalog(first.Id, state.Generation, Catalog(first.Id)));
      Assert.Equal(new[] { first, second }, store.Providers.List());
      Assert.Null(store.Providers.Get(second.Id).Credentials);
      saved = StoredProviders.Read(database)[0];
      Assert.NotNull(saved.Credentials);
      hostId = store.Providers.GetOrCreateHostId();
      Assert.StartsWith("urn:uuid:", hostId);
      Assert.NotEqual(Guid.Empty, Guid.Parse(hostId[9..]));
      Assert.Equal(hostId, store.Providers.GetOrCreateHostId());
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(saved, StoredProviders.Read(database)[0]);
    ProviderState persisted = reopened.Providers.Get(first.Id);
    Assert.Equal(Registration, persisted.Registration);
    Assert.Equal("synthetic-access-original", persisted.Credentials!.AccessToken);
    Assert.Equal(Tokens().Scopes, persisted.Credentials.Scopes);
    Assert.Equal(FetchedAt, persisted.Catalog!.RefreshedAt);
    Assert.Equal(Catalog(first.Id).Models, persisted.Catalog.Models);
    Assert.Equal(hostId, reopened.Providers.GetOrCreateHostId());
    Assert.Equal(hostId, Assert.Single(StoredProviders.ReadHostIds(database)));
  }

  [Fact]
  public void RenameAndDisplayOnlyCustomUpdatePreservePrivateStateAndFence() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    ProviderConnection connection = Custom();
    ProviderState state = store.Providers.Add(connection);
    state = store.Providers.SetCredentials(connection.Id, state.Generation, null, ApiKey());
    store.Providers.SaveCatalog(connection.Id, state.Generation, Catalog(connection.Id));
    store.Providers.BeginDisconnect(connection.Id);
    StoredProviderRow original = Assert.Single(StoredProviders.Read(database));

    ProviderState renamed = store.Providers.Rename(connection.Id, "renamed");
    Assert.Equal("renamed", renamed.Connection.Name);
    Assert.Equal(original with { Connection = original.Connection with { Name = "renamed" } },
        Assert.Single(StoredProviders.Read(database)));
    ProviderState updated = store.Providers.UpdateCustom(connection.Id,
        new("display only", connection.Api, connection.Endpoint, connection.Authentication));
    Assert.True(updated.Disconnecting);
    Assert.Equal(original with { Connection = original.Connection with { Name = "display only" } },
        Assert.Single(StoredProviders.Read(database)));
  }

  [Theory]
  [InlineData("endpoint")]
  [InlineData("api")]
  [InlineData("authentication")]
  public void CustomBindingChangesClearPrivateStateAndInvalidateOldResults(string changed) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    ProviderConnection connection = Custom();
    ProviderState state = store.Providers.Add(connection);
    state = store.Providers.SetCredentials(connection.Id, state.Generation, null, ApiKey());
    store.Providers.SaveCatalog(connection.Id, state.Generation, Catalog(connection.Id));
    store.Providers.BeginDisconnect(connection.Id);
    var settings = new CustomProviderSettings("new binding",
        changed == "api" ? LlmApi.OpenAIChatCompletions : connection.Api,
        changed == "endpoint" ? new Uri("https://other.example.invalid/prefix/") : connection.Endpoint,
        changed == "authentication" ? AuthenticationMethod.None : connection.Authentication);

    ProviderState updated = store.Providers.UpdateCustom(connection.Id, settings);
    Assert.Equal(state.Generation + 1, updated.Generation);
    Assert.Equal(state.CredentialVersion + 1, updated.CredentialVersion);
    Assert.False(updated.Disconnecting);
    Assert.Null(updated.Registration);
    Assert.Null(updated.Credentials);
    Assert.Null(updated.Catalog);
    Assert.Equal(settings.Endpoint, updated.Connection.Endpoint);
    Assert.False(store.Providers.SaveRefresh(connection.Id, state.Generation, state.CredentialVersion, ApiKey()));
    Assert.False(store.Providers.SaveCatalog(connection.Id, state.Generation, Catalog(connection.Id)));
    AssertFailure(ProviderFailure.ConnectionChanged,
        () => store.Providers.SetCredentials(connection.Id, state.Generation, null, ApiKey()));
    StoredProviderRow row = Assert.Single(StoredProviders.Read(database));
    Assert.Null(row.Registration);
    Assert.Null(row.Credentials);
    Assert.Null(row.Catalog);
  }

  [Fact]
  public void UnsupportedCustomUpdatesAndDuplicateIdsLeaveExistingRowsIntact() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    ProviderConnection chatGpt = ChatGpt();
    ProviderConnection custom = Custom();
    store.Providers.Add(chatGpt);
    store.Providers.Add(custom);
    StoredProviderRow[] original = StoredProviders.Read(database).ToArray();
    Assert.Throws<ArgumentException>(() => store.Providers.UpdateCustom(chatGpt.Id,
        new("invalid", custom.Api, custom.Endpoint, custom.Authentication)));
    Assert.Throws<ArgumentException>(() => store.Providers.UpdateCustom(custom.Id,
        new("invalid", custom.Api, custom.Endpoint, AuthenticationMethod.OAuth)));
    Assert.Throws<SqliteException>(() => store.Providers.Add(chatGpt with { Name = "duplicate" }));
    Assert.Equal(original, StoredProviders.Read(database));
  }

  [Fact]
  public void CredentialReplacementFencesOldWritersAndClearsCatalog() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    ProviderState state = SignedIn(store);
    Guid id = state.Connection.Id;
    store.Providers.SaveCatalog(id, state.Generation, Catalog(id));
    ProviderState replacement = store.Providers.SetCredentials(id, state.Generation, Registration, Tokens("replacement"));
    Assert.Equal(state.Generation + 1, replacement.Generation);
    Assert.Equal(state.CredentialVersion + 1, replacement.CredentialVersion);
    Assert.Null(replacement.Catalog);
    Assert.False(store.Providers.SaveRefresh(id, state.Generation, state.CredentialVersion, Tokens("stale")));
    Assert.False(store.Providers.SaveCatalog(id, state.Generation, Catalog(id)));
    AssertFailure(ProviderFailure.ConnectionChanged,
        () => store.Providers.SetCredentials(id, state.Generation, Registration, Tokens("stale")));
    Assert.Equal("synthetic-access-replacement", store.Providers.Get(id).Credentials!.AccessToken);
  }

  [Fact]
  public void RefreshUsesBothVersionsPreservesSameGrantCatalogAndInvalidatesChangedGrant() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    ProviderState state = SignedIn(store);
    Guid id = state.Connection.Id;
    store.Providers.SaveCatalog(id, state.Generation, Catalog(id));
    ProviderCredentials rotated = Tokens("rotated") with { Scopes = Tokens().Scopes.Reverse().ToArray() };
    Assert.True(store.Providers.SaveRefresh(id, state.Generation, state.CredentialVersion, rotated));
    ProviderState refreshed = store.Providers.Get(id);
    Assert.Equal(state.Generation, refreshed.Generation);
    Assert.Equal(state.CredentialVersion + 1, refreshed.CredentialVersion);
    Assert.Equal(FetchedAt, refreshed.Catalog!.RefreshedAt);
    Assert.Equal(Catalog(id).Models, refreshed.Catalog.Models);
    Assert.False(store.Providers.SaveRefresh(id, state.Generation, state.CredentialVersion, Tokens("stale")));

    Assert.True(store.Providers.SaveRefresh(id, refreshed.Generation, refreshed.CredentialVersion,
        rotated with { Scopes = ["openid"] }));
    refreshed = store.Providers.Get(id);
    Assert.Null(refreshed.Catalog);
    store.Providers.SaveCatalog(id, refreshed.Generation, Catalog(id));
    Assert.True(store.Providers.SaveRefresh(id, refreshed.Generation, refreshed.CredentialVersion,
        rotated with { Scopes = ["openid"], RequiresSignIn = true }));
    Assert.Null(store.Providers.Get(id).Catalog);
  }

  [Fact]
  public void DisconnectFenceSurvivesReopenAllowsJoinedRotationButNotNewUseAndRetainsRegistration() {
    using var database = new TemporaryDatabase();
    ProviderState state;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      state = SignedIn(store);
      store.Providers.SaveCatalog(state.Connection.Id, state.Generation, Catalog(state.Connection.Id));
      ProviderState fenced = store.Providers.BeginDisconnect(state.Connection.Id);
      Assert.Equal(state.Generation, fenced.Generation);
      Assert.Equal(state.CredentialVersion, fenced.CredentialVersion);
      StoredProviderRow original = Assert.Single(StoredProviders.Read(database));
      store.Providers.BeginDisconnect(state.Connection.Id);
      Assert.Equal(original, Assert.Single(StoredProviders.Read(database)));
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Guid id = state.Connection.Id;
    Assert.True(reopened.Providers.Get(id).Disconnecting);
    AssertFailure(ProviderFailure.Disconnecting,
        () => reopened.Providers.SetCredentials(id, state.Generation, Registration, Tokens("replacement")));
    Assert.False(reopened.Providers.SaveCatalog(id, state.Generation, Catalog(id)));
    Assert.True(reopened.Providers.SaveRefresh(id, state.Generation, state.CredentialVersion, Tokens("joined")));
    ProviderState rotated = reopened.Providers.Get(id);
    Assert.True(rotated.Disconnecting);
    Assert.Equal("synthetic-refresh-joined", rotated.Credentials!.RefreshToken);
    Assert.Equal(FetchedAt, rotated.Catalog!.RefreshedAt);
    AssertFailure(ProviderFailure.ConnectionChanged,
        () => reopened.Providers.CompleteDisconnect(id, state.Generation - 1));

    ProviderState disconnected = reopened.Providers.CompleteDisconnect(id, state.Generation);
    Assert.Equal(state.Generation + 1, disconnected.Generation);
    Assert.Equal(rotated.CredentialVersion + 1, disconnected.CredentialVersion);
    Assert.Equal(Registration, disconnected.Registration);
    Assert.Null(disconnected.Credentials);
    Assert.Null(disconnected.Catalog);
    Assert.False(disconnected.Disconnecting);
    StoredProviderRow row = Assert.Single(StoredProviders.Read(database));
    Assert.NotNull(row.Registration);
    Assert.Null(row.Credentials);
    Assert.Null(row.Catalog);
    Assert.False(reopened.Providers.SaveRefresh(id, rotated.Generation, rotated.CredentialVersion, Tokens("late")));
    Assert.False(reopened.Providers.SaveCatalog(id, state.Generation, Catalog(id)));
    AssertFailure(ProviderFailure.ConnectionChanged,
        () => reopened.Providers.CompleteDisconnect(id, disconnected.Generation));
    ProviderState signedInAgain = reopened.Providers.SetCredentials(id, disconnected.Generation, Registration, Tokens("again"));
    Assert.Equal("synthetic-access-again", signedInAgain.Credentials!.AccessToken);
  }

  [Fact]
  public void RemovingConnectionsDoesNotLetLateResultsResurrectThem() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    ProviderState state = SignedIn(store);
    Guid id = state.Connection.Id;
    store.Providers.Remove(id);
    Assert.Empty(store.Providers.List());
    Assert.Empty(StoredProviders.Read(database));
    Assert.Throws<KeyNotFoundException>(() => store.Providers.Get(id));
    Assert.Throws<KeyNotFoundException>(() => store.Providers.Remove(id));
    Assert.False(store.Providers.SaveRefresh(id, state.Generation, state.CredentialVersion, Tokens("late")));
    Assert.False(store.Providers.SaveCatalog(id, state.Generation, Catalog(id)));
    AssertFailure(ProviderFailure.ConnectionChanged,
        () => store.Providers.SetCredentials(id, state.Generation, Registration, Tokens("late")));
    AssertFailure(ProviderFailure.ConnectionChanged, () => store.Providers.CompleteDisconnect(id, state.Generation));
  }

  [Theory]
  [InlineData("custom")]
  [InlineData("replace")]
  [InlineData("refresh")]
  [InlineData("begin-disconnect")]
  [InlineData("complete-disconnect")]
  [InlineData("catalog")]
  [InlineData("remove")]
  public void DeferredCommitFailureRollsBackEntireProviderMutation(string operation) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    ProviderState state;
    if (operation == "custom") {
      ProviderConnection connection = Custom();
      state = store.Providers.Add(connection);
      state = store.Providers.SetCredentials(connection.Id, state.Generation, null, ApiKey());
    } else {
      state = SignedIn(store);
    }
    Guid id = state.Connection.Id;
    store.Providers.SaveCatalog(id, state.Generation, Catalog(id));
    if (operation == "complete-disconnect")
      store.Providers.BeginDisconnect(id);
    StoredProviderRow original = Assert.Single(StoredProviders.Read(database));
    string triggerEvent = operation == "remove" ? "DELETE" : "UPDATE";
    database.Execute($"""
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_provider AFTER {triggerEvent} ON provider_connections
      BEGIN INSERT INTO test_child VALUES (1); END;
      """);

    Assert.Throws<SqliteException>(() => Mutate());
    Assert.Equal(original, Assert.Single(StoredProviders.Read(database)));
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM test_child;")); // raw-sql: fixture-owned rollback witness
    database.Execute("DROP TRIGGER fail_provider;");
    Mutate();
    if (operation == "remove")
      Assert.Empty(StoredProviders.Read(database));
    else
      Assert.NotEqual(original, Assert.Single(StoredProviders.Read(database)));

    void Mutate() {
      switch (operation) {
        case "custom":
          store.Providers.UpdateCustom(id, new("changed", LlmApi.OpenAIChatCompletions,
              new Uri("https://other.example.invalid/"), AuthenticationMethod.None));
          break;
        case "replace":
          store.Providers.SetCredentials(id, state.Generation, Registration, Tokens("replacement"));
          break;
        case "refresh":
          Assert.True(store.Providers.SaveRefresh(id, state.Generation, state.CredentialVersion, Tokens("rotated")));
          break;
        case "begin-disconnect":
          store.Providers.BeginDisconnect(id);
          break;
        case "complete-disconnect":
          store.Providers.CompleteDisconnect(id, state.Generation);
          break;
        case "catalog":
          Assert.True(store.Providers.SaveCatalog(id, state.Generation, Catalog(id) with { RefreshedAt = FetchedAt.AddMinutes(1) }));
          break;
        case "remove":
          store.Providers.Remove(id);
          break;
      }
    }
  }

  [Fact]
  public void SharedDatabaseOwnerRefusesProviderUseAfterDisposal() {
    using var database = new TemporaryDatabase();
    UrdStore store = UrdStore.Open(database.Path);
    ProviderStorage providers = store.Providers;
    store.Dispose();
    Assert.Throws<ObjectDisposedException>(() => providers.List());
    Assert.Throws<ObjectDisposedException>(() => providers.GetOrCreateHostId());
    Assert.Throws<ObjectDisposedException>(() => providers.Add(ChatGpt()));
  }

  private static ProviderState SignedIn(UrdStore store) {
    ProviderState state = store.Providers.Add(ChatGpt());
    return store.Providers.SetCredentials(state.Connection.Id, state.Generation, Registration, Tokens());
  }

  private static ProviderConnection ChatGpt() => new(Guid.NewGuid(), "ChatGPT", ProviderRoute.ChatGpt,
      LlmApi.OpenAIResponses, new Uri("https://chatgpt.example.invalid/api/"), AuthenticationMethod.OAuth);

  private static ProviderConnection Custom() => new(Guid.NewGuid(), "Custom", ProviderRoute.Custom,
      LlmApi.OpenAIResponses, new Uri("https://custom.example.invalid/prefix/"), AuthenticationMethod.ApiKey);

  private static ProviderCredentials ApiKey() => new("synthetic-api-key", null, null, null, null, []);

  private static ProviderCredentials Tokens(string suffix = "original") => new(null, $"synthetic-access-{suffix}",
      $"synthetic-refresh-{suffix}", $"synthetic-id-{suffix}", FetchedAt.AddHours(1), ["openid", "model.read", "model.request"]);

  private static ProviderModelCatalog Catalog(Guid id) => new(id, FetchedAt,
      [new ProviderModel("synthetic-model", "Synthetic model")]);

  private static void AssertFailure(ProviderFailure expected, Action operation)
      => Assert.Equal(expected, Assert.Throws<ProviderOperationException>(operation).Failure);
}
