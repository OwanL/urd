using System.Text.Json;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

public sealed class SessionEventQueueTests {
  [Fact]
  public async Task CoalescesTheLatestValuePerSessionAndEventType() {
    var queue = new SessionEventQueue();
    Guid session = Guid.NewGuid();
    queue.Enqueue(Message("status", session, 1));
    queue.Enqueue(Message("change", session, 1));
    queue.Enqueue(Message("change", session, 2));
    queue.Enqueue(Message("status", session, 2));

    SessionSseRead status = await queue.ReadNextAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
    SessionSseRead change = await queue.ReadNextAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
    Assert.Equal("status", status.Event?.Name);
    Assert.Equal(2, ReadVersion(status.Event!.Data));
    Assert.Equal("change", change.Event?.Name);
    Assert.Equal(2, ReadVersion(change.Event!.Data));
  }

  [Fact]
  public async Task StalledReaderKeepsOnlyTheLatestNoticePerSessionAndEventType() {
    const int sessionCount = 50;
    const int updateCount = 64;
    var queue = new SessionEventQueue();
    Guid[] sessions = Enumerable.Range(0, sessionCount).Select(_ => Guid.NewGuid()).ToArray();
    HashSet<Guid> watched = sessions.Where((_, index) => index % 2 == 0).ToHashSet();

    for (int version = 1; version <= updateCount; version++) {
      foreach (Guid session in sessions) {
        Assert.True(queue.Enqueue(Message("status", session, version)));
        if (watched.Contains(session))
          Assert.True(queue.Enqueue(Message("change", session, version)));
      }
    }

    Assert.False(queue.IsClosed);
    var read = new List<SessionSseEvent>();
    for (int index = 0; index < sessionCount + watched.Count; index++) {
      SessionSseRead next = await queue.ReadNextAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
      Assert.False(next.Closed);
      Assert.False(next.Heartbeat);
      read.Add(Assert.IsType<SessionSseEvent>(next.Event));
    }

    Assert.Equal(sessionCount, read.Count(message => message.Name == "status"));
    Assert.Equal(watched.Count, read.Count(message => message.Name == "change"));
    Assert.True(sessions.Order().SequenceEqual(read.Where(message => message.Name == "status")
        .Select(message => message.Session!.Value).Order()));
    Assert.True(watched.Order().SequenceEqual(read.Where(message => message.Name == "change")
        .Select(message => message.Session!.Value).Order()));
    Assert.All(read, message => Assert.Equal(updateCount, ReadVersion(message.Data)));
    Assert.True((await queue.ReadNextAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None)).Heartbeat);
  }

  [Fact]
  public async Task ProgressCoalescesPerToolCallAndIsDiscardedWhenTheSessionIsUnwatched() {
    var queue = new SessionEventQueue();
    Guid session = Guid.NewGuid();
    queue.Enqueue(Progress(session, "first", "old"));
    queue.Enqueue(Progress(session, "second", "other"));
    queue.Enqueue(Progress(session, "first", "latest"));

    SessionSseRead first = await queue.ReadNextAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
    SessionSseRead second = await queue.ReadNextAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
    Assert.Equal("latest", ReadText(first.Event!.Data));
    Assert.Equal("other", ReadText(second.Event!.Data));

    queue.Enqueue(Progress(session, "first", "stale"));
    queue.RemoveSessionUpdates(session);
    Assert.True((await queue.ReadNextAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None)).Heartbeat);
  }

  [Fact]
  public async Task ProgressOverflowDropsOldTransientEventsWithoutClosingThePage() {
    var queue = new SessionEventQueue();
    Guid session = Guid.NewGuid();
    for (int index = 0; index <= SessionEventQueue.MaxProgressEvents; index++) {
      string key = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
      Assert.True(queue.Enqueue(Progress(session, key, key)));
    }

    Assert.False(queue.IsClosed);
    var received = new List<SessionSseEvent>();
    for (int index = 0; index < SessionEventQueue.MaxProgressEvents; index++)
      received.Add(Assert.IsType<SessionSseEvent>((await queue.ReadNextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Event));
    Assert.DoesNotContain(received, message => ReadText(message.Data) == "0");
    Assert.Equal(SessionEventQueue.MaxProgressEvents, received.Count);
  }

  [Fact]
  public async Task RemovingQueuedUpdatesLeavesHeartbeatWaitsUsable() {
    var queue = new SessionEventQueue();
    Guid session = Guid.NewGuid();
    queue.Enqueue(Message("change", session, 1));
    queue.Enqueue(Message("browser", session, 1));
    queue.RemoveSessionUpdates(session);

    SessionSseRead heartbeat = await queue.ReadNextAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);
    Assert.True(heartbeat.Heartbeat);
    queue.Enqueue(Message("status", session, 2));
    SessionSseRead status = await queue.ReadNextAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
    Assert.Equal("status", status.Event?.Name);
  }

  [Fact]
  public async Task CreationOverflowClosesTheQueueInsteadOfDroppingAnIdentity() {
    var queue = new SessionEventQueue();
    for (int index = 0; index < SessionEventQueue.MaxCreations; index++) {
      Guid session = Guid.NewGuid();
      Assert.True(queue.Enqueue(new SessionSseEvent("session-created", session, new { session })));
    }

    Assert.False(queue.Enqueue(new SessionSseEvent("session-created", Guid.NewGuid(), new { session = Guid.NewGuid() })));
    SessionSseRead disconnected = await queue.ReadNextAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
    Assert.True(disconnected.Closed);
  }

  private static SessionSseEvent Message(string name, Guid session, long version) =>
      new(name, session, new { session, version });

  private static SessionSseEvent Progress(Guid session, string key, string text) =>
      new("tool-progress", session, new { text }, key);

  private static string ReadText(object data) =>
      JsonDocument.Parse(JsonSerializer.Serialize(data)).RootElement.GetProperty("text").GetString()!;

  private static long ReadVersion(object data) =>
      JsonDocument.Parse(JsonSerializer.Serialize(data)).RootElement.GetProperty("version").GetInt64();
}
