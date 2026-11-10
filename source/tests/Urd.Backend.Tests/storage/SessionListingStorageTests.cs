using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class SessionListingStorageTests {
  [Fact]
  public void KeysetPagesSurviveInsertionDeletedBoundaryAndReopen() {
    using var database = new TemporaryDatabase();
    Guid[] created = Enumerable.Range(1, 125).Reverse().Select(Id).ToArray();
    SessionIdPage first;
    Guid inserted = Id(200);
    using (UrdStore store = UrdStore.Open(database.Path)) {
      foreach (Guid id in created)
        store.Sessions.CreatePrimary(id);
      first = store.Sessions.ListPrimaryIds(null);
      Assert.Equal(created.Reverse().Take(60), first.SessionIds);
      Assert.Equal("66", first.NextCursor);
      store.Sessions.CreatePrimary(inserted);
      database.Execute($"PRAGMA foreign_keys = ON; DELETE FROM sessions WHERE session_id = '{first.SessionIds[^1]:D}';");
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    SessionIdPage second = reopened.Sessions.ListPrimaryIds(first.NextCursor);
    SessionIdPage third = reopened.Sessions.ListPrimaryIds(second.NextCursor);
    Assert.Equal(created.Reverse(), first.SessionIds.Concat(second.SessionIds).Concat(third.SessionIds));
    Assert.Equal("6", second.NextCursor);
    Assert.Equal(5, third.SessionIds.Count);
    Assert.Null(third.NextCursor);
    Assert.Equal(inserted, reopened.Sessions.ListPrimaryIds(null).SessionIds[0]);
  }

  private static Guid Id(int value) => Guid.Parse($"{value:x8}-0000-0000-0000-000000000000");
}
