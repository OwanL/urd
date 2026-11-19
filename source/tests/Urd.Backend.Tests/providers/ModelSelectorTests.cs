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
