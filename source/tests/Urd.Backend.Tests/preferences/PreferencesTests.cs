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
      preferences.SetWorkingDirectory(directory + new string(Path.DirectorySeparatorChar, 3));
      Assert.Equal(Path.GetFullPath(directory), preferences.Read().WorkingDirectory);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    var saved = new PreferenceManager(reopened);
    Assert.Equal(Path.GetFullPath(directory), saved.Read().WorkingDirectory);
    Assert.Throws<ArgumentException>(() => saved.SetWorkingDirectory("relative"));
    Assert.Throws<ArgumentException>(() => saved.SetWorkingDirectory(Path.Combine(directory, "missing")));
    saved.SetWorkingDirectory(null);
    Assert.Null(saved.Read().WorkingDirectory);
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
