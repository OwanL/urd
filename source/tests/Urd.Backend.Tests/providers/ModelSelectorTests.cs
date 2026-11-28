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
      preferences.SetBucketModels(bucket, [new(bucketConnection.Id, "bucket-model", "extraHigh")]);
      Assert.Equal("extraHigh", (await selector.ResolveAsync(bucket, preferences.Read()).WaitAsync(TestTimeout)).ReasoningEffort);
      Assert.Equal("high", overrideSelection.ReasoningEffort);
      Assert.Equal(overrideConnection, overrideSelection.Connection);
      Assert.Equal("override-model", overrideSelection.ModelId);
      Assert.Equal(ModelFamily.Google, overrideSelection.Family);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task InferredFamilyAppliesUntilAnExplicitOverrideIsSaved() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    const string model = "claude-sonnet-4.5";
    var manager = CreateManager(store, model);
    try {
      ProviderConnection connection = await AddConnectionAsync(manager, "provider");
      var preferences = new PreferenceManager(store);
      preferences.SetModelChoice(AuxiliaryModelPurpose.Tools, ModelChoice.ForModel(connection.Id, model));
      var selector = new ModelSelector(manager);

      LlmSelection inferred = await selector.ResolveAsync(AuxiliaryModelPurpose.Tools, preferences.Read());
      Assert.Equal(ModelFamily.Anthropic, inferred.Family);

      await manager.SaveModelSettingsAsync(new(connection.Id, model, ModelFamily.Other,
          SupportsImages: false, FamilyExplicit: false));
      LlmSelection learnedCapability = await selector.ResolveAsync(AuxiliaryModelPurpose.Tools, preferences.Read());
      Assert.Equal(ModelFamily.Anthropic, learnedCapability.Family);
      Assert.False(learnedCapability.SupportsImages);

      await manager.SaveModelSettingsAsync(new(connection.Id, model, ModelFamily.Other,
          SupportsImages: false, FamilyExplicit: true));
      Assert.Equal(ModelFamily.Other,
          (await selector.ResolveAsync(AuxiliaryModelPurpose.Tools, preferences.Read())).Family);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Theory]
  [InlineData("small", ModelBucket.Small)]
  [InlineData("medium", ModelBucket.Medium)]
  [InlineData("large", ModelBucket.Large)]
  public async Task SubagentUsesOnlyAvailableAssignmentsInItsBucketAndProviderScope(string bucket, ModelBucket tier) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = CreateManager(store, "assigned", "gpt-99-luna", "gpt-99-terra", "gpt-99-sol");
    try {
      ProviderConnection allowed = await AddConnectionAsync(manager, "allowed");
      ProviderConnection excluded = await AddConnectionAsync(manager, "excluded");
      var selector = new ModelSelector(manager);
      var preferences = new PreferenceManager(store);
      preferences.SetBucketModels(tier == ModelBucket.Small ? ModelBucket.Medium : ModelBucket.Small,
          [new(allowed.Id, "assigned")]);

      var empty = await Assert.ThrowsAsync<AuxiliaryModelUnavailableException>(() =>
          selector.ResolveSubagentAsync(bucket, preferences.Read(), [allowed.Id]).WaitAsync(TestTimeout));
      Assert.Contains("Settings → Model buckets", empty.Message);

      preferences.SetBucketModels(tier, [new(allowed.Id, "stale"), new(excluded.Id, "assigned")]);
      var stale = await Assert.ThrowsAsync<AuxiliaryModelUnavailableException>(() =>
          selector.ResolveSubagentAsync(bucket, preferences.Read(), [allowed.Id]).WaitAsync(TestTimeout));
      Assert.Contains("Settings → Model buckets", stale.Message);

      preferences.SetBucketModels(tier, [new(allowed.Id, "stale"), new(allowed.Id, "assigned", "low"), new(excluded.Id, "assigned")]);
      LlmSelection selection = await selector.ResolveSubagentAsync(bucket, preferences.Read(), [allowed.Id]).WaitAsync(TestTimeout);
      Assert.Equal(allowed, selection.Connection);
      Assert.Equal("assigned", selection.ModelId);
      Assert.Equal("low", selection.ReasoningEffort);
      Assert.True(selection.SupportsImages);

      await Assert.ThrowsAsync<AuxiliaryModelUnavailableException>(() =>
          selector.ResolveSubagentAsync(bucket, preferences.Read(), []).WaitAsync(TestTimeout));
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task CopilotSelectionsFreezeCatalogProtocolForAuxiliaryAndSubagentRuns() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var manager = new ProviderManager(store, () => new Uri("http://localhost/auth/callback"), copilotClientId: "fixture");
    try {
      ProviderConnection connection = await manager.AddGitHubCopilotAsync("GitHub Copilot");
      ProviderState state = store.Providers.SetCredentials(connection.Id, 0, new("fixture", "account", null),
          new(null, "synthetic-copilot-access", "synthetic-github-access", null, DateTimeOffset.UtcNow.AddHours(1), []));
      Assert.True(store.Providers.SaveCatalog(connection.Id, state.Generation, new(connection.Id, DateTimeOffset.UtcNow, [
        new("null-api", "Missing protocol"),
        new("chat-model", "Chat", Api: LlmApi.OpenAIChatCompletions),
        new("messages-model", "Messages", Api: LlmApi.AnthropicMessages)
      ])));
      var preferences = new PreferenceManager(store);
      preferences.SetBucketModels(ModelBucket.Small, [new(connection.Id, "null-api"), new(connection.Id, "chat-model")]);
      preferences.SetModelChoice(AuxiliaryModelPurpose.Skills, ModelChoice.ForModel(connection.Id, "messages-model"));
      var selector = new ModelSelector(manager);
      LlmSelection configured = await selector.ResolveAsync(AuxiliaryModelPurpose.Skills, preferences.Read());
      LlmSelection bucket = await selector.ResolveAsync(ModelBucket.Small, preferences.Read());
      LlmSelection subagent = await selector.ResolveSubagentAsync("small", preferences.Read(), [connection.Id]);

      Assert.Equal(LlmApi.AnthropicMessages, configured.ModelApi);
      Assert.Equal("chat-model", bucket.ModelId);
      Assert.Equal(LlmApi.OpenAIChatCompletions, bucket.ModelApi);
      Assert.Equal(bucket, subagent);
      Assert.All(new[] { configured, bucket, subagent }, selection => Assert.Equal(connection, selection.Connection));
      Assert.Equal(LlmApi.OpenAIResponses, connection.Api);
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
