using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

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
    Assert.True(store.Prepass.PublishTitle(identity, "First request"));
    Assert.False(store.Prepass.PublishTitle(identity, "Replacement title"));
    Assert.True(store.Prepass.WritePruningStatus(identity, Guid.Empty,
        new StartingResourcePruningStatus("tools", "running", 4, 3)));
    Assert.True(store.Prepass.WritePruningStatus(identity, Guid.Empty,
        new StartingResourcePruningStatus("skills", "running", 2, 1)));

    store.Executions.RequestStop(session, execution, ownsExecution: true);
    Assert.False(store.Prepass.WritePruningStatus(identity, Guid.Empty,
        new StartingResourcePruningStatus("tools", "running", 4, 3)));
    Assert.True(store.Prepass.WritePruningStatus(identity, Guid.Empty,
        new StartingResourcePruningStatus("tools", "completed", 4, 3)));
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
    Assert.False(store.Prepass.PublishTitle(identity, "Late old title"));
    Assert.True(store.Prepass.WritePruningStatus(retryIdentity, Guid.Empty,
        new StartingResourcePruningStatus("skills", "running", 2, 2)));
    Assert.Equal(ExecutionOutcome.Cancelled,
        store.Executions.TryFinish(session, execution, ExecutionOutcome.Completed)!.Outcome);
    Assert.Contains(store.Snapshots.ReadPersisted(session).Pruning!,
        status => status.Category == "skills" && status.State == "running");
  }
}
