using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class BrowserViewerOwnersTests {
  [Fact]
  public async Task LooksUpSavedPrimaryWithoutOpeningItAndRejectsOtherIds() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid primary = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid child = store.Sessions.CreateSubagent(Guid.NewGuid(), primary, "finished").Id;
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("Must not run."));

    Assert.Null(await manager.BrowserViewerOwnersAsync(Guid.NewGuid()));
    Assert.Null(await manager.BrowserViewerOwnersAsync(child));
    Assert.Equal(new[] { primary }, await manager.BrowserViewerOwnersAsync(primary));
    Assert.Null(manager.Registry.FindPrimary(primary));

    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.BrowserViewerOwnersAsync(primary, cancellation.Token));
  }

  [Fact]
  public async Task IncludesOnlyLiveDescendantsWithPersistedAncestryToPrimary() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid primary = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid other = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    SessionRecord child = store.Sessions.CreateSubagent(Guid.NewGuid(), primary, "child");
    SessionRecord nested = store.Sessions.CreateSubagent(Guid.NewGuid(), child.Id, "nested");
    SessionRecord inactive = store.Sessions.CreateSubagent(Guid.NewGuid(), primary, "inactive");
    SessionRecord throughInactive = store.Sessions.CreateSubagent(Guid.NewGuid(), inactive.Id, "through-inactive");
    SessionRecord wrongTree = store.Sessions.CreateSubagent(Guid.NewGuid(), other, "wrong-tree");
    SessionRecord wrongNested = store.Sessions.CreateSubagent(Guid.NewGuid(), wrongTree.Id, "wrong-nested");
    SessionRecord missing = child with { Id = Guid.NewGuid() };
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("Must not run."));

    await TrackAsync(manager, store, child);
    await TrackAsync(manager, store, nested);
    // These live handles deliberately disagree with storage to test the authorization boundary.
    await TrackAsync(manager, store, throughInactive with { ParentSessionId = primary });
    await TrackAsync(manager, store, wrongTree with { ParentSessionId = primary });
    await TrackAsync(manager, store, wrongNested);
    await TrackAsync(manager, store, missing);

    IReadOnlyList<Guid> owners = (await manager.BrowserViewerOwnersAsync(primary))!;
    Assert.Equal(new[] { primary, child.Id, throughInactive.Id, nested.Id }.Order(), owners.Order());
    Assert.DoesNotContain(inactive.Id, owners);
    Assert.DoesNotContain(wrongTree.Id, owners);
    Assert.DoesNotContain(wrongNested.Id, owners);
    Assert.DoesNotContain(missing.Id, owners);
    Assert.Equal(new[] { other }, await manager.BrowserViewerOwnersAsync(other));

    await manager.Registry.UntrackChildAsync(child.Id);
    owners = (await manager.BrowserViewerOwnersAsync(primary))!;
    Assert.DoesNotContain(child.Id, owners);
    Assert.DoesNotContain(nested.Id, owners);
  }

  [Fact]
  public async Task RejectsCyclesInPersistedAncestry() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid primary = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    SessionRecord first = store.Sessions.CreateSubagent(Guid.NewGuid(), primary, "first");
    SessionRecord second = store.Sessions.CreateSubagent(Guid.NewGuid(), first.Id, "second");
    // Raw SQL models a corrupted persisted ancestry that registry ancestry alone would accept.
    database.Execute($"UPDATE sessions SET parent_session_id = '{second.Id:D}' WHERE session_id = '{first.Id:D}';"); // raw-sql: simulate corrupt ancestry.
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("Must not run."));
    await TrackAsync(manager, store, first);
    await TrackAsync(manager, store, second);

    Assert.Equal(new[] { primary }, await manager.BrowserViewerOwnersAsync(primary).WaitAsync(TestTimeout));
  }

  private static Task<bool> TrackAsync(SessionManager manager, UrdStore store, SessionRecord record) =>
      manager.Registry.RunOpenAsync(() => {
        manager.Registry.TrackChild(new SubagentSession(record, store, _ => throw new InvalidOperationException("Must not run.")));
        return true;
      });
}
