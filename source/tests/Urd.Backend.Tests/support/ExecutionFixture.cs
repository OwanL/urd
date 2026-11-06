using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Support;

// One primary session over a temporary database, with run cleanup on dispose.
internal sealed class ExecutionFixture : IAsyncDisposable {
    private readonly SessionStore _store;
    private readonly ScriptedChatClient _client;
    private readonly List<Guid> _runs = [];

    internal ExecutionFixture(ScriptedChatClient client) {
        _client = client;
        Database = new TemporaryDatabase();
        _store = SessionStore.Open(Database.Path);
        Session = new PrimarySession(_store.Sessions.CreatePrimary(Guid.NewGuid()), _store, client);
    }

    internal TemporaryDatabase Database { get; }
    internal PrimarySession Session { get; }

    internal async Task<Guid> StartRunAsync(string text) {
        InputReceipt receipt = await Session.SubmitUserInputAsync(new InputCommand(Guid.NewGuid(), 0, text));
        Assert.Equal(InputDecision.Accepted, receipt.Decision);
        Guid runId = Assert.IsType<Guid>(receipt.RunId);
        _runs.Add(runId);
        return runId;
    }

    public async ValueTask DisposeAsync() {
        // Even a failed assertion must not dispose the database or client underneath owned work.
        foreach (Guid runId in _runs) {
            await Session.RequestStopAsync(runId);
            await Session.WaitForSettlementAsync(runId).WaitAsync(TestTimeout);
        }

        _store.Dispose();
        _client.Dispose();
        Database.Dispose();
    }
}
