using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

// Discovery does not hydrate handles, replay retained runs or acquire ownership of live work.
public sealed class SessionListingTests {
  [Fact]
  public async Task ListingColdIdentitiesDoesNotCacheHandlesAndShutdownRejectsFurtherListing() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    using var client = new ScriptedChatClient(_ =>
        throw new InvalidOperationException("Discovery must not start model work."));
    Guid first = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid second = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var manager = new SessionManager(store, client);
    SessionIdPage page = await manager.ListPrimaryIdsAsync();
    Assert.Equal(new[] { second, first }, page.SessionIds);
    Assert.Null(page.NextCursor);

    // A cached handle would retain revision zero. Discovery must leave opening to GetPrimary.
    database.ForceHistoryRevision(7);
    PrimarySession opened = await manager.GetPrimaryAsync(first);
    Assert.Equal(7, opened.HistoryRevision);
    Assert.Same(opened, await manager.GetPrimaryAsync(first));
    Assert.Equal(7, (await manager.GetPrimaryAsync(second)).HistoryRevision);
    Assert.Equal(page.SessionIds, (await manager.ListPrimaryIdsAsync()).SessionIds);
    await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ListPrimaryIdsAsync());
    Assert.Equal(2, StoredSessions.Read(database).Count);
    Assert.Empty(StoredRuns.Read(database));
    Assert.Equal(0, client.Calls);
    Assert.Equal(0, client.DisposeCalls);
  }

  [Fact]
  public async Task ListingReopenedOrphansKeepsReceiptsOutputAndUnsettledEvidenceWithoutReplay() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid(), emptyId = Guid.NewGuid(), runId = Guid.NewGuid();
    var command = new InputCommand(Guid.NewGuid(), 0, "retained input");
    InputReceipt accepted;
    LlmRequest request;
    using (SessionStore original = SessionStore.Open(database.Path)) {
      original.Sessions.CreatePrimary(sessionId);
      original.Sessions.CreatePrimary(emptyId);
      accepted = original.Inputs.Admit(sessionId, 0, command, true, runId).Receipt;
      request = Assert.IsType<LlmRequest>(original.Runs.ClaimNextRequest(sessionId, runId, 0));
      Assert.True(original.Output.Append(sessionId, runId, 0,
          new(request.RequestId, Guid.NewGuid(), 0, "retained partial", false)));
    }

    var runs = StoredRuns.Read(database);
    var requests = StoredRuns.ReadRequests(database);
    var deliveries = StoredDeliveries.Read(database);
    var creations = StoredSessions.ReadCreation(database);
    using SessionStore store = SessionStore.Open(database.Path);
    using var client = new ScriptedChatClient(_ =>
        throw new InvalidOperationException("Listing must not replay an orphaned run."));
    var manager = new SessionManager(store, client);
    Assert.Equal(new[] { emptyId, sessionId }, (await manager.ListPrimaryIdsAsync()).SessionIds);
    PrimarySession opened = await manager.GetPrimaryAsync(sessionId);
    SessionSnapshot snapshot = await opened.ReadSnapshotAsync();
    Assert.True(snapshot.AdmissionFenced);
    Assert.Equal(runId, snapshot.UnsettledRunId);
    Assert.False(snapshot.StopRequested);
    Assert.Equal(accepted, await opened.GetInputReceiptAsync(command.InputId));
    await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    Assert.Equal(runs, StoredRuns.Read(database));
    Assert.Equal(requests, StoredRuns.ReadRequests(database));
    Assert.Equal(deliveries, StoredDeliveries.Read(database));
    Assert.Equal(creations, StoredSessions.ReadCreation(database));
    Assert.Equal("retained partial", StoredOutput.ReadText(database));
    Assert.Equal(0, client.Calls);
  }

  [Fact]
  public async Task CancelledListingDoesNotCancelLiveExecutionAndActivityDoesNotReorderChats() {
    using var database = new TemporaryDatabase();
    var waiting = Signal();
    var release = Signal();
    CancellationToken modelToken = default;
    using var client = new ScriptedChatClient(token => {
      modelToken = token;
      return ScriptedResponses.Paused(waiting, release, token);
    });
    using SessionStore store = SessionStore.Open(database.Path);
    var manager = new SessionManager(store, client);
    PrimarySession active = await manager.CreatePrimaryAsync();
    PrimarySession idle = await manager.CreatePrimaryAsync();
    try {
      InputReceipt accepted = await active.SubmitUserInputAsync(new(Guid.NewGuid(), 0, "first"));
      Guid runId = Assert.IsType<Guid>(accepted.RunId);
      await waiting.Task.WaitAsync(TestTimeout);
      using var cancelled = new CancellationTokenSource();
      await cancelled.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(
          () => manager.ListPrimaryIdsAsync(cancellationToken: cancelled.Token));
      Assert.False(modelToken.IsCancellationRequested);
      Assert.False(active.WaitForSettlementAsync(runId).IsCompleted);
      Assert.Equal(new[] { idle.Id, active.Id }, (await manager.ListPrimaryIdsAsync()).SessionIds);
      release.TrySetResult();
      await active.WaitForSettlementAsync(runId).WaitAsync(TestTimeout);
      Assert.Equal(ExecutionOutcome.Completed, (await active.ReadSnapshotAsync()).LastSettlement!.Outcome);
      Assert.Equal(new[] { idle.Id, active.Id }, (await manager.ListPrimaryIdsAsync()).SessionIds);
      Assert.Equal(1, client.Calls);
    } finally {
      release.TrySetResult();
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }
}
