using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Storage;

public sealed class ModelSettingsStorageTests {
  [Fact]
  public void SettingsPersistIndependentlyOfCredentialsAndCatalogUntilConnectionRemoval() {
    using var database = new TemporaryDatabase();
    Guid first = Guid.NewGuid(), second = Guid.NewGuid();
    var original = new ModelSettings(first, "manual-model", ModelFamily.Anthropic, true);
    var updated = original with { Family = ModelFamily.Google, SupportsImages = false };
    var isolated = original with { ConnectionId = second };
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Providers.Add(Connection(first));
      store.Providers.Add(Connection(second));
      Assert.Empty(store.ModelSettings.List(first));
      Assert.Equal(original, store.ModelSettings.Save(original));
      store.ModelSettings.Save(isolated);
      store.ModelSettings.Save(updated);
      var state = store.Providers.Get(first);
      var credentials = new ProviderCredentials("synthetic-key", null, null, null, null, []);
      state = store.Providers.SetCredentials(first, state.Generation, null, credentials);
      Assert.True(store.Providers.SaveCatalog(first, state.Generation, new(first, DateTimeOffset.UtcNow, [])));
      Assert.True(store.Providers.SaveRefresh(first, state.Generation, state.CredentialVersion, credentials));
      store.Providers.BeginDisconnect(first);
      store.Providers.CompleteDisconnect(first, state.Generation);
      Assert.Equal(updated, Assert.Single(store.ModelSettings.List(first)));
      Assert.Equal(isolated, Assert.Single(store.ModelSettings.List(second)));
    }
    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(updated, Assert.Single(reopened.ModelSettings.List(first)));
    reopened.Providers.Remove(first);
    Assert.Throws<KeyNotFoundException>(() => reopened.ModelSettings.List(first));
    Assert.Equal(isolated, Assert.Single(reopened.ModelSettings.List(second)));
    Assert.Equal(1L, database.Scalar("SELECT count(*) FROM model_settings;")); // raw-sql: FK cascade must remove metadata no longer queryable through the deleted connection.
  }

  [Fact]
  public void InvalidSettingsAndUnknownConnectionsDoNotCreateRecords() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid id = Guid.NewGuid();
    store.Providers.Add(Connection(id));
    var valid = new ModelSettings(id, "model", ModelFamily.Other, false);
    ModelSettings[] invalid = [
      valid with { ConnectionId = Guid.Empty }, valid with { ModelId = " " },
      valid with { ModelId = "model\n" }, valid with { ModelId = new string('x', 257) },
      valid with { Family = (ModelFamily)99 }
    ];
    foreach (var settings in invalid)
      Assert.Throws<ArgumentException>(() => store.ModelSettings.Save(settings));
    Assert.Throws<ArgumentException>(() => store.ModelSettings.List(Guid.Empty));
    Assert.Throws<KeyNotFoundException>(() => store.ModelSettings.List(Guid.NewGuid()));
    Assert.Throws<KeyNotFoundException>(() => store.ModelSettings.Save(valid with { ConnectionId = Guid.NewGuid() }));
    Assert.Empty(store.ModelSettings.List(id));
    var longest = valid with { ModelId = new string('x', 256) };
    Assert.Equal(longest, store.ModelSettings.Save(longest));
  }

  [Fact]
  public async Task ManagerMetadataIsOfflineAndUsesReadWriteLifecycleGuards() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = new ControlledProviderHandler((_, _) => throw new InvalidOperationException("Unexpected network access"));
    var manager = new ProviderManager(store, () => new Uri("http://localhost:54321/api/providers/callback"), handler);
    try {
      var connection = await manager.AddOpenAIAsync("Offline");
      Assert.Empty(await manager.GetModelSettingsAsync(connection.Id));
      var settings = new ModelSettings(connection.Id, "uncatalogued-model", ModelFamily.OpenAI, true);
      Assert.Equal(settings, await manager.SaveModelSettingsAsync(settings));
      using var cancelled = new CancellationTokenSource();
      await cancelled.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.GetModelSettingsAsync(connection.Id, cancelled.Token));
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      Assert.Equal(settings, Assert.Single(await manager.GetModelSettingsAsync(connection.Id)));
      await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveModelSettingsAsync(settings));
      manager.Dispose();
      await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.GetModelSettingsAsync(connection.Id));
      Assert.Empty(handler.Requests);
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  private static ProviderConnection Connection(Guid id) => new(id, "Synthetic", ProviderRoute.OpenAI,
    LlmApi.OpenAIResponses, new Uri("https://provider.example.invalid/"), AuthenticationMethod.ApiKey);
}
