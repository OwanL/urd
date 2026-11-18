using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Preferences;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class InstructionResourceIntegrationTests {
  [Fact]
  public async Task PickerAndDirectSessionShareWarmedCatalog() {
    using var database = new TemporaryDatabase();
    string directory = Path.Combine(Path.GetDirectoryName(database.Path)!, "project");
    Directory.CreateDirectory(directory);
    string instruction = Path.Combine(directory, "AGENTS.md");
    await File.WriteAllTextAsync(instruction, "Integration instruction marker");
    using UrdStore store = UrdStore.Open(database.Path);
    new PreferenceManager(store).SetWorkingDirectory(directory);
    store.WarmInstructionResources(directory);
    using var client = new ScriptedChatClient(Complete);
    await using var manager = new SessionManager(store, _ => client);
    var picker = await manager.ReadStartingResourcesAsync();
    Assert.Contains(picker.Instructions, item => item.Id == instruction);

    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(), directory), store, _ => client);
    try {
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, "hello", TestLlmSelection.Default));
      await session.WaitForExecutionFinishAsync(receipt.ExecutionId!.Value).WaitAsync(TestTimeout);
      Assert.Contains(client.Requests[0], message => message.Role == ChatRole.System
          && message.Text.Contains("Integration instruction marker", StringComparison.Ordinal));
    } finally {
      await session.StopAcceptingUserMessagesAsync();
      await session.StopExecutionAsync();
    }
  }
}
