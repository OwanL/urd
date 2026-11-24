using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Tools;

public sealed class BrowserCatalogTests {
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task BrowserAvailabilityMatchesConfiguration(bool configured) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(token => Complete(token));
    var manager = new SessionManager(store, _ => client);
    if (configured)
      manager.ConfigureBrowser(Path.GetDirectoryName(database.Path)!);
    try {
      StartingResourceCatalog resources = await manager.ReadStartingResourcesAsync();
      string[] resourceNames = resources.Tools.Select(tool => tool.Name).ToArray();
      Assert.Equal(configured, resourceNames.Contains("browser"));
      Assert.DoesNotContain("playwright", resourceNames);

      PrimarySession session = await manager.CreatePrimaryAsync();
      var selection = TestLlmSelection.Default with { Family = ModelFamily.Google };
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "work", selection));
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(receipt.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
      string[] toolNames = client.Options[0]!.Tools!.Select(tool => tool.Name).ToArray();
      Assert.Equal(configured, toolNames.Contains("browser"));
      Assert.DoesNotContain("playwright", toolNames);
    } finally {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }
}
