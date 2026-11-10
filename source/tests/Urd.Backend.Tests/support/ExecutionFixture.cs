using Microsoft.Extensions.AI;
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
  private readonly List<Guid> executions = [];

  internal ExecutionFixture(ScriptedChatClient client, IReadOnlyList<AIFunction>? tools = null)
      : this(client, TestAgentTool.Catalog(tools)) { }

  internal ExecutionFixture(ScriptedChatClient client, IReadOnlyList<AgentTool> tools)
      : this(client, new ToolCatalog(tools)) { }

  private ExecutionFixture(ScriptedChatClient client, ToolCatalog tools) {
    this.client = client;
    Database = new TemporaryDatabase();
    store = UrdStore.Open(Database.Path);
    Session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(),
        Path.GetDirectoryName(Database.Path)), store, _ => client, tools);
  }

  internal TemporaryDatabase Database { get; }
  internal PrimarySession Session { get; }

  internal async Task<Guid> StartExecutionAsync(string text) {
    UserMessageReceipt receipt = await Session.SubmitUserMessageAsync(new UserMessageSubmission(Guid.NewGuid(), 0, text, TestLlmSelection.Default));
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
    executions.Add(executionId);
    return executionId;
  }

  public async ValueTask DisposeAsync() {
    // Even a failed assertion must not dispose the database or client underneath owned work.
    foreach (Guid executionId in executions) {
      await Session.RequestStopAsync(executionId);
      await Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout);
    }

    store.Dispose();
    client.Dispose();
    Database.Dispose();
  }
}
