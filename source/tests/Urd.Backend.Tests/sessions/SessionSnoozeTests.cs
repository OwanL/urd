using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class SessionSnoozeTests {
  [Fact]
  public async Task SnoozeSurvivesRestartAndRestoreKeepsHistoryWithoutStartingWork() {
    using var database = new TemporaryDatabase();
    DateTimeOffset until = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddHours(1);
    Guid id;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      using var client = new ScriptedChatClient(Complete);
      await using var manager = new SessionManager(store, _ => client);
      PrimarySession session = await manager.CreatePrimaryAsync();
      id = session.Id;
      UserMessageReceipt accepted = await session.SubmitUserMessageAsync(Input("Keep this history"));
      await session.WaitForExecutionFinishAsync(accepted.ExecutionId!.Value).WaitAsync(TestTimeout);
      var changes = new List<Guid>();
      manager.SessionSnoozeChanged += changes.Add;

      Assert.Equal(SessionSnoozeStatus.Changed, await manager.SetSnoozeAsync(id, until, null));
      Assert.Equal(SessionSnoozeStatus.Unchanged, await manager.SetSnoozeAsync(id, until, null));
      Assert.Equal(id, Assert.Single(changes));
      Assert.Equal(id, Assert.Single((await manager.ListPrimaryIdsAsync()).SessionIds));
      Assert.Equal(SessionCloseStatus.Busy, await manager.ClosePrimaryAsync(id));
      Assert.NotEqual(MessageAcceptance.Accepted, (await session.SubmitUserMessageAsync(Input("Stay idle"))).Acceptance);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    reopened.RecoverInterruptedExecutions();
    await using var restarted = new SessionManager(reopened, _ => throw new InvalidOperationException("Must not run."));
    PrimarySession restored = await restarted.GetPrimaryAsync(id);
    SnoozedSession saved = Assert.Single((await restarted.ReadSnoozedAsync()).Sessions);
    Assert.Equal(id, saved.SessionId);
    Assert.Equal(until, saved.Until);
    Assert.True((await restored.ReadSnapshotAsync()).AdmissionFenced);
    Assert.Equal(SessionSnoozeStatus.Conflict, await restarted.SetSnoozeAsync(id, null, until.AddMinutes(-1)));
    Assert.Equal(SessionSnoozeStatus.Changed, await restarted.SetSnoozeAsync(id, null, until));
    Assert.Empty((await restarted.ReadSnoozedAsync()).Sessions);
    SessionSnapshot snapshot = await restored.ReadSnapshotAsync();
    Assert.False(snapshot.AdmissionFenced);
    Assert.Null(snapshot.UnsettledExecutionId);
    var history = await restored.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
    Assert.Single(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.User);
  }

  [Fact]
  public async Task RunningOrQueuedWorkPreventsSnoozing() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var release = Signal();
    using var client = new ScriptedChatClient(token => Paused(entered, release, token));
    await using var manager = new SessionManager(store, _ => client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    PrimarySession sender = await manager.CreatePrimaryAsync();
    DateTimeOffset until = DateTimeOffset.UtcNow.AddHours(1);
    UserMessageReceipt input = await session.SubmitUserMessageAsync(Input("Run first"));
    try {
      await entered.Task.WaitAsync(TestTimeout);
      Assert.Equal(SessionSnoozeStatus.Conflict, await manager.SetSnoozeAsync(session.Id, until, null));
      Assert.False((await session.ReadSnapshotAsync()).StopRequested);
    } finally {
      release.TrySetResult();
    }
    await session.WaitForExecutionFinishAsync(input.ExecutionId!.Value).WaitAsync(TestTimeout);
    using var cancellation = new CancellationTokenSource();
    Task<AgentQuestionResult> pending = manager.Questions.Enqueue(session.Id, sender.Id, Guid.NewGuid(), "call",
        "I can wait until your next turn", wake: false, cancellation.Token);
    try {
      Assert.False(pending.IsCompleted);
      Assert.Equal(SessionSnoozeStatus.Conflict, await manager.SetSnoozeAsync(session.Id, until, null));
      Assert.Empty((await manager.ReadSnoozedAsync()).Sessions);
    } finally {
      await cancellation.CancelAsync();
    }
  }

  [Fact]
  public async Task SnoozeAndSendCannotBothWinAdmission() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var ready = Signal();
    var entered = Signal();
    var release = Signal();
    using var client = new ScriptedChatClient(token => Paused(entered, release, token));
    await using var manager = new SessionManager(store, _ => client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    Task<SessionSnoozeStatus> snooze = Task.Run(async () => {
      await ready.Task;
      return await manager.SetSnoozeAsync(session.Id, DateTimeOffset.UtcNow.AddHours(1), null);
    });
    Task<UserMessageReceipt> send = Task.Run(async () => {
      await ready.Task;
      return await session.SubmitUserMessageAsync(Input("Send or snooze"));
    });
    ready.SetResult();

    try {
      await Task.WhenAll(snooze, send).WaitAsync(TestTimeout);
      if ((await send).Acceptance == MessageAcceptance.Accepted) {
        Assert.Equal(SessionSnoozeStatus.Conflict, await snooze);
        Assert.Empty((await manager.ReadSnoozedAsync()).Sessions);
      } else {
        Assert.Equal(SessionSnoozeStatus.Changed, await snooze);
        Assert.Single((await manager.ReadSnoozedAsync()).Sessions);
        Assert.Null((await session.ReadSnapshotAsync()).UnsettledExecutionId);
      }
    } finally {
      release.TrySetResult();
    }
  }

  [Fact]
  public async Task OverdueSessionIsVisibleAndUnfencedAfterRestart() {
    using var database = new TemporaryDatabase();
    Guid id;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      id = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      DateTimeOffset until = DateTimeOffset.UtcNow.AddMinutes(-1);
      store.Sessions.SetSnoozedUntil(id, until);
      Assert.Equal(id, Assert.Single(store.Sessions.ReadSnoozed(until.AddSeconds(-1))).SessionId);
      Assert.Empty(store.Sessions.ReadSnoozed(until.AddSeconds(1)));
    }
    using UrdStore reopened = UrdStore.Open(database.Path);
    await using var manager = new SessionManager(reopened, _ => throw new InvalidOperationException("Must not run."));
    Assert.Empty((await manager.ReadSnoozedAsync()).Sessions);
    SessionSnapshot snapshot = await (await manager.GetPrimaryAsync(id)).ReadSnapshotAsync();
    Assert.False(snapshot.AdmissionFenced);
    Assert.Null(snapshot.UnsettledExecutionId);
  }

  private static UserMessageSubmission Input(string text) => new(Guid.NewGuid(), 0, text, TestLlmSelection.Default);
}
