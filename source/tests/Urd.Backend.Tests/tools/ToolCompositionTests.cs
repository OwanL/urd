using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Preferences;
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
          ? ToolBatch(token, new FunctionCallContent("write", "write_file", new Dictionary<string, object?> {
            ["file_path"] = "note.txt", ["content"] = "original"
          })) : Complete(token));
      var preferences = new PreferenceManager(store);
      preferences.SetWorkingDirectory(first);
      var manager = new SessionManager(store, _ => client,
          new AgentToolSettings(null, new Dictionary<string, string>()), preferences: preferences);
      try {
        PrimarySession session = await manager.CreatePrimaryAsync();
        sessionId = session.Id;
        await SubmitAsync(session);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(first, "note.txt")));
        Assert.All(client.Options, options => Assert.Equal(new[] { "answer_agent", "ask_agent", "ask_user", "bash", "cancel_pending_calls", "list_agents", "list_pending_calls", "read_file", "replace", "web_read", "web_search", "write_file" },
            options!.Tools!.Select(tool => tool.Name).Order(StringComparer.Ordinal)));
      } finally {
        await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      }
    }
    using UrdStore reopened = UrdStore.Open(database.Path);
    int nextRound = 0;
    using var nextClient = new ScriptedChatClient(token => ++nextRound == 1
        ? ToolBatch(token, new FunctionCallContent("edit", "replace", new Dictionary<string, object?> {
          ["file_path"] = "note.txt", ["instruction"] = "Update the note.",
          ["old_string"] = "original", ["new_string"] = "updated"
        })) : Complete(token));
    var reopenedPreferences = new PreferenceManager(reopened);
    reopenedPreferences.SetWorkingDirectory(second);
    var loadedManager = new SessionManager(reopened, _ => nextClient,
        new AgentToolSettings(null, new Dictionary<string, string>()), preferences: reopenedPreferences);
    try {
      PrimarySession loaded = await loadedManager.GetPrimaryAsync(sessionId);
      await SubmitAsync(loaded);
      Assert.Equal("updated", await File.ReadAllTextAsync(Path.Combine(first, "note.txt")));
      Assert.Empty(Directory.EnumerateFileSystemEntries(second));
      PrimarySession toolFree = await loadedManager.GetPrimaryAsync(toolFreeId);
      await SubmitAsync(toolFree);
      Assert.Equal(new[] { "answer_agent", "ask_agent", "ask_user", "cancel_pending_calls", "list_agents", "list_pending_calls", "web_read", "web_search" },
          nextClient.Options[^1]!.Tools!.Select(tool => tool.Name).Order(StringComparer.Ordinal));
      Assert.Null(reopened.Sessions.Read(toolFreeId)!.WorkingDirectory);
    } finally {
      await loadedManager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task WebToolsAreAvailableAndInvocableWithoutASavedFolder() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    int round = 0;
    using var client = new ScriptedChatClient(token => ++round == 1
        ? ToolBatch(token, new FunctionCallContent("web_read", "web_read", new Dictionary<string, object?> {
          ["url"] = "invalid"
        })) : Complete(token));
    var manager = new SessionManager(store, _ => client);
    try {
      PrimarySession session = await manager.CreatePrimaryAsync();
      Assert.Null(store.Sessions.Read(session.Id)!.WorkingDirectory);
      await SubmitAsync(session);
      Assert.Equal(new[] { "answer_agent", "ask_agent", "ask_user", "cancel_pending_calls", "list_agents", "list_pending_calls", "web_read", "web_search" },
          client.Options[0]!.Tools!.Select(tool => tool.Name).Order(StringComparer.Ordinal));
      Assert.Contains(client.Requests.SelectMany(input => input).SelectMany(message => message.Contents),
          content => content is FunctionResultContent result && result.Result?.ToString()?.Contains("web_read failed:") == true);
    } finally {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  private static async Task SubmitAsync(PrimarySession session) {
    var selection = TestLlmSelection.Default with { Family = ModelFamily.Google };
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "work", selection));
    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForExecutionFinishAsync(receipt.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
  }
}
