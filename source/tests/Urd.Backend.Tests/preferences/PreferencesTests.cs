using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Preferences;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Preferences;

public sealed class PreferencesTests {
  [Fact]
  public void SubagentDepthDefaultsValidatesAndPersists() {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      Assert.Equal(UserPreferences.DefaultMaxSubagentDepth, preferences.Read().MaxSubagentDepth);
      preferences.SetMaxSubagentDepth(0);
      Assert.Throws<ArgumentOutOfRangeException>(() => preferences.SetMaxSubagentDepth(-1));
      Assert.Equal(0, preferences.Read().MaxSubagentDepth);
    }
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      Assert.Equal(0, preferences.Read().MaxSubagentDepth);
      preferences.SetMaxSubagentDepth(int.MaxValue);
      preferences.SetLanHostingEnabled(true);
    }
    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(int.MaxValue, new PreferenceManager(reopened).Read().MaxSubagentDepth);
    Assert.True(new PreferenceManager(reopened).Read().LanHostingEnabled);
  }

  [Fact]
  public void LanHostingPreferenceDefaultsToFalseAndPersistsIndependently() {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      Assert.False(preferences.Read().LanHostingEnabled);
      preferences.SetCompactionTriggerTokenCount(4096);
      preferences.SetLanHostingEnabled(true);
      Assert.True(preferences.Read().LanHostingEnabled);
    }

    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      Assert.True(preferences.Read().LanHostingEnabled);
      Assert.Equal(4096, preferences.Read().CompactionTriggerTokenCount);
      preferences.SetCompactionTriggerTokenCount(8192);
      Assert.True(preferences.Read().LanHostingEnabled);
      preferences.SetLanHostingEnabled(false);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    var saved = new PreferenceManager(reopened);
    Assert.False(saved.Read().LanHostingEnabled);
    Assert.Equal(8192, saved.Read().CompactionTriggerTokenCount);
  }

  [Fact]
  public void WorkingDirectoryIsValidatedNormalizedAndPersisted() {
    using var database = new TemporaryDatabase();
    string directory = Path.Combine(Path.GetDirectoryName(database.Path)!, "working-directory");
    Directory.CreateDirectory(directory);
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      string? fallback = Path.GetPathRoot(Environment.CurrentDirectory);
      Assert.Equal(fallback, preferences.Read().WorkingDirectory);
      preferences.SetWorkingDirectory(directory + new string(Path.DirectorySeparatorChar, 3));
      Assert.Equal(Path.GetFullPath(directory), preferences.Read().WorkingDirectory);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    var saved = new PreferenceManager(reopened);
    Assert.Equal(Path.GetFullPath(directory), saved.Read().WorkingDirectory);
    Assert.Throws<ArgumentException>(() => saved.SetWorkingDirectory("relative"));
    Assert.Throws<ArgumentException>(() => saved.SetWorkingDirectory(Path.Combine(directory, "missing")));
    saved.SetWorkingDirectory(null);
    Assert.Equal(Path.GetPathRoot(Environment.CurrentDirectory), saved.Read().WorkingDirectory);
  }

  [Fact]
  public void CompactionTriggerPreferencePersistsAndCanBeReset() {
    using var database = new TemporaryDatabase();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      Assert.Null(preferences.Read().CompactionTriggerTokenCount);
      preferences.SetCompactionTriggerTokenCount(int.MaxValue);
      Assert.Equal(int.MaxValue, preferences.Read().CompactionTriggerTokenCount);
    }

    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      Assert.Equal(int.MaxValue, preferences.Read().CompactionTriggerTokenCount);
      preferences.SetCompactionTriggerTokenCount(null);
      Assert.Null(preferences.Read().CompactionTriggerTokenCount);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Null(new PreferenceManager(reopened).Read().CompactionTriggerTokenCount);
  }

  [Fact]
  public void AuxiliaryModelChoicesBucketMappingsAndProtectionsPersistIndependently() {
    using var database = new TemporaryDatabase();
    var model = new ConfiguredModel(Guid.NewGuid(), "configured-model", "medium");
    var another = new ConfiguredModel(Guid.NewGuid(), "another-model");
    string skillSource = Path.Combine(Path.GetTempPath(), "skills", "cleaner", "SKILL.md");
    string instructionSource = Path.Combine(Path.GetTempPath(), "AGENTS.md");
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      UserPreferences defaults = preferences.Read();
      Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), defaults.ModelChoices.Tools);
      Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), defaults.ModelChoices.Skills);
      Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), defaults.ModelChoices.Instructions);
      Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), defaults.ModelChoices.Title);
      Assert.Equal(new[] {
        "bash", "read", "write", "edit", "web_search", "web_read", "list_pending_calls", "cancel_pending_calls",
        "subagent", "list_agents", "ask_agent", "answer_agent", "ask_user"
      }, defaults.ProtectedResources.Tools);

      preferences.SetBucketModels(ModelBucket.Small, [model, another]);
      preferences.SetBucketModels(ModelBucket.Medium, [model with { ReasoningEffort = "low" }]);
      preferences.SetModelChoice(AuxiliaryModelPurpose.Skills, ModelChoice.ForModel(model.ConnectionId, model.ModelId));
      preferences.SetProtectedItems(ProtectedResourceCategory.Tools, ["bash"]);
      preferences.SetProtectedItems(ProtectedResourceCategory.Skills, [skillSource]);
      preferences.SetProtectedItems(ProtectedResourceCategory.Instructions, [instructionSource]);
      Assert.Equal(new[] { model, another }, preferences.Read().ModelChoices.Small);
      Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), preferences.Read().ModelChoices.Tools);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    UserPreferences saved = new PreferenceManager(reopened).Read();
    Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), saved.ModelChoices.Tools);
    Assert.Equal(ModelChoice.ForModel(model.ConnectionId, model.ModelId), saved.ModelChoices.Skills);
    Assert.Equal(new[] { model, another }, saved.ModelChoices.Small);
    Assert.Equal(new[] { model with { ReasoningEffort = "low" } }, saved.ModelChoices.Medium);
    Assert.Equal(new[] { "bash" }, saved.ProtectedResources.Tools);
    Assert.Equal(new[] { skillSource }, saved.ProtectedResources.Skills);
    Assert.Equal(new[] { instructionSource }, saved.ProtectedResources.Instructions);
  }

  [Fact]
  public void LegacyBucketEntriesWithoutReasoningEffortLoad() {
    using var database = new TemporaryDatabase();
    Guid connectionId = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path))
      new PreferenceManager(store).SetLanHostingEnabled(true);
    database.Execute($$"""
      UPDATE preferences SET model_choices_json = '{"Small":[{"ConnectionId":"{{connectionId}}","ModelId":"legacy"}]}'
      WHERE singleton = 1;
      """);
    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Equal(new ConfiguredModel(connectionId, "legacy"),
        Assert.Single(new PreferenceManager(reopened).Read().ModelChoices.Small));
  }

  [Fact]
  public void ModelAndProtectionPreferencesRejectInvalidChoicesWithoutChangingSavedValues() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var preferences = new PreferenceManager(store);
    preferences.SetModelChoice(AuxiliaryModelPurpose.Tools, ModelChoice.ForBucket(ModelBucket.Small));
    preferences.SetProtectedItems(ProtectedResourceCategory.Tools, ["bash"]);

    Assert.Throws<ArgumentException>(() => preferences.SetModelChoice(AuxiliaryModelPurpose.Tools,
        new ModelChoice(ModelBucket.Small, Guid.NewGuid(), "mixed-choice")));
    Assert.Throws<ArgumentException>(() => preferences.SetModelChoice(AuxiliaryModelPurpose.Title,
        ModelChoice.ForModel(Guid.Empty, "model")));
    Assert.Throws<ArgumentException>(() => preferences.SetProtectedItems(ProtectedResourceCategory.Skills, ["", " "]));
    Assert.Throws<ArgumentException>(() => preferences.SetProtectedItems(ProtectedResourceCategory.Tools, ["bash", "bash"]));

    Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), preferences.Read().ModelChoices.Tools);
    Assert.Equal(new[] { "bash" }, preferences.Read().ProtectedResources.Tools);
    Assert.Empty(preferences.Read().ProtectedResources.Skills);
  }

  [Fact]
  public void BucketReplacementRejectsInvalidOrDuplicatePairsAndCopiesLists() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var preferences = new PreferenceManager(store);
    var model = new ConfiguredModel(Guid.NewGuid(), "model");
    var alternate = new ConfiguredModel(model.ConnectionId, "other-model");
    ConfiguredModel[] input = [model, alternate];
    preferences.SetBucketModels(ModelBucket.Small, input);

    input[0] = alternate;
    Assert.Throws<ArgumentException>(() => preferences.SetBucketModels(ModelBucket.Small, [model, model with { ReasoningEffort = "low" }]));
    Assert.Throws<ArgumentException>(() => preferences.SetBucketModels(ModelBucket.Small, [model with { ReasoningEffort = "invalid" }]));
    Assert.Throws<ArgumentException>(() => preferences.SetBucketModels(ModelBucket.Small, [new(Guid.Empty, "model")]));
    Assert.Throws<ArgumentException>(() => preferences.SetBucketModels(ModelBucket.Small, [new(model.ConnectionId, " ")]));
    Assert.Throws<ArgumentNullException>(() => preferences.SetBucketModels(ModelBucket.Small, [null!]));
    Assert.Equal(new[] { model, alternate }, preferences.Read().ModelChoices.Small);

    preferences.SetBucketModels(ModelBucket.Small, []);
    Assert.Empty(preferences.Read().ModelChoices.Small);
  }

  [Fact]
  public void SubagentProviderPreferencePersistsNullEmptyAndSelectedListsSeparately() {
    using var database = new TemporaryDatabase();
    Guid first = Guid.NewGuid(), second = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      Assert.Null(preferences.Read().SubagentProviderConnectionIds);
      Guid[] input = [first, second];
      preferences.SetSubagentProviders(input);
      input[0] = second;
      Assert.Throws<ArgumentException>(() => preferences.SetSubagentProviders([Guid.Empty]));
      Assert.Throws<ArgumentException>(() => preferences.SetSubagentProviders([first, first]));
      Assert.Equal(new[] { first, second }, preferences.Read().SubagentProviderConnectionIds);
    }

    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      Assert.Equal(new[] { first, second }, preferences.Read().SubagentProviderConnectionIds);
      preferences.SetSubagentProviders([]);
    }

    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      Assert.Empty(preferences.Read().SubagentProviderConnectionIds!);
      preferences.SetSubagentProviders(null);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Null(new PreferenceManager(reopened).Read().SubagentProviderConnectionIds);
  }

  [Fact]
  public void PreferenceManagersShareCommittedCacheAndFailedWritesDoNotPublish() {
    using var database = new TemporaryDatabase();
    string directory = Path.Combine(Path.GetDirectoryName(database.Path)!, "shared-preference-directory");
    Directory.CreateDirectory(directory);
    using UrdStore store = UrdStore.Open(database.Path);
    var first = new PreferenceManager(store);
    var second = new PreferenceManager(store);

    Assert.Null(first.Read().CompactionTriggerTokenCount);
    first.SetWorkingDirectory(directory);
    Assert.Equal(directory, second.Read().WorkingDirectory);
    second.SetCompactionTriggerTokenCount(4096);
    Assert.Equal(4096, first.Read().CompactionTriggerTokenCount);
    first.SetWorkingDirectory(directory);
    Assert.Equal(4096, second.Read().CompactionTriggerTokenCount);

    Assert.Throws<ArgumentOutOfRangeException>(() => first.SetCompactionTriggerTokenCount(0));
    Assert.Throws<ArgumentOutOfRangeException>(() => first.SetCompactionTriggerTokenCount(-1));
    Assert.Equal(4096, second.Read().CompactionTriggerTokenCount);

    database.Execute("""
      CREATE TRIGGER fail_preferences BEFORE UPDATE ON preferences
      BEGIN SELECT RAISE(ABORT, 'Injected preference write failure'); END;
      """);
    Assert.Throws<SqliteException>(() => first.SetCompactionTriggerTokenCount(8192));
    Assert.Equal(4096, second.Read().CompactionTriggerTokenCount);
    Assert.Equal(directory, second.Read().WorkingDirectory);
    Assert.Equal(4096L, database.Scalar("SELECT compaction_trigger_token_count FROM preferences WHERE singleton = 1;")); // raw-sql: failed write must not persist
  }

  [Fact]
  public async Task NewSessionsUsePreferenceThenFallbackWithoutChangingExistingSessions() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    string parent = Path.GetDirectoryName(database.Path)!;
    string firstDirectory = Path.Combine(parent, "first");
    string secondDirectory = Path.Combine(parent, "second");
    string? fallback = Path.GetPathRoot(Environment.CurrentDirectory);
    Directory.CreateDirectory(firstDirectory);
    Directory.CreateDirectory(secondDirectory);
    var preferences = new PreferenceManager(store);
    preferences.SetWorkingDirectory(firstDirectory);
    using var client = new FakeChatClient();
    var manager = new SessionManager(store, _ => client,
        new AgentToolSettings(null, new Dictionary<string, string>()), preferences);

    try {
      PrimarySession first = await manager.CreatePrimaryAsync();
      preferences.SetWorkingDirectory(secondDirectory);
      PrimarySession second = await manager.CreatePrimaryAsync();
      preferences.SetWorkingDirectory(null);
      PrimarySession defaulted = await manager.CreatePrimaryAsync();

      Assert.Equal(firstDirectory, first.WorkingDirectory);
      Assert.Equal(secondDirectory, second.WorkingDirectory);
      Assert.Equal(fallback, defaulted.WorkingDirectory);
      Assert.Equal(firstDirectory, (await manager.GetPrimaryAsync(first.Id)).WorkingDirectory);
      Assert.Equal(firstDirectory, store.Sessions.Read(first.Id)!.WorkingDirectory);
    } finally {
      await manager.ShutdownSessionsAsync();
    }
  }
}
