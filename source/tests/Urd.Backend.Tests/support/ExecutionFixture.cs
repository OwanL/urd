using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Support;

// One primary session over a temporary database, with run cleanup on dispose.
internal sealed class ExecutionFixture : IAsyncDisposable {
  private readonly SessionStore store;
  private readonly ScriptedChatClient client;
  private readonly List<Guid> runs = [];

  internal ExecutionFixture(ScriptedChatClient client) {
    this.client = client;
    Database = new TemporaryDatabase();
    store = SessionStore.Open(Database.Path);
    Session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, client);
  }

  internal TemporaryDatabase Database { get; }
  internal PrimarySession Session { get; }

  internal async Task<Guid> StartRunAsync(string text) {
    InputReceipt receipt = await Session.SubmitUserInputAsync(new InputCommand(Guid.NewGuid(), 0, text));
    Assert.Equal(InputDecision.Accepted, receipt.Decision);
    Guid runId = Assert.IsType<Guid>(receipt.RunId);
    runs.Add(runId);
    return runId;
  }

  public async ValueTask DisposeAsync() {
    // Even a failed assertion must not dispose the database or client underneath owned work.
    foreach (Guid runId in runs) {
      await Session.RequestStopAsync(runId);
      await Session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout);
    }

    store.Dispose();
    client.Dispose();
    Database.Dispose();
  }
}
