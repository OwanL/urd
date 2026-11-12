using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class SessionWorkingTimeStorageTests {
  [Fact]
  public void SettledRunsAccumulateOnceAndIdleTimeDoesNotAccrue() {
    using var database = new TemporaryDatabase();
    Guid session;
    long completed;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      var first = new UserMessageSubmission(Guid.NewGuid(), 0, "first", TestLlmSelection.Default);
      Guid firstRun = store.UserMessages.Admit(session, 0, first, true, Guid.NewGuid()).Receipt.ExecutionId!.Value;
      SetWorkingStart(database, firstRun, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
      DateTimeOffset startedAt = store.Snapshots.ReadStored(session).WorkingTime!.StartedAt!.Value;

      var retry = store.UserMessages.Admit(session, 0, first, true, Guid.NewGuid());
      Assert.False(retry.NewlyAccepted);
      Assert.Equal(startedAt, store.Snapshots.ReadStored(session).WorkingTime!.StartedAt!.Value);
      SessionExecutionResult firstSettlement = StopAndSettle(store, session, firstRun);
      completed = store.Snapshots.ReadStored(session).WorkingTime!.CompletedMilliseconds;
      Assert.True(completed > 0);
      Assert.Equal(firstSettlement, store.Executions.TrySettle(session, firstRun, ExecutionOutcome.Cancelled)!);
      long firstCompleted = completed;
      Assert.Equal(firstCompleted, store.Snapshots.ReadStored(session).WorkingTime!.CompletedMilliseconds);

      Guid secondRun = store.UserMessages.Admit(session, 0,
          new(Guid.NewGuid(), 0, "second", TestLlmSelection.Default), true, Guid.NewGuid()).Receipt.ExecutionId!.Value;
      SetWorkingStart(database, secondRun, new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
      StopAndSettle(store, session, secondRun);
      completed = store.Snapshots.ReadStored(session).WorkingTime!.CompletedMilliseconds;
      Assert.True(completed > firstCompleted);
      Assert.Null(store.Snapshots.ReadStored(session).WorkingTime!.StartedAt);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    SessionWorkingTime restored = reopened.Snapshots.ReadStored(session).WorkingTime!;
    Assert.Equal(completed, restored.CompletedMilliseconds);
    Assert.Null(restored.StartedAt);
    Assert.False(restored.HasUnknownDuration);
  }

  [Fact]
  public void ManualCompactionAdmissionStartsWorkingTimeAndExactRetryKeepsIt() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var submission = new CompactionSubmission(Guid.NewGuid(), 0, TestLlmSelection.Default);

    var admission = store.Compactions.Admit(session, submission, accepting: true)!.Value;
    Assert.True(admission.New);
    DateTimeOffset startedAt = store.Snapshots.ReadStored(session).WorkingTime!.StartedAt!.Value;
    Assert.False(store.Compactions.Admit(session, submission, accepting: true)!.Value.New);
    Assert.Equal(startedAt, store.Snapshots.ReadStored(session).WorkingTime!.StartedAt!.Value);

    Assert.Equal(ExecutionOutcome.Failed,
        store.Executions.TrySettle(session, submission.OperationId, ExecutionOutcome.Failed)!.Outcome);
    SessionWorkingTime settled = store.Snapshots.ReadStored(session).WorkingTime!;
    Assert.Null(settled.StartedAt);
    Assert.False(settled.HasUnknownDuration);
  }

  [Fact]
  public void RecoveryPreservesCompletedTimeAndMarksInterruptedDurationUnknown() {
    using var database = new TemporaryDatabase();
    Guid session;
    long completedBeforeInterruption;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      Guid firstRun = store.UserMessages.Admit(session, 0,
          new(Guid.NewGuid(), 0, "completed", TestLlmSelection.Default), true, Guid.NewGuid()).Receipt.ExecutionId!.Value;
      SetWorkingStart(database, firstRun, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
      StopAndSettle(store, session, firstRun);
      completedBeforeInterruption = store.Snapshots.ReadStored(session).WorkingTime!.CompletedMilliseconds;
      Assert.True(completedBeforeInterruption > 0);

      store.UserMessages.Admit(session, 0,
          new(Guid.NewGuid(), 0, "interrupted", TestLlmSelection.Default), true, Guid.NewGuid());
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.NotNull(reopened.Snapshots.ReadStored(session).WorkingTime!.StartedAt);
    reopened.RecoverInterruptedExecutions();
    SessionWorkingTime recovered = reopened.Snapshots.ReadStored(session).WorkingTime!;
    Assert.Equal(completedBeforeInterruption, recovered.CompletedMilliseconds);
    Assert.Null(recovered.StartedAt);
    Assert.True(recovered.HasUnknownDuration);
    reopened.RecoverInterruptedExecutions();
    Assert.Equal(recovered, reopened.Snapshots.ReadStored(session).WorkingTime!);
  }

  private static void SetWorkingStart(TemporaryDatabase database, Guid execution, DateTimeOffset startedAt) =>
      database.Execute($"UPDATE execution_runs SET working_started_at = '{startedAt:O}' WHERE run_id = '{execution:D}';");

  private static SessionExecutionResult StopAndSettle(UrdStore store, Guid session, Guid execution) {
    store.Executions.RequestStop(session, execution, ownsExecution: true);
    return store.Executions.TrySettle(session, execution, ExecutionOutcome.Cancelled)!;
  }
}
