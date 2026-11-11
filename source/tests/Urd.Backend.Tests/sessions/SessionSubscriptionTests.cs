using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class SessionSubscriptionTests {
  [Fact]
  public async Task ThrowingObserverDoesNotInterruptOtherObserversOrSettlement() {
    using var client = new ScriptedChatClient(Complete);
    await using var fixture = new ExecutionFixture(client);
    var healthyChanges = new ConcurrentQueue<SessionChange>();
    using IDisposable throwingSubscription = await fixture.Session.SubscribeAsync(
        _ => throw new InvalidOperationException("Subscriber failed."), watched: true);
    using IDisposable healthySubscription = await fixture.Session.SubscribeAsync(healthyChanges.Enqueue, watched: false);

    Guid executionId = await fixture.StartExecutionAsync("hello");
    SessionExecutionResult result = await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout);

    Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
    Assert.Contains(healthyChanges, change => change.Status == "running");
    Assert.Equal("idle", healthyChanges.Last().Status);
  }

  [Fact]
  public async Task DisposalIsIdempotentAndUnobservedChangesAdvanceVersion() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(_ => throw new InvalidOperationException("No request expected."));
    var manager = new SessionManager(store, _ => client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    var changes = new ConcurrentQueue<SessionChange>();
    IDisposable subscription = await session.SubscribeAsync(changes.Enqueue, watched: false);
    int initialCount = changes.Count;

    subscription.Dispose();
    subscription.Dispose();
    await session.FenceUserMessagesAsync();
    Assert.Equal(initialCount, changes.Count);

    using IDisposable laterSubscription = await session.SubscribeAsync(changes.Enqueue, watched: false);
    Assert.Equal(new SessionChange(session.Id, 1, "fenced"), changes.Last());
  }

  [Fact]
  public async Task RegistrationDeliversStatusAndWatchedChangeAtOneVersion() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(_ => throw new InvalidOperationException("No request expected."));
    var manager = new SessionManager(store, _ => client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    var changes = new ConcurrentQueue<SessionChange>();

    using IDisposable subscription = await session.SubscribeAsync(changes.Enqueue, watched: true);

    SessionChange[] initial = changes.ToArray();
    Assert.Equal(2, initial.Length);
    Assert.All(initial, change => Assert.Equal(session.Id, change.Session));
    Assert.Equal(new SessionChange(session.Id, 0, "idle"), initial[0]);
    Assert.Equal(new SessionChange(session.Id, 0), initial[1]);

    await session.FenceUserMessagesAsync();
    SessionChange[] fenced = changes.ToArray();
    Assert.Equal(new SessionChange(session.Id, 1, "fenced"), fenced[^2]);
    Assert.Equal(new SessionChange(session.Id, 1), fenced[^1]);
  }

  [Fact]
  public async Task UnwatchedObserverReceivesOnlyInitialAndChangedStatuses() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(_ => throw new InvalidOperationException("No request expected."));
    var manager = new SessionManager(store, _ => client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    var changes = new ConcurrentQueue<SessionChange>();

    using IDisposable subscription = await session.SubscribeAsync(changes.Enqueue, watched: false);
    await session.FenceUserMessagesAsync();

    Assert.Equal(new[] {
      new SessionChange(session.Id, 0, "idle"),
      new SessionChange(session.Id, 1, "fenced")
    }, changes.ToArray());
  }

  [Fact]
  public async Task AcceptedOutputAndSettlementEachAdvanceTheWatchedVersion() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(ScriptedResponses.Complete);
    var manager = new SessionManager(store, _ => client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    var changes = new ConcurrentQueue<SessionChange>();
    using IDisposable subscription = await session.SubscribeAsync(changes.Enqueue, watched: true);

    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
        new(Guid.NewGuid(), 0, "hello", TestLlmSelection.Default));
    await session.WaitForSettlementAsync(receipt.ExecutionId!.Value).WaitAsync(TestTimeout);

    SessionChange[] notifications = changes.ToArray();
    SessionChange[] statuses = notifications.Where(change => change.Status is not null).ToArray();
    Assert.Equal(new[] { "idle", "running", "idle" }, statuses.Select(change => change.Status));
    Assert.All(statuses, status => Assert.Contains(notifications,
        change => change.Status is null && change.Version == status.Version));
    Assert.All(notifications.Zip(notifications.Skip(1)), pair =>
        Assert.True(pair.First.Version <= pair.Second.Version));
    Assert.Equal("idle", notifications[^2].Status);
    Assert.Null(notifications[^1].Status);
    Assert.Equal(notifications[^2].Version, notifications[^1].Version);
    Assert.True(notifications[^1].Version > notifications[0].Version);
  }

  [Fact]
  public async Task WatchedObserverReceivesToolPendingStopAndSettlementChanges() {
    var toolEntered = Signal();
    var toolRelease = Signal();
    async Task<int> WaitForRelease(int value) {
      toolEntered.TrySetResult();
      await toolRelease.Task;
      return value;
    }

    var client = new ScriptedChatClient(token => ToolBatch(token,
        new FunctionCallContent("call", "wait", new Dictionary<string, object?> { ["value"] = 7 })));
    await using var fixture = new ExecutionFixture(client, [AIFunctionFactory.Create(WaitForRelease, name: "wait")]);
    var watched = new ConcurrentQueue<SessionChange>();
    var unwatched = new ConcurrentQueue<SessionChange>();
    using IDisposable watchedSubscription = await fixture.Session.SubscribeAsync(watched.Enqueue, watched: true);
    using IDisposable unwatchedSubscription = await fixture.Session.SubscribeAsync(unwatched.Enqueue, watched: false);
    long versionAtStop = watched.Last().Version;

    try {
      Guid executionId = await fixture.StartExecutionAsync("start");
      await toolEntered.Task.WaitAsync(TestTimeout);
      long runningVersion = watched.Single(change => change.Status == "running").Version;
      StoredTool runningTool = Assert.Single(StoredTools.Read(fixture.Database));
      Assert.Equal("call", runningTool.CallId);
      Assert.Equal("wait", runningTool.Name);
      Assert.True(runningTool.Started);
      Assert.Null(runningTool.Outcome);
      long versionBeforePending = watched.Last().Version;
      Assert.True(versionBeforePending > runningVersion);

      var pending = new UserMessageSubmission(Guid.NewGuid(), 0, "pending", TestLlmSelection.Default);
      UserMessageReceipt receipt = await fixture.Session.SubmitUserMessageAsync(pending);
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      Assert.Equal(executionId, receipt.ExecutionId);
      SessionSnapshot pendingSnapshot = await fixture.Session.ReadSnapshotAsync();
      Assert.Contains(pendingSnapshot.PendingInputs, input => input.SubmissionId == pending.SubmissionId);
      Assert.True(watched.Last().Version > versionBeforePending);
      long versionBeforeStop = watched.Last().Version;

      Assert.Equal(new StopReceipt(executionId, StopStatus.Requested),
          await fixture.Session.RequestStopAsync(executionId).WaitAsync(TestTimeout));
      SessionSnapshot stoppingSnapshot = await fixture.Session.ReadSnapshotAsync();
      Assert.True(stoppingSnapshot.StopRequested);
      Assert.Equal(executionId, stoppingSnapshot.UnsettledExecutionId);
      Assert.True(watched.Last().Version > versionBeforeStop);
      versionAtStop = watched.Last().Version;

      toolRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Cancelled,
          (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    } finally {
      toolRelease.TrySetResult();
    }

    StoredTool completedTool = Assert.Single(StoredTools.Read(fixture.Database));
    Assert.True(completedTool.Started);
    Assert.Equal(ExecutionOutcome.Cancelled, completedTool.Outcome);
    Assert.NotNull(completedTool.ResultJson);

    SessionChange[] changes = watched.ToArray();
    SessionChange[] watchedChanges = changes.Where(change => change.Status is null).ToArray();
    Assert.All(watchedChanges.Zip(watchedChanges.Skip(1)), pair =>
        Assert.True(pair.First.Version < pair.Second.Version));
    SessionChange[] statuses = changes.Where(change => change.Status is not null).ToArray();
    Assert.Equal(new[] { "idle", "running", "idle" }, statuses.Select(change => change.Status));
    Assert.All(statuses, status => Assert.Contains(watchedChanges, change => change.Version == status.Version));
    long settledVersion = statuses[^1].Version;
    Assert.Contains(watchedChanges, change => change.Version > versionAtStop && change.Version < settledVersion);
    Assert.Equal(settledVersion, watchedChanges[^1].Version);
    Assert.Equal(new[] { "idle", "running", "idle" },
        unwatched.Select(change => change.Status).ToArray());
    Assert.All(unwatched, change => Assert.NotNull(change.Status));
  }

  [Fact]
  public async Task UnwatchedRegistrationRacingSettlementAndFenceMissesNoStatusAndGetsNoContentNotices() {
    var responseEntered = Signal();
    var responseRelease = Signal();
    var client = new ScriptedChatClient(token => Paused(responseEntered, responseRelease, token));
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("start");
    await responseEntered.Task.WaitAsync(TestTimeout);

    var settlementChanges = new ConcurrentQueue<SessionChange>();
    var settlementRace = Signal();
    var subscribing = Signal();
    var settling = Signal();
    Task<IDisposable> registrationTask = Task.Run(async () => {
      subscribing.TrySetResult();
      await settlementRace.Task;
      return await fixture.Session.SubscribeAsync(settlementChanges.Enqueue, watched: false);
    });
    Task releaseTask = Task.Run(async () => {
      settling.TrySetResult();
      await settlementRace.Task;
      responseRelease.TrySetResult();
    });
    await Task.WhenAll(subscribing.Task, settling.Task).WaitAsync(TestTimeout);
    settlementRace.TrySetResult();
    using IDisposable settlementRegistration = await registrationTask.WaitAsync(TestTimeout);
    await releaseTask.WaitAsync(TestTimeout);
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);

    string?[] settlementStatuses = settlementChanges.Select(change => change.Status).ToArray();
    Assert.True(settlementStatuses.SequenceEqual(new string?[] { "idle" })
        || settlementStatuses.SequenceEqual(new string?[] { "running", "idle" }));
    Assert.All(settlementChanges, change => Assert.NotNull(change.Status));
    settlementRegistration.Dispose();

    var fenceChanges = new ConcurrentQueue<SessionChange>();
    var fenceRace = Signal();
    var fenceSubscribing = Signal();
    var fencing = Signal();
    Task<IDisposable> fenceRegistrationTask = Task.Run(async () => {
      fenceSubscribing.TrySetResult();
      await fenceRace.Task;
      return await fixture.Session.SubscribeAsync(fenceChanges.Enqueue, watched: false);
    });
    Task fenceTask = Task.Run(async () => {
      fencing.TrySetResult();
      await fenceRace.Task;
      await fixture.Session.FenceUserMessagesAsync();
    });
    await Task.WhenAll(fenceSubscribing.Task, fencing.Task).WaitAsync(TestTimeout);
    fenceRace.TrySetResult();
    using IDisposable fenceRegistration = await fenceRegistrationTask.WaitAsync(TestTimeout);
    await fenceTask.WaitAsync(TestTimeout);
    await fixture.Session.FenceUserMessagesAsync();

    string?[] fenceStatuses = fenceChanges.Select(change => change.Status).ToArray();
    Assert.True(fenceStatuses.SequenceEqual(new string?[] { "fenced" })
        || fenceStatuses.SequenceEqual(new string?[] { "idle", "fenced" }));
    Assert.All(fenceChanges, change => Assert.NotNull(change.Status));
    Assert.True((await fixture.Session.ReadSnapshotAsync()).AdmissionFenced);
  }

  [Fact]
  public async Task FailedSettlementWriteAfterSubscriptionPublishesNeedsAttention() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(Complete);
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);
    var changes = new ConcurrentQueue<SessionChange>();
    using IDisposable subscription = await session.SubscribeAsync(changes.Enqueue, watched: true);
    database.Execute("""
      CREATE TRIGGER reject_settlement BEFORE UPDATE OF outcome ON execution_runs
      WHEN NEW.outcome IS NOT NULL
      BEGIN SELECT RAISE(ABORT, 'Injected settlement write failure'); END;
      """);

    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
        new(Guid.NewGuid(), 0, "hello", TestLlmSelection.Default));
    Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
    Exception failure = await Assert.ThrowsAnyAsync<Exception>(
        () => session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout));

    Assert.Equal(19, Assert.IsType<SqliteException>(failure.GetBaseException()).SqliteErrorCode);
    SessionSnapshot snapshot = await session.ReadSnapshotAsync();
    Assert.True(snapshot.AdmissionFenced);
    Assert.Equal(executionId, snapshot.UnsettledExecutionId);
    Assert.Null(snapshot.LastSettlement);
    SessionChange[] notifications = changes.ToArray();
    Assert.Equal("needs-attention", notifications.Last(change => change.Status is not null).Status);
    Assert.Null(notifications[^1].Status);
    Assert.Equal(notifications[^2].Version, notifications[^1].Version);
  }

  [Fact]
  public async Task UnexpectedExecutionFailurePublishesNeedsAttention() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(ScriptedResponses.Complete);
    database.Execute("""
      CREATE TRIGGER reject_output BEFORE INSERT ON execution_output
      BEGIN SELECT RAISE(ABORT, 'Injected output write failure'); END;
      """);
    var manager = new SessionManager(store, _ => client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    var changes = new ConcurrentQueue<SessionChange>();
    using IDisposable subscription = await session.SubscribeAsync(changes.Enqueue, watched: false);
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
        new(Guid.NewGuid(), 0, "hello", TestLlmSelection.Default));

    await Assert.ThrowsAnyAsync<Exception>(
        () => session.WaitForSettlementAsync(receipt.ExecutionId!.Value).WaitAsync(TestTimeout));

    Assert.Equal("needs-attention", changes.Last().Status);
    Assert.True((await session.ReadSnapshotAsync()).AdmissionFenced);
  }
}
