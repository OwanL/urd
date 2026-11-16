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
    var model = new ConfiguredModel(Guid.NewGuid(), "configured-model");
    string skillSource = Path.Combine(Path.GetTempPath(), "skills", "cleaner", "SKILL.md");
    string instructionSource = Path.Combine(Path.GetTempPath(), "AGENTS.md");
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var preferences = new PreferenceManager(store);
      UserPreferences defaults = preferences.Read();
      Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), defaults.ModelChoices.Tools);
      Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), defaults.ModelChoices.Skills);
      Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), defaults.ModelChoices.Instructions);
      Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), defaults.ModelChoices.Title);
      Assert.Equal(new[] { "bash", "read", "write", "edit" }, defaults.ProtectedResources.Tools);

      preferences.SetBucketModel(ModelBucket.Small, model);
      preferences.SetModelChoice(AuxiliaryModelPurpose.Skills, ModelChoice.ForModel(model.ConnectionId, model.ModelId));
      preferences.SetProtectedItems(ProtectedResourceCategory.Tools, ["bash"]);
      preferences.SetProtectedItems(ProtectedResourceCategory.Skills, [skillSource]);
      preferences.SetProtectedItems(ProtectedResourceCategory.Instructions, [instructionSource]);
      Assert.Equal(model, preferences.Read().ModelChoices.Small);
      Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), preferences.Read().ModelChoices.Tools);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    UserPreferences saved = new PreferenceManager(reopened).Read();
    Assert.Equal(ModelChoice.ForBucket(ModelBucket.Small), saved.ModelChoices.Tools);
    Assert.Equal(ModelChoice.ForModel(model.ConnectionId, model.ModelId), saved.ModelChoices.Skills);
    Assert.Null(saved.ModelChoices.Medium);
    Assert.Equal(new[] { "bash" }, saved.ProtectedResources.Tools);
    Assert.Equal(new[] { skillSource }, saved.ProtectedResources.Skills);
    Assert.Equal(new[] { instructionSource }, saved.ProtectedResources.Instructions);
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
