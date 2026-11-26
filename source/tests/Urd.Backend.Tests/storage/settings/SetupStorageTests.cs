using Urd.Backend.Tests.Support;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class SetupStorageTests {
  [Fact]
  public async Task CompletionDoesNotBlockStorageDuringProbeAndRetriesAfterDirectoryChanges() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    string existing = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(database.Path)!, "existing")).FullName;
    string missing = Path.Combine(existing, "missing");
    var connection = new ProviderConnection(Guid.NewGuid(), "Synthetic", ProviderRoute.Custom,
        LlmApi.OpenAIResponses, new Uri("https://provider.example.invalid/"), AuthenticationMethod.None);
    ProviderState provider = store.Providers.Add(connection);
    Assert.True(store.Providers.SaveCatalog(connection.Id, provider.Generation,
        new ProviderModelCatalog(connection.Id, DateTimeOffset.UtcNow, [new ProviderModel("model", "Model")])));
    var assignment = new ConfiguredModel(connection.Id, "model");
    store.Preferences.Update(current => current with {
      WorkingDirectory = existing,
      ModelChoices = new() { Small = [assignment], Medium = [assignment], Large = [assignment] }
    });

    var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var resume = new ManualResetEventSlim();
    Task<SetupStatus> completion = Task.Run(() => store.CheckSetup(complete: true, directory => {
      bool exists = Directory.Exists(directory);
      if (directory == existing) {
        probing.TrySetResult();
        if (!resume.Wait(TimeSpan.FromSeconds(20)))
          throw new TimeoutException("Setup's directory probe was not released.");
      }
      return exists;
    }));
    Task concurrentWork = Task.Run(async () => {
      await probing.Task.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.False(store.IsSetupComplete());
      store.Preferences.Update(current => current with { WorkingDirectory = missing });
    });
    try {
      await concurrentWork.WaitAsync(TimeSpan.FromSeconds(10));
    } finally {
      resume.Set();
      await Task.WhenAll(completion, concurrentWork);
    }

    Assert.Equal(new SetupStatus(false, true, true, false), await completion);
    Assert.False(store.IsSetupComplete());
  }
}
