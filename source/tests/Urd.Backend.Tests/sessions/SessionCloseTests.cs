using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class SessionCloseTests {
  [Fact]
  public async Task ClosesSettledSessionAndNeverLoadsItAgainAfterReopeningStore() {
    using var database = new TemporaryDatabase();
    using var client = new ScriptedChatClient(Complete);
    Guid closedId;
    Guid openId;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      await using var manager = new SessionManager(store, _ => client);
      PrimarySession session = await manager.CreatePrimaryAsync();
      closedId = session.Id;
      openId = (await manager.CreatePrimaryAsync()).Id;
      var input = new UserMessageSubmission(Guid.NewGuid(), 0, "delete this conversation", TestLlmSelection.Default);
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(input);
      Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout)).Outcome);
      var history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
      MessageRef message = Assert.Single(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.User).Text!.From.Message;

      Task<AgentQuestionResult> question = manager.Questions.Enqueue(closedId, openId, Guid.NewGuid(),
          "ask", "Won't wake the recipient", wake: false, CancellationToken.None);
      Assert.False(question.IsCompleted);
      var notices = new List<SessionChange>();
      using var watching = await session.SubscribeAsync(notices.Add, watched: true);
      using var background = await session.SubscribeAsync(notices.Add, watched: false);

      Assert.Equal(SessionCloseStatus.Closed, await manager.ClosePrimaryAsync(closedId));
      Assert.Equal(new AgentQuestionResult(null, AgentQuestions.RecipientFinished), await question.WaitAsync(TestTimeout));
      Assert.Equal("unavailable", notices[^1].Status);
      Assert.Equal(notices[^1], notices[^2]);
      Assert.Null(notices[^1].Title);
      Assert.Equal(2, notices.Count(change => change.Status == "unavailable"));
      var racingSubscription = new List<SessionChange>();
      using var afterClose = await session.SubscribeAsync(racingSubscription.Add, watched: true);
      Assert.Equal(notices[^1], Assert.Single(racingSubscription));

      Assert.Equal(SessionCloseStatus.NotFound, await manager.ClosePrimaryAsync(closedId));
      Assert.Equal(new[] { openId }, (await manager.ListPrimaryIdsAsync()).SessionIds);
      await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.GetPrimaryAsync(closedId));
      await Assert.ThrowsAsync<KeyNotFoundException>(() => session.ReadSnapshotAsync());
      await Assert.ThrowsAsync<KeyNotFoundException>(() => session.SubmitUserMessageAsync(input));
      await Assert.ThrowsAsync<KeyNotFoundException>(() => session.ContinueAsync(new(Guid.NewGuid(), 0, executionId,
          TestLlmSelection.Default)));
      await Assert.ThrowsAsync<KeyNotFoundException>(() => session.EditUserMessageAsync(new(Guid.NewGuid(), message,
          "must not replace", TestLlmSelection.Default)));
      await Assert.ThrowsAsync<KeyNotFoundException>(() => session.RequestStopAsync(executionId));
      Assert.Null(store.Sessions.Read(closedId));
      Assert.Empty(StoredExecutions.Read(database));
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    reopened.RecoverInterruptedExecutions();
    await using var restarted = new SessionManager(reopened, _ => throw new InvalidOperationException("No replay."));
    Assert.Equal(new[] { openId }, (await restarted.ListPrimaryIdsAsync()).SessionIds);
    Assert.Equal(openId, (await restarted.GetPrimaryAsync(openId)).Id);
    Assert.Equal(SessionCloseStatus.NotFound, await restarted.ClosePrimaryAsync(closedId));
    await Assert.ThrowsAsync<KeyNotFoundException>(() => restarted.GetPrimaryAsync(closedId));
  }

  [Fact]
  public async Task ActiveRunAndEditDrainAreBusyWithoutImplicitStop() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var runRelease = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    int calls = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref calls) == 1
        ? Paused(entered, runRelease, token, cleanupEntered, cleanupRelease) : Complete(token));
    await using var manager = new SessionManager(store, _ => client);
    PrimarySession session = await manager.CreatePrimaryAsync();
    var input = new UserMessageSubmission(Guid.NewGuid(), 0, "original", TestLlmSelection.Default);
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(input);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      Assert.Equal(SessionCloseStatus.Busy, await manager.ClosePrimaryAsync(session.Id));
      Assert.False((await session.ReadSnapshotAsync()).StopRequested);
      var history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
      var message = Assert.Single(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.User).Text!.From.Message;
      Task<MessageEditReceipt> editing = session.EditUserMessageAsync(new(Guid.NewGuid(), message,
          "replacement", TestLlmSelection.Default));
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.True((await session.ReadSnapshotAsync()).StopRequested);
      Assert.Equal(SessionCloseStatus.Busy, await manager.ClosePrimaryAsync(session.Id));
      Assert.Equal(session.Id, Assert.Single((await manager.ListPrimaryIdsAsync()).SessionIds));
      cleanupRelease.TrySetResult();
      MessageEditReceipt replacement = await editing.WaitAsync(TestTimeout);
      Assert.Equal(ExecutionOutcome.Cancelled, (await session.WaitForExecutionFinishAsync(receipt.ExecutionId!.Value)).Outcome);
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(replacement.ExecutionId).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(SessionCloseStatus.Closed, await manager.ClosePrimaryAsync(session.Id));
    } finally {
      runRelease.TrySetResult();
      cleanupRelease.TrySetResult();
    }
  }

  [Fact]
  public async Task UnownedExecutionIsBusyAndHelperIsNotAPrimaryCloseTarget() {
    using var database = new TemporaryDatabase();
    Guid id = Guid.NewGuid();
    Guid childId = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(id);
      store.Sessions.CreateSubagent(childId, id, "bounded-call");
      UserMessageReceipt receipt = store.UserMessages.Submit(id, 0, new(Guid.NewGuid(), 0,
          "interrupted", TestLlmSelection.Default), true, Guid.NewGuid()).Receipt;
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    await using var manager = new SessionManager(reopened, _ => throw new InvalidOperationException("No replay."));
    Assert.Equal(SessionCloseStatus.Busy, await manager.ClosePrimaryAsync(id));
    Assert.Equal(SessionCloseStatus.NotFound, await manager.ClosePrimaryAsync(childId));
    Assert.NotNull(reopened.Sessions.Read(childId));
    Assert.NotNull(reopened.Sessions.Read(id));
  }

  [Fact]
  public async Task FailedDeletionLeavesCanonicalHandleAndObserversIntactForRetry() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("No model request."));
    PrimarySession session = await manager.CreatePrimaryAsync();
    var statuses = new List<string?>();
    using var subscribed = await session.SubscribeAsync(change => statuses.Add(change.Status), watched: false);
    database.Execute("""
        CREATE TRIGGER block_close BEFORE DELETE ON sessions
        BEGIN SELECT RAISE(ABORT, 'injected close failure'); END;
        """);
    Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => manager.ClosePrimaryAsync(session.Id))).SqliteErrorCode);
    Assert.Same(session, await manager.GetPrimaryAsync(session.Id));
    Assert.False((await session.ReadSnapshotAsync()).AdmissionFenced);
    Assert.Equal(new[] { "idle" }, statuses);

    database.Execute("DROP TRIGGER block_close;");
    Assert.Equal(SessionCloseStatus.Closed, await manager.ClosePrimaryAsync(session.Id));
    Assert.Equal(new[] { "idle", "unavailable" }, statuses);
  }

  [Fact]
  public async Task CloseKillsRetainedShellDescendantsBeforeSuccess() {
    if (!OperatingSystem.IsWindows())
      return;
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    string directory = Path.GetDirectoryName(database.Path)!;
    var preferences = new PreferenceManager(store);
    preferences.SetWorkingDirectory(directory);
    int calls = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref calls) == 1
        ? ToolBatch(token, new FunctionCallContent("launch", "bash", new Dictionary<string, object?> {
          ["command"] = "sleep 60 & child=$!; cat /proc/$child/winpid > survivor.pid"
        })) : Complete(token));
    await using var manager = new SessionManager(store, _ => client,
        new AgentToolSettings(null, new Dictionary<string, string>()), preferences);
    PrimarySession session = await manager.CreatePrimaryAsync();
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "launch process", TestLlmSelection.Default));
    Assert.Equal(ExecutionOutcome.Completed, (await session.WaitForExecutionFinishAsync(receipt.ExecutionId!.Value)
        .WaitAsync(TestTimeout)).Outcome);
    string pid = await File.ReadAllTextAsync(Path.Combine(directory, "survivor.pid"));
    using Process survivor = Process.GetProcessById(int.Parse(pid, CultureInfo.InvariantCulture));
    _ = survivor.Handle;
    Assert.False(survivor.HasExited);

    Assert.Equal(SessionCloseStatus.Closed, await manager.ClosePrimaryAsync(session.Id).WaitAsync(TestTimeout));
    Assert.True(survivor.HasExited);
  }

  [Fact]
  public async Task AdmittedCloseSurvivesCancellationAndShutdownCannotOvertakeIt() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("No model request."));
    PrimarySession closing = await manager.CreatePrimaryAsync();
    var closedNotice = Signal();
    var releaseNotice = Signal();
    var statuses = new ConcurrentQueue<string?>();
    using var subscribed = await closing.SubscribeAsync(change => {
      statuses.Enqueue(change.Status);
      if (change.Status == "unavailable") {
        closedNotice.TrySetResult();
        releaseNotice.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
      }
    }, watched: false);
    using var cancellation = new CancellationTokenSource();
    Task<SessionCloseStatus> close = Task.Run(() => manager.ClosePrimaryAsync(closing.Id, cancellation.Token));
    try {
      await closedNotice.Task.WaitAsync(TestTimeout);
      Assert.Equal(SessionCloseStatus.NotFound, await manager.ClosePrimaryAsync(closing.Id));
      Assert.Empty((await manager.ListPrimaryIdsAsync()).SessionIds);
      PrimarySession other = await manager.CreatePrimaryAsync();
      Task shutdown = manager.ShutdownSessionsAsync();
      Assert.False(shutdown.IsCompleted); // The retained closing handle still belongs to shutdown.
      await cancellation.CancelAsync();
      await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ClosePrimaryAsync(other.Id));
      Assert.NotNull(store.Sessions.Read(other.Id));
      releaseNotice.TrySetResult();
      Assert.Equal(SessionCloseStatus.Closed, await close.WaitAsync(TestTimeout));
      await shutdown.WaitAsync(TestTimeout);
      Assert.Equal(new[] { "idle", "unavailable" }, statuses);
    } finally {
      releaseNotice.TrySetResult();
      await close.WaitAsync(TestTimeout);
    }
  }
}
