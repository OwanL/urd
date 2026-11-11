using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Tools;

public sealed class ToolCompositionTests {
  [Fact]
  public async Task ReopenedChatsKeepTheirSavedFoldersAndToolAvailability() {
    if (!OperatingSystem.IsWindows())
      return;
    using var database = new TemporaryDatabase();
    string first = Path.Combine(Path.GetDirectoryName(database.Path)!, "first");
    string second = Path.Combine(Path.GetDirectoryName(database.Path)!, "second");
    Directory.CreateDirectory(first);
    Directory.CreateDirectory(second);
    Guid sessionId, toolFreeId;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      toolFreeId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      int round = 0;
      using var client = new ScriptedChatClient(token => ++round == 1
          ? ToolBatch(token, new FunctionCallContent("write", "write", new Dictionary<string, object?> {
            ["path"] = "note.txt", ["content"] = "original"
          })) : Complete(token));
      var manager = new SessionManager(store, _ => client, new AgentToolSettings(first, null, new Dictionary<string, string>()));
      try {
        PrimarySession session = await manager.CreatePrimaryAsync();
        sessionId = session.Id;
        await SubmitAsync(session);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(first, "note.txt")));
        Assert.All(client.Options, options => Assert.Equal(new[] { "bash", "cancel_pending_calls", "edit", "list_pending_calls", "read", "write" },
            options!.Tools!.Select(tool => tool.Name).Order(StringComparer.Ordinal)));
      } finally {
        await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      }
    }
    using UrdStore reopened = UrdStore.Open(database.Path);
    int nextRound = 0;
    using var nextClient = new ScriptedChatClient(token => ++nextRound == 1
        ? ToolBatch(token, new FunctionCallContent("edit", "edit", new Dictionary<string, object?> {
          ["path"] = "note.txt", ["edits"] = new[] { new { oldText = "original", newText = "updated" } }
        })) : Complete(token));
    var loadedManager = new SessionManager(reopened, _ => nextClient, new AgentToolSettings(second, null, new Dictionary<string, string>()));
    try {
      PrimarySession loaded = await loadedManager.GetPrimaryAsync(sessionId);
      await SubmitAsync(loaded);
      Assert.Equal("updated", await File.ReadAllTextAsync(Path.Combine(first, "note.txt")));
      Assert.Empty(Directory.EnumerateFileSystemEntries(second));
      PrimarySession toolFree = await loadedManager.GetPrimaryAsync(toolFreeId);
      await SubmitAsync(toolFree);
      Assert.Empty(nextClient.Options[^1]!.Tools!);
      Assert.Null(reopened.Sessions.Read(toolFreeId)!.WorkingDirectory);
    } finally {
      await loadedManager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  private static async Task SubmitAsync(PrimarySession session) {
    var selection = TestLlmSelection.Default with { Family = ModelFamily.Google };
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "work", selection));
    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForSettlementAsync(receipt.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
  }
}
