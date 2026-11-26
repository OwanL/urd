using System.Text;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Storage;

public sealed class PrepassStorageTests {
  [Fact]
  public void TitleClaimIsOneShotAndPruningStatusRetiresByCategory() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid execution = Guid.NewGuid();
    var identity = new ExecutionIdentity(session, execution, 0);
    store.UserMessages.Submit(session, 0, new(Guid.NewGuid(), 0, "request", TestLlmSelection.Default),
        true, execution, null);

    Assert.True(store.Prepass.TryClaimTitle(identity));
    Assert.False(store.Prepass.TryClaimTitle(identity));
    Assert.Equal("First request", store.Prepass.PublishTitle(identity, "First request"));
    Assert.Null(store.Prepass.PublishTitle(identity, "Replacement title"));
    Assert.True(store.Prepass.WritePruningProgress(identity, Guid.Empty,
        new StartingResourcePruningStatus("tools", "running", 4, 3), null, null));
    Assert.True(store.Prepass.WritePruningProgress(identity, Guid.Empty,
        new StartingResourcePruningStatus("skills", "running", 2, 1), null, null));

    store.Executions.RequestStop(session, execution, ownsExecution: true);
    Assert.False(store.Prepass.WritePruningProgress(identity, Guid.Empty,
        new StartingResourcePruningStatus("tools", "running", 4, 3), null, null));
    Assert.True(store.Prepass.WritePruningProgress(identity, Guid.Empty,
        new StartingResourcePruningStatus("tools", "completed", 4, 3), null, null));
    Assert.Equal(ExecutionOutcome.Cancelled,
        store.Executions.TryFinish(session, execution, ExecutionOutcome.Completed)!.Outcome);

    SessionSnapshot snapshot = store.Snapshots.ReadPersisted(session);
    Assert.Equal("First request", snapshot.Title);
    Assert.Equal(new[] { ("tools", "completed"), ("skills", "cancelled") },
        snapshot.Pruning!.Select(status => (status.Category, status.State)));

