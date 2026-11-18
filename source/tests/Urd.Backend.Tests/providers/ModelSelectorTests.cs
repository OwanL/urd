using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

public sealed class ModelSelectorTests {
  [Theory]
  [InlineData(ModelBucket.Small)]
  [InlineData(ModelBucket.Medium)]
  [InlineData(ModelBucket.Large)]
  public async Task BucketChoiceResolvesOnlyItsExactConfiguredConnectionAndModel(ModelBucket bucket) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = CreateManager(store, "bucket-model", "override-model");
    try {
      ProviderConnection bucketConnection = await AddConnectionAsync(manager, "bucket-provider");
      ProviderConnection overrideConnection = await AddConnectionAsync(manager, "override-provider");
      await manager.SaveModelSettingsAsync(new(bucketConnection.Id, "bucket-model", ModelFamily.Anthropic,
          SupportsImages: true, ContextWindow: 64_000));
      await manager.SaveModelSettingsAsync(new(overrideConnection.Id, "override-model", ModelFamily.Google,
          SupportsImages: false, ContextWindow: 32_000));
      var preferences = new PreferenceManager(store);
      preferences.SetBucketModel(bucket, new(bucketConnection.Id, "bucket-model"));
      preferences.SetModelChoice(AuxiliaryModelPurpose.Tools, ModelChoice.ForBucket(bucket));
      preferences.SetModelChoice(AuxiliaryModelPurpose.Skills,
          ModelChoice.ForModel(overrideConnection.Id, "override-model"));
      var selector = new ModelSelector(manager);

      LlmSelection selection = await selector.ResolveAsync(
          AuxiliaryModelPurpose.Tools, preferences.Read()).WaitAsync(TestTimeout);
      LlmSelection overrideSelection = await selector.ResolveAsync(
          AuxiliaryModelPurpose.Skills, preferences.Read()).WaitAsync(TestTimeout);

      Assert.Equal(bucketConnection, selection.Connection);
      Assert.Equal("bucket-model", selection.ModelId);
      Assert.Equal(ModelFamily.Anthropic, selection.Family);
      Assert.True(selection.SupportsImages);
      Assert.Equal(64_000, selection.ContextWindow);
      Assert.Equal("high", selection.ReasoningEffort);
      Assert.Equal(overrideConnection, overrideSelection.Connection);
      Assert.Equal("override-model", overrideSelection.ModelId);
      Assert.Equal(ModelFamily.Google, overrideSelection.Family);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Theory]
  [InlineData(ModelBucket.Small)]
  [InlineData(ModelBucket.Medium)]
  [InlineData(ModelBucket.Large)]
  public async Task DirectBucketResolutionIgnoresPurposeOverrides(ModelBucket bucket) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = CreateManager(store, "bucket-model", "override-model");
    try {
      ProviderConnection connection = await AddConnectionAsync(manager, "provider");
      var preferences = new PreferenceManager(store);
      preferences.SetBucketModel(bucket, new(connection.Id, "bucket-model"));
      preferences.SetModelChoice(AuxiliaryModelPurpose.Tools, ModelChoice.ForModel(connection.Id, "override-model"));

      LlmSelection selection = await new ModelSelector(manager).ResolveAsync(
          bucket.ToString().ToLowerInvariant(), preferences.Read()).WaitAsync(TestTimeout);

      Assert.Equal(connection, selection.Connection);
      Assert.Equal("bucket-model", selection.ModelId);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task DirectBucketResolutionRejectsMissingAndInvalidBuckets() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = CreateManager(store);
    try {
      var selector = new ModelSelector(manager);
      UserPreferences preferences = new PreferenceManager(store).Read();
      AuxiliaryModelUnavailableException missing = await Assert.ThrowsAsync<AuxiliaryModelUnavailableException>(
          () => selector.ResolveAsync(ModelBucket.Medium, preferences));
      Assert.Contains("medium bucket", missing.Message, StringComparison.OrdinalIgnoreCase);
      await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => selector.ResolveAsync((ModelBucket)999, preferences));
      ArgumentException unknown = await Assert.ThrowsAsync<ArgumentException>(() => selector.ResolveAsync("unknown", preferences));
      Assert.Contains("Unknown model bucket", unknown.Message);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task MissingBucketOrUnavailableSpecificModelFailsWithoutRebinding() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = CreateManager(store, "configured-model");
    try {
      ProviderConnection connection = await AddConnectionAsync(manager, "configured-model");
      await manager.SaveModelSettingsAsync(new(connection.Id, "configured-model", ModelFamily.OpenAI, false));
      var preferences = new PreferenceManager(store);
      var selector = new ModelSelector(manager);

      InvalidOperationException missingBucket = await Assert.ThrowsAsync<AuxiliaryModelUnavailableException>(() =>
          selector.ResolveAsync(AuxiliaryModelPurpose.Tools, preferences.Read()));
      Assert.Contains("small bucket", missingBucket.Message, StringComparison.OrdinalIgnoreCase);

      preferences.SetBucketModel(ModelBucket.Small, new(connection.Id, "configured-model"));
      preferences.SetModelChoice(AuxiliaryModelPurpose.Title, ModelChoice.ForModel(connection.Id, "unavailable-model"));
      AuxiliaryModelUnavailableException unavailableOverride = await Assert.ThrowsAsync<AuxiliaryModelUnavailableException>(() =>
          selector.ResolveAsync(AuxiliaryModelPurpose.Title, preferences.Read()));
      Assert.Contains("configured model is unavailable", unavailableOverride.Message, StringComparison.OrdinalIgnoreCase);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task CatalogModelWithoutSavedSettingsUsesExplicitUnknownCapabilityDefaults() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = CreateManager(store, "catalog-only-model");
    try {
      ProviderConnection connection = await AddConnectionAsync(manager, "catalog-only-model");
      var preferences = new PreferenceManager(store);
      preferences.SetBucketModel(ModelBucket.Small, new(connection.Id, "catalog-only-model"));

      LlmSelection selection = await new ModelSelector(manager).ResolveAsync(
          AuxiliaryModelPurpose.Title, preferences.Read()).WaitAsync(TestTimeout);

      Assert.Equal("catalog-only-model", selection.ModelId);
      Assert.Equal(ModelFamily.Other, selection.Family);
      Assert.False(selection.SupportsImages);
      Assert.Equal(32_000, selection.ContextWindow);
      Assert.Equal("high", selection.ReasoningEffort);
    } finally {
      await CloseAsync(manager);
    }
  }

  private static ProviderManager CreateManager(UrdStore store, params string[] modelIds) => new(store,
      () => new Uri("http://localhost:54321/api/providers/callback"),
      new ScriptedOpenAIHandler(CatalogBody(modelIds), "application/json"));

  private static async Task<ProviderConnection> AddConnectionAsync(ProviderManager manager, string name) {
    ProviderConnection connection = await manager.AddCustomAsync(new(name, LlmApi.OpenAIResponses,
        new Uri("https://fixture.invalid/v1"), AuthenticationMethod.None));
    await manager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
    return connection;
  }

  private static string CatalogBody(IReadOnlyList<string> modelIds) => JsonSerializer.Serialize(new {
    @object = "list",
    data = modelIds.Select(id => new {
      id,
      @object = "model",
      created = 1700000000,
      owned_by = "fixture",
      context_window = 32_000
    })
  });

  private static async Task CloseAsync(ProviderManager manager) {
    await manager.ShutdownAsync().WaitAsync(TestTimeout);
    manager.Dispose();
  }
}
