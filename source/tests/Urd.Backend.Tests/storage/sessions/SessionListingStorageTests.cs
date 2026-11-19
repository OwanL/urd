using Urd.Backend.Tests.Support;
using Urd.Core.Context;
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

  [Fact]
  public void SessionKindsAndOwnershipRoundTripAcrossReopen() {
    using var database = new TemporaryDatabase();
    Guid primary = Guid.NewGuid(), child = Guid.NewGuid(), nested = Guid.NewGuid();
    string primaryDirectory = Path.Combine(Path.GetTempPath(), "primary");
    string childDirectory = Path.Combine(Path.GetTempPath(), "child");
    using (UrdStore store = UrdStore.Open(database.Path)) {
      AssertSession(store.Sessions.CreatePrimary(primary, primaryDirectory), primary, SessionKind.Primary, null, null, primaryDirectory);
      Assert.Throws<ArgumentException>(() => store.Sessions.CreateSubagent(child, child, "call-1"));
      AssertSession(store.Sessions.CreateSubagent(child, primary, "call-1", childDirectory), child, SessionKind.Subagent, primary, "call-1", childDirectory);
      AssertSession(store.Sessions.CreateSubagent(nested, child, "call-2"), nested, SessionKind.Subagent, child, "call-2", null);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    AssertSession(Assert.IsType<SessionRecord>(reopened.Sessions.Read(primary)), primary, SessionKind.Primary, null, null, primaryDirectory);
    AssertSession(Assert.IsType<SessionRecord>(reopened.Sessions.Read(child)), child, SessionKind.Subagent, primary, "call-1", childDirectory);
    AssertSession(Assert.IsType<SessionRecord>(reopened.Sessions.Read(nested)), nested, SessionKind.Subagent, child, "call-2", null);
  }

  [Theory]
  [InlineData(60)]
  [InlineData(65)]
  public void PrimaryPagesExcludeInterleavedSubagents(int count) {
    using var database = new TemporaryDatabase();
    Guid[] primaryIds = Enumerable.Range(1, count).Select(Id).ToArray();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      foreach (Guid primary in primaryIds) {
        store.Sessions.CreatePrimary(primary);
        store.Sessions.CreateSubagent(Guid.NewGuid(), primary, "spawn");
      }
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    SessionIdPage first = reopened.Sessions.ListPrimaryIds(null);
    Assert.Equal(primaryIds.Reverse().Take(60), first.SessionIds);
    if (count == 60) {
      Assert.Null(first.NextCursor);
      return;
    }

    Assert.NotNull(first.NextCursor);
    SessionIdPage second = reopened.Sessions.ListPrimaryIds(first.NextCursor);
    Assert.Equal(primaryIds.Reverse().Skip(60), second.SessionIds);
    Assert.Null(second.NextCursor);
  }

  private static void AssertSession(SessionRecord record, Guid id, SessionKind kind, Guid? parent, string? call, string? directory) {
    Assert.Equal(id, record.Id);
    Assert.Equal(0, record.HistoryRevision);
    Assert.Equal(kind, record.Kind);
    Assert.Equal(parent, record.ParentSessionId);
    Assert.Equal(call, record.OwningCallId);
    Assert.Equal(directory, record.WorkingDirectory);
  }

  private static Guid Id(int value) => Guid.Parse($"{value:x8}-0000-0000-0000-000000000000");
}