    // A stopped preparation can retry its still-unactivated boundary in a new run,
    // but neither that run nor retiring the old one may replace/replay its title.
    Guid retry = Guid.NewGuid();
    store.UserMessages.Submit(session, 0, new(Guid.NewGuid(), 0, "retry", TestLlmSelection.Default),
        true, retry, null);
    var retryIdentity = new ExecutionIdentity(session, retry, 0);
    Assert.False(store.Prepass.TryClaimTitle(retryIdentity));
    Assert.Null(store.Prepass.PublishTitle(identity, "Late old title"));
    Assert.True(store.Prepass.WritePruningProgress(retryIdentity, Guid.Empty,
        new StartingResourcePruningStatus("skills", "running", 2, 2), null, null));
    Assert.Equal(ExecutionOutcome.Cancelled,
        store.Executions.TryFinish(session, execution, ExecutionOutcome.Completed)!.Outcome);
    Assert.Contains(store.Snapshots.ReadPersisted(session).Pruning!,
        status => status.Category == "skills" && status.State == "running");
  }

  [Fact]
  public void PreviewTailsHandleSplitUnicodeAndTitleFailureSurvivesReopen() {
    using var database = new TemporaryDatabase();
    Guid session;
    Guid execution = Guid.NewGuid();
    string longError = new('x', 1100);
    using (UrdStore store = UrdStore.Open(database.Path)) {
      session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      var identity = new ExecutionIdentity(session, execution, 0);
      store.UserMessages.Submit(session, 0, new(Guid.NewGuid(), 0, "request", TestLlmSelection.Default),
          true, execution, null);
      Assert.True(store.Prepass.TryClaimTitle(identity));
      Assert.True(store.Prepass.RecordModel(identity, "title", TestLlmSelection.UsedSessionResolution));
      Assert.Equal(1L, database.Scalar("SELECT used_session_model FROM session_prepass_details WHERE category = 'title';")); // raw-sql: the session-model stand-in is recorded with the model

      PrepassPreviewWrite highSurrogate = store.Prepass.RecordPreview(identity, "title", "\ud83d");
      Assert.True(highSurrogate.Accepted);
      PrepassPreviewWrite completedPair = store.Prepass.RecordPreview(identity, "title",
          "\ude80" + new string('a', 8191), pendingOutput: highSurrogate.PendingOutput);
      Assert.True(completedPair.Accepted);
      PrepassPreviewWrite outputTail = store.Prepass.RecordPreview(identity, "title", "b",
          pendingOutput: completedPair.PendingOutput);
      Assert.True(outputTail.Accepted);
      Assert.True(store.Prepass.RecordPreview(identity, "title", reasoning: new string('r', 8193)).Accepted);
      Assert.True(store.Prepass.RecordTitleOutcome(identity, "failed", longError));
      Assert.Null(store.Prepass.PublishTitle(identity, "Late title"));
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Null(reopened.Snapshots.ReadPersisted(session).Title);
    string output = database.ReadRows("SELECT output_text FROM session_prepass_details WHERE category = 'title';",
        row => row.GetString(0)).Single();
    string reasoning = database.ReadRows("SELECT reasoning_text FROM session_prepass_details WHERE category = 'title';",
        row => row.GetString(0)).Single();
    Assert.Equal(8192, output.EnumerateRunes().Count());
    Assert.StartsWith("a", output, StringComparison.Ordinal);
    Assert.EndsWith("b", output, StringComparison.Ordinal);
    Assert.Equal(8192, reasoning.EnumerateRunes().Count());
    Assert.Equal(1L, database.Scalar("SELECT output_truncated FROM session_prepass_details WHERE category = 'title';")); // raw-sql: stored bounds and terminal state are the subject under test
    Assert.Equal(1L, database.Scalar("SELECT reasoning_truncated FROM session_prepass_details WHERE category = 'title';")); // raw-sql: stored bounds and terminal state are the subject under test
    Assert.Equal("failed", database.Scalar("SELECT state FROM session_prepass_details WHERE category = 'title';")); // raw-sql: stored bounds and terminal state are the subject under test
    Assert.Equal(1L, database.Scalar("SELECT finished_at IS NOT NULL FROM session_prepass_details WHERE category = 'title';")); // raw-sql: stored bounds and terminal state are the subject under test
    Assert.Equal(1024L, database.Scalar("SELECT length(error) FROM session_prepass_details WHERE category = 'title';")); // raw-sql: stored bounds and terminal state are the subject under test
    Assert.Contains(TestLlmSelection.Default.ModelId,
        database.ReadRows("SELECT llm_selection_json FROM session_prepass_details WHERE category = 'title';",
            row => row.GetString(0)).Single(), StringComparison.Ordinal);
  }

  [Fact]
  public void UsedSessionModelSurvivesSameRunUpdatesAndResetsPerRun() {
    using var database = new TemporaryDatabase();
    Guid session;
    Guid execution = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      var identity = new ExecutionIdentity(session, execution, 0);
      store.UserMessages.Submit(session, 0, new(Guid.NewGuid(), 0, "request", TestLlmSelection.Default),
          true, execution, null);
      Assert.True(store.Prepass.WritePruningProgress(identity, Guid.Empty,
          new StartingResourcePruningStatus("tools", "running", 2, 2), null, null));
      Assert.True(store.Prepass.RecordModel(identity, "pruning.tools", TestLlmSelection.UsedSessionResolution));

      // Same-run updates keep whichever recording happened on the running detail.
      Assert.True(store.Prepass.WritePruningProgress(identity, Guid.Empty,
          new StartingResourcePruningStatus("tools", "completed", 2, 1), null, null));
      Assert.Equal(1L, database.Scalar("SELECT used_session_model FROM session_prepass_details WHERE category = 'tools';")); // raw-sql: the last recorded model fact survives same-run updates

      // A new run's detail starts without the previous run's recorded model fact.
      store.Executions.RequestStop(session, execution, ownsExecution: true);
      Assert.Equal(ExecutionOutcome.Cancelled,
          store.Executions.TryFinish(session, execution, ExecutionOutcome.Completed)!.Outcome);
      Guid retry = Guid.NewGuid();
      var retryIdentity = new ExecutionIdentity(session, retry, 0);
      store.UserMessages.Submit(session, 0, new(Guid.NewGuid(), 0, "retry", TestLlmSelection.Default), true, retry, null);
      Assert.True(store.Prepass.WritePruningProgress(retryIdentity, Guid.Empty,
          new StartingResourcePruningStatus("tools", "running", 2, 2), null, null));
      Assert.Equal(0L, database.Scalar("SELECT used_session_model FROM session_prepass_details WHERE category = 'tools';")); // raw-sql: the recorded model fact resets for a new run
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Contains(reopened.Snapshots.ReadPersisted(session).Pruning!,
        status => status.Category == "tools" && status.State == "running");
  }

  [Fact]
  public void DuplicateTitlesTakeTheLowestFreeSuffixShortenedWithinTheLimit() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    string longBase = new string('b', 25);

    Assert.Equal("Fix login bug", Published(store, "Fix login bug"));
    Assert.Equal("Fix login bug (2)", Published(store, "Fix login bug"));
    Assert.Equal("Fix login bug (3)", Published(store, "Fix login bug"));
    Assert.Equal(longBase, Published(store, longBase));

    // The base shortens so the suffixed title stays at or below 25 scalars.
    string second = Published(store, longBase);
    Assert.Equal(25, second.EnumerateRunes().Count());
    Assert.EndsWith(" (2)", second, StringComparison.Ordinal);
    string third = Published(store, longBase);
    Assert.Equal(25, third.EnumerateRunes().Count());
    Assert.EndsWith(" (3)", third, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ConcurrentStoresPublishingTheSameTitleDoNotCollide() {
    using var database = new TemporaryDatabase();
    var first = ClaimedTitle(database.Path);
    var second = ClaimedTitle(database.Path);
    using UrdStore storeA = UrdStore.Open(database.Path);
    using UrdStore storeB = UrdStore.Open(database.Path);

    string?[] published = await Task.WhenAll(
        Task.Run(() => storeA.Prepass.PublishTitle(first.Identity, "Shared launch")),
        Task.Run(() => storeB.Prepass.PublishTitle(second.Identity, "Shared launch"))).WaitAsync(TestTimeout);
    Assert.Equal(storeA.Snapshots.ReadPersisted(first.Session).Title, published[0]);
    Assert.Equal(storeB.Snapshots.ReadPersisted(second.Session).Title, published[1]);
    string[] titles = published.Select(title => Assert.IsType<string>(title)).ToArray();
    Assert.Equal(["Shared launch", "Shared launch (2)"], [.. titles.OrderBy(title => title, StringComparer.Ordinal)]);
  }

  private static string Published(UrdStore store, string title) {
    var execution = new ExecutionIdentity(store.Sessions.CreatePrimary(Guid.NewGuid()).Id, Guid.NewGuid(), 0);
    store.UserMessages.Submit(execution.SessionId, 0,
        new(Guid.NewGuid(), 0, "requested", TestLlmSelection.Default), true, execution.ExecutionId, null);
    Assert.True(store.Prepass.TryClaimTitle(execution));
    string published = Assert.IsType<string>(store.Prepass.PublishTitle(execution, title));
    Assert.Equal(published, store.Snapshots.ReadPersisted(execution.SessionId).Title);
    return published;
  }

  private static (Guid Session, ExecutionIdentity Identity) ClaimedTitle(string databasePath) {
    using UrdStore store = UrdStore.Open(databasePath);
    var identity = new ExecutionIdentity(store.Sessions.CreatePrimary(Guid.NewGuid()).Id, Guid.NewGuid(), 0);
    store.UserMessages.Submit(identity.SessionId, 0,
        new(Guid.NewGuid(), 0, "requested", TestLlmSelection.Default), true, identity.ExecutionId, null);
    Assert.True(store.Prepass.TryClaimTitle(identity));
    return (identity.SessionId, identity);
  }
}
