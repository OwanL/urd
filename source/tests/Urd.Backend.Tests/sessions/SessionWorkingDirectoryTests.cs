using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Preferences;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class SessionWorkingDirectoryTests {
  [Fact]
  public async Task ChangesBeforeAcceptanceAndPersistsAcrossReopenWithoutChangingNullDefaults() {
    using var database = new TemporaryDatabase();
    string original = Path.GetTempPath();
    string alternate = Path.Combine(Path.GetDirectoryName(database.Path)!, "alternate");
    Directory.CreateDirectory(alternate);
    UrdStore store = UrdStore.Open(database.Path);
    Guid sessionId = Guid.NewGuid();
    var session = new PrimarySession(store.Sessions.CreatePrimary(sessionId, original), store,
        _ => throw new InvalidOperationException("No request should start."));
    var defaultSession = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store,
        _ => throw new InvalidOperationException("No request should start."));

    SessionSnapshot before = await session.ReadSnapshotAsync();
    Assert.Equal(original, before.WorkingDirectory);
    Assert.True(before.CanChangeWorkingDirectory);
    Assert.False(store.Snapshots.ReadPersisted(sessionId).CanChangeWorkingDirectory);
    SessionSnapshot unchangedDefault = await defaultSession.ReadSnapshotAsync();
    Assert.Null(unchangedDefault.WorkingDirectory);
    Assert.False(unchangedDefault.CanChangeWorkingDirectory);

    string normalized = await session.TryChangeWorkingDirectoryAsync(alternate + Path.DirectorySeparatorChar)
        ?? throw new InvalidOperationException("The idle session should allow a directory change.");
    Assert.Equal(alternate, normalized);
    Assert.Equal(alternate, session.WorkingDirectory);
    Assert.True((await session.ReadSnapshotAsync()).CanChangeWorkingDirectory);
    store.Dispose();

    using UrdStore reopened = UrdStore.Open(database.Path);
    var reopenedSession = new PrimarySession(reopened.Sessions.Read(sessionId)!, reopened,
        _ => throw new InvalidOperationException("No request should start."));
    SessionSnapshot afterReopen = await reopenedSession.ReadSnapshotAsync();
    Assert.Equal(alternate, afterReopen.WorkingDirectory);
    Assert.True(afterReopen.CanChangeWorkingDirectory);
    Assert.False(reopened.Snapshots.ReadPersisted(sessionId).CanChangeWorkingDirectory);
    Assert.Null(reopened.Sessions.Read(defaultSession.Id)!.WorkingDirectory);
  }

  [Fact]
  public async Task OverrideDrivesToolsAndStartingContextWithoutChangingTheDefault() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    string original = Path.GetTempPath();
    string alternate = Path.GetDirectoryName(database.Path)!;
    string? toolDirectory = null;
    var tool = new TestAgentTool("location", (_, context, _) => {
      toolDirectory = context.WorkingDirectory;
      return Task.FromResult(new Urd.Core.Tools.ToolOutput("ok", []));
    });
    int round = 0;
    using var client = new ScriptedChatClient(token => ++round == 1
        ? ToolBatch(token, new FunctionCallContent("call", "location")) : Complete(token));
    var preferences = new PreferenceManager(store);
    preferences.SetWorkingDirectory(original);
    var manager = new SessionManager(store, _ => client, [tool], original);
    try {
      PrimarySession session = await manager.CreatePrimaryAsync();
      Assert.Equal(alternate, await session.TryChangeWorkingDirectoryAsync(alternate));
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, "use this folder", TestLlmSelection.Default));
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForSettlementAsync(receipt.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(alternate, toolDirectory);
      Assert.Contains(client.Requests[0], message => message.Role == ChatRole.System && message.Text.Contains(alternate, StringComparison.Ordinal));
      Assert.Equal(Path.TrimEndingDirectorySeparator(original), preferences.Read().WorkingDirectory);
      Assert.Null(await session.TryChangeWorkingDirectoryAsync(original));
    } finally {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task AcceptedInputLocksDirectoryEvenWhenProviderSetupFailsBeforeVisibleHistory() {
    using var database = new TemporaryDatabase();
    UrdStore store = UrdStore.Open(database.Path);
    string original = Path.GetTempPath();
    string alternate = Path.GetDirectoryName(database.Path)!;
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(), original), store,
        _ => throw new InvalidOperationException("Provider setup failed."));
    try {
      Assert.True((await session.ReadSnapshotAsync()).CanChangeWorkingDirectory);
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, "accepted", TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      Assert.False((await session.ReadSnapshotAsync()).CanChangeWorkingDirectory);

      SessionExecutionResult settled = await session.WaitForSettlementAsync(receipt.ExecutionId!.Value)
          .WaitAsync(TestTimeout);
      Assert.Equal(ExecutionOutcome.Failed, settled.Outcome);
      SessionSnapshot snapshot = await session.ReadSnapshotAsync();
      Assert.Null(snapshot.FirstUserPreview);
      Assert.False(snapshot.CanChangeWorkingDirectory);
      Assert.Null(await session.TryChangeWorkingDirectoryAsync(alternate));
      Assert.Equal(original, snapshot.WorkingDirectory);
    } finally {
      await session.FenceUserMessagesAsync();
      await session.StopExecutionAsync().WaitAsync(TestTimeout);
      store.Dispose();
    }
  }
}
