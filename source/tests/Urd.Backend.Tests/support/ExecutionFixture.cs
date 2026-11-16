using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Support;

// One primary session over a temporary database, with execution cleanup on dispose.
internal sealed class ExecutionFixture : IAsyncDisposable {
  private readonly UrdStore store;
  private readonly ScriptedChatClient client;
  private readonly ProviderManager? providers;

  internal ExecutionFixture(ScriptedChatClient client, IReadOnlyList<AIFunction>? tools = null)
      : this(client, TestAgentTool.CreateToolCatalog(tools), providers: null) { }

  internal ExecutionFixture(ScriptedChatClient client, IReadOnlyList<AgentTool> tools)
      : this(client, new ToolCatalog(tools), providers: null) { }

  // The optional provider factory supplies auxiliary model resolution over saved providers;
  // the fixture owns the manager's shutdown and disposal.
  internal ExecutionFixture(ScriptedChatClient client, IReadOnlyList<AgentTool> tools,
      Func<UrdStore, ProviderManager?>? providers)
      : this(client, new ToolCatalog(tools), providers) { }

  private ExecutionFixture(ScriptedChatClient client, ToolCatalog tools, Func<UrdStore, ProviderManager?>? providers) {
    this.client = client;
    Database = new TemporaryDatabase();
    store = UrdStore.Open(Database.Path);
    this.providers = providers?.Invoke(store);
    Session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(),
        Path.GetDirectoryName(Database.Path)), store, _ => client, tools,
        modelSelector: this.providers is null ? null : new ModelSelector(this.providers));
  }

  internal TemporaryDatabase Database { get; }
  internal PrimarySession Session { get; }
  internal UrdStore Store => store;
  internal bool ExpectFencedExecution { get; init; }

  internal async Task<Guid> StartExecutionAsync(string text) {
    long revision = (await Session.ReadSnapshotAsync()).HistoryRevision;
    UserMessageReceipt receipt = await Session.SubmitUserMessageAsync(new UserMessageSubmission(Guid.NewGuid(), revision, text, TestLlmSelection.Default));
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
    return executionId;
  }

  public async ValueTask DisposeAsync() {
    // Even a failed assertion must not dispose the database or client underneath owned work.
    await Session.StopAcceptingUserMessagesAsync();
    if (ExpectFencedExecution) {
      await Assert.ThrowsAsync<AggregateException>(() => Session.StopExecutionAsync().WaitAsync(TestTimeout));
      Assert.True((await Session.ReadSnapshotAsync()).AdmissionFenced);
    } else {
      await Session.StopExecutionAsync().WaitAsync(TestTimeout);
    }
    if (providers is { } manager) {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }

    store.Dispose();
    client.Dispose();
    Database.Dispose();
  }
}
