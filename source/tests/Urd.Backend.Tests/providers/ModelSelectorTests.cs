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
      preferences.SetBucketModels(bucket, [new(bucketConnection.Id, "bucket-model")]);
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
      preferences.SetBucketModels(bucket, [new(connection.Id, "bucket-model")]);
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

      preferences.SetBucketModels(ModelBucket.Small, [new(connection.Id, "configured-model")]);
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
      preferences.SetBucketModels(ModelBucket.Small, [new(connection.Id, "catalog-only-model")]);

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

  [Fact]
  public async Task SubagentPoolsLiveExplicitPairsAndOneFallbackPerOtherAllowedProvider() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = new ScriptedOpenAIHandler(CatalogBody(["explicit-a", "explicit-b", "disabled-model",
        "gpt-6-luna", "gpt-6.2-luna", "gpt-6.10-luna", "gpt-90-sol"]), "application/json");
    var manager = CreateManager(store, handler);
    try {
      ProviderConnection assigned = await AddConnectionAsync(manager, "assigned");
      ProviderConnection automatic = await AddConnectionAsync(manager, "automatic");
      ProviderConnection disabled = await AddConnectionAsync(manager, "disabled");
      var first = new ConfiguredModel(assigned.Id, "explicit-a");
      var second = new ConfiguredModel(assigned.Id, "explicit-b");
      var unavailable = new ConfiguredModel(automatic.Id, "missing");
      var blocked = new ConfiguredModel(disabled.Id, "disabled-model");
      var preferences = new PreferenceManager(store);
      preferences.SetBucketModels(ModelBucket.Small, [first, second, unavailable, blocked]);
      var selector = new ModelSelector(manager);
      var fallback = new ConfiguredModel(automatic.Id, "gpt-6.10-luna");
      ConfiguredModel[] expected = [first, second, fallback];
      var seen = new HashSet<ConfiguredModel>();
      for (int attempt = 0; attempt < 96; attempt++) {
        LlmSelection selection = await selector.ResolveSubagentAsync(
            "small", preferences.Read(), [assigned.Id, automatic.Id]).WaitAsync(TestTimeout);
        var chosen = new ConfiguredModel(selection.Connection.Id, selection.ModelId);
        Assert.Contains(chosen, expected);
        Assert.Equal("high", selection.ReasoningEffort);
        seen.Add(chosen);
      }

      Assert.True(seen.SetEquals(expected));
      Assert.Equal(3, handler.Requests.Count);
      Assert.Equal(new[] { first, second, unavailable, blocked }, preferences.Read().ModelChoices.Small);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Theory]
  [InlineData("small", "gpt-6.01-luna")]
  [InlineData("medium", "gpt-6.11-terra")]
  [InlineData("large", "gpt-7-sol")]
  public async Task SubagentDerivesOnlyExactTierModelsUsingNumericDottedVersions(string bucket, string expected) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = CreateManager(store, "gpt-luna", "gpt--luna", "gpt-6-luna", "gpt-6.1-luna", "gpt-6.01-luna", "gpt-8.-luna",
        "gpt-8..1-luna", "gpt-9.1-luna-preview", "gpt-6.2-terra", "gpt-6.11-terra", "gpt-9x-terra",
        "gpt-7-sol", "gpt-6.99.1-sol", "gpt-99-astra", "model-99-sol", "GPT-99-sol", "gpt-99-SOL");
    try {
      ProviderConnection connection = await AddConnectionAsync(manager, "automatic");
      var selector = new ModelSelector(manager);
      var preferences = new UserPreferences(null, null);

      LlmSelection selection = await selector.ResolveSubagentAsync(bucket, preferences, null).WaitAsync(TestTimeout);

      Assert.Equal(connection, selection.Connection);
      Assert.Equal(expected, selection.ModelId);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task SubagentDoesNotCrossTiersOrSelectUnrelatedCatalogModels() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = CreateManager(store, "gpt-6.1-terra", "gpt-6.1-sol", "gpt-6.1-astra", "other-model");
    try {
      await AddConnectionAsync(manager, "not-small");
      var selector = new ModelSelector(manager);
      var preferences = new UserPreferences(null, null);

      await Assert.ThrowsAsync<AuxiliaryModelUnavailableException>(
          () => selector.ResolveSubagentAsync("small", preferences, null));
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task SubagentToolVisibilityUsesSelectedIdsWhileRoutingRequiresLocalCredentials() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = new ScriptedOpenAIHandler(CatalogBody(["gpt-9-luna"]), "application/json");
    var manager = CreateManager(store, handler);
    try {
      var selector = new ModelSelector(manager);
      var preferences = new UserPreferences(null, null);
      Assert.False(await selector.HasSubagentProvidersAsync(null));
      ProviderConnection unconnected = await manager.AddOpenAIAsync("missing key");
      store.Providers.SaveCatalog(unconnected.Id, 0, new(unconnected.Id, DateTimeOffset.UtcNow, [new("gpt-9-luna", "unavailable")]));
      Assert.True(await selector.HasSubagentProvidersAsync(null));
      Assert.True(await selector.HasSubagentProvidersAsync([unconnected.Id]));
      await Assert.ThrowsAsync<AuxiliaryModelUnavailableException>(
          () => selector.ResolveSubagentAsync("small", preferences, null));

      ProviderConnection connected = await manager.AddCustomAsync(new("no key needed", LlmApi.OpenAIResponses,
          new Uri("https://fixture.invalid/v1"), AuthenticationMethod.None));
      Assert.True(await selector.HasSubagentProvidersAsync(null));
      Assert.True(await selector.HasSubagentProvidersAsync([connected.Id]));
      Assert.False(await selector.HasSubagentProvidersAsync([Guid.NewGuid()]));
      Assert.False(await selector.HasSubagentProvidersAsync([]));
      await Assert.ThrowsAsync<AuxiliaryModelUnavailableException>(
          () => selector.ResolveSubagentAsync("small", preferences, []));
      Assert.Empty(handler.Requests);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task AuxiliaryBucketsUseOnlyLiveExplicitPairsAndHonorTheProviderRestriction() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = CreateManager(store, "explicit-a", "explicit-b", "gpt-9-luna");
    try {
      ProviderConnection first = await AddConnectionAsync(manager, "first");
      ProviderConnection second = await AddConnectionAsync(manager, "second");
      var preferences = new PreferenceManager(store);
      preferences.SetBucketModels(ModelBucket.Small, [new(first.Id, "missing"), new(first.Id, "explicit-a"),
          new(second.Id, "explicit-b")]);
      preferences.SetModelChoice(AuxiliaryModelPurpose.Tools, ModelChoice.ForModel(first.Id, "explicit-a"));
      var selector = new ModelSelector(manager);

      LlmSelection allowed = await selector.ResolveAsync(AuxiliaryModelPurpose.Title,
          preferences.Read(), [second.Id], default).WaitAsync(TestTimeout);
      Assert.Equal(second, allowed.Connection);
      Assert.Equal("explicit-b", allowed.ModelId);
      await Assert.ThrowsAsync<AuxiliaryModelUnavailableException>(() => selector.ResolveAsync(
          AuxiliaryModelPurpose.Tools, preferences.Read(), [second.Id], default));
      LlmSelection exact = await selector.ResolveAsync(AuxiliaryModelPurpose.Tools,
          preferences.Read()).WaitAsync(TestTimeout);
      Assert.Equal(first, exact.Connection);
      Assert.Equal("explicit-a", exact.ModelId);

      preferences.SetBucketModels(ModelBucket.Small, [new(second.Id, "missing")]);
      await Assert.ThrowsAsync<AuxiliaryModelUnavailableException>(() => selector.ResolveAsync(
          AuxiliaryModelPurpose.Title, preferences.Read(), [second.Id], default));
    } finally {
      await CloseAsync(manager);
    }
  }

  private static ProviderManager CreateManager(UrdStore store, params string[] modelIds) =>
      CreateManager(store, new ScriptedOpenAIHandler(CatalogBody(modelIds), "application/json"));

  private static ProviderManager CreateManager(UrdStore store, HttpMessageHandler handler) => new(store,
      () => new Uri("http://localhost:54321/api/providers/callback"), handler);

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
