using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

public sealed class SessionEventHubTests {
  [Fact]
  public async Task WatchedRegistrationSettlingBeforeFirstReadReportsTheFinalSnapshotVersion() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(Complete);
    var sessions = new SessionManager(store, _ => client);
    PrimarySession session = await sessions.CreatePrimaryAsync();
    using var hub = new SessionEventHub(sessions);
    Assert.True(hub.TryCreate(out SessionEventPage page));
    using (page) {
      await page.UpdateAsync(sessions, [session.Id], [session.Id], CancellationToken.None);
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, "settle before reading", TestLlmSelection.Default));
      await session.WaitForSettlementAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout);

      SessionSseEvent status = Assert.IsType<SessionSseEvent>(
          (await page.ReadNextAsync(CancellationToken.None).WaitAsync(TestTimeout)).Event);
      SessionSseEvent change = Assert.IsType<SessionSseEvent>(
          (await page.ReadNextAsync(CancellationToken.None).WaitAsync(TestTimeout)).Event);
      Assert.Equal("status", status.Name);
      Assert.Equal("idle", ReadString(status.Data, "status"));
      Assert.Equal("change", change.Name);
      Assert.Equal(session.Id, status.Session);
      Assert.Equal(session.Id, change.Session);
      Assert.True(ReadVersion(status.Data) > 0);
      Assert.Equal(ReadVersion(status.Data), ReadVersion(change.Data));
    }
  }

  [Fact]
  public async Task UpdatingOpenSessionsRepeatsWatchedHandshakeForRetainedRegistrations() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(Complete);
    var sessions = new SessionManager(store, _ => client);
    PrimarySession watched = await sessions.CreatePrimaryAsync();
    PrimarySession openedLater = await sessions.CreatePrimaryAsync();
    using var hub = new SessionEventHub(sessions);
    Assert.True(hub.TryCreate(out SessionEventPage page));
    using (page) {
      await page.UpdateAsync(sessions, [watched.Id], [watched.Id], CancellationToken.None);
      SessionSseEvent[] initial = await ReadEventsAsync(page, 2);
      Assert.Equal(new[] { "status", "change" }, initial.Select(message => message.Name));

      UserMessageReceipt receipt = await watched.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, "settle before update", TestLlmSelection.Default));
      await watched.WaitForSettlementAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout);
      SessionSseEvent[] settled = await ReadEventsAsync(page, 2);
      Assert.Equal(new[] { "status", "change" }, settled.Select(message => message.Name));
      Assert.Equal("idle", ReadString(settled[0].Data, "status"));

      await page.UpdateAsync(sessions, [watched.Id, openedLater.Id], [watched.Id], CancellationToken.None);
      SessionSseEvent[] handshake = await ReadEventsAsync(page, 3);
      SessionSseEvent[] statuses = handshake.Where(message => message.Name == "status").ToArray();
      SessionSseEvent change = Assert.Single(handshake, message => message.Name == "change");
      Assert.True(new[] { watched.Id, openedLater.Id }.Order()
          .SequenceEqual(statuses.Select(message => message.Session!.Value).Order()));
      Assert.Equal(watched.Id, change.Session);
      SessionSseEvent watchedStatus = Assert.Single(statuses, message => message.Session == watched.Id);
      Assert.Equal("idle", ReadString(watchedStatus.Data, "status"));
      Assert.Equal(ReadVersion(watchedStatus.Data), ReadVersion(change.Data));
    }
  }

  [Fact]
  public async Task StalledPageCoalescesFiftyLiveSessionsAndOnlyWatchedContent() {
    const int sessionCount = 50;
    var allStreaming = Signal();
    var release = Signal();
    int streaming = 0;
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken cancellation) {
      yield return Text("live");
      if (Interlocked.Increment(ref streaming) == sessionCount)
        allStreaming.TrySetResult();
      await release.Task.WaitAsync(cancellation);
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(Response);
    var sessions = new SessionManager(store, _ => client);
    PrimarySession[] opened = await Task.WhenAll(Enumerable.Range(0, sessionCount)
        .Select(_ => sessions.CreatePrimaryAsync()));
    Guid[] ids = opened.Select(session => session.Id).ToArray();
    Guid[] watched = ids.Where((_, index) => index % 2 == 0).ToArray();
    using var hub = new SessionEventHub(sessions);
    try {
      Assert.True(hub.TryCreate(out SessionEventPage page));
      using (page) {
        await page.UpdateAsync(sessions, ids, watched, CancellationToken.None);
        SessionSseEvent[] initial = await ReadEventsAsync(page, sessionCount + watched.Length);
        Assert.Equal(sessionCount, initial.Count(message => message.Name == "status"));
        Assert.Equal(watched.Length, initial.Count(message => message.Name == "change"));
        Assert.True(ids.Order().SequenceEqual(initial.Where(message => message.Name == "status")
            .Select(message => message.Session!.Value).Order()));
        Assert.True(watched.Order().SequenceEqual(initial.Where(message => message.Name == "change")
            .Select(message => message.Session!.Value).Order()));

        UserMessageReceipt[] receipts = await Task.WhenAll(opened.Select((session, index) =>
            session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, $"message {index}", TestLlmSelection.Default))));
        Assert.All(receipts, receipt => Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance));
        await allStreaming.Task.WaitAsync(TestTimeout);
        Assert.Equal(sessionCount, client.Calls);

        release.TrySetResult();
        SessionExecutionResult[] settlements = await Task.WhenAll(opened.Select((session, index) =>
            session.WaitForSettlementAsync(Assert.IsType<Guid>(receipts[index].ExecutionId)).WaitAsync(TestTimeout)));
        Assert.All(settlements, result => Assert.Equal(ExecutionOutcome.Completed, result.Outcome));

        SessionSseEvent[] pending = await ReadEventsAsync(page, sessionCount + watched.Length);
        Assert.Equal(sessionCount, pending.Count(message => message.Name == "status"));
        Assert.Equal(watched.Length, pending.Count(message => message.Name == "change"));
        Assert.True(ids.Order().SequenceEqual(pending.Where(message => message.Name == "status")
            .Select(message => message.Session!.Value).Order()));
        Assert.True(watched.Order().SequenceEqual(pending.Where(message => message.Name == "change")
            .Select(message => message.Session!.Value).Order()));
        foreach (SessionSseEvent status in pending.Where(message => message.Name == "status")) {
          Assert.Equal("idle", ReadString(status.Data, "status"));
          Assert.True(ReadVersion(status.Data) > 1);
        }
        foreach (SessionSseEvent change in pending.Where(message => message.Name == "change")) {
          SessionSseEvent status = Assert.Single(pending,
              message => message.Name == "status" && message.Session == change.Session);
          Assert.Equal(ReadVersion(status.Data), ReadVersion(change.Data));
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => page.ReadNextAsync(cancellation.Token));
      }
    } finally {
      release.TrySetResult();
      await sessions.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public void RefusesPagesPastTheProcessLimit() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var sessions = new SessionManager(store, _ => new FakeChatClient());
    using var hub = new SessionEventHub(sessions);
    var pages = new List<SessionEventPage>();

    for (int index = 0; index < SessionEventHub.MaxPages; index++) {
      Assert.True(hub.TryCreate(out SessionEventPage page));
      pages.Add(page);
    }

    Assert.False(hub.TryCreate(out _));
    foreach (SessionEventPage page in pages)
      page.Dispose();
    Assert.True(hub.TryCreate(out SessionEventPage afterDisconnect));
    afterDisconnect.Dispose();
  }

  private static async Task<SessionSseEvent[]> ReadEventsAsync(SessionEventPage page, int count) {
    var events = new SessionSseEvent[count];
    for (int index = 0; index < count; index++) {
      SessionSseRead next = await page.ReadNextAsync(CancellationToken.None).WaitAsync(TestTimeout);
      Assert.False(next.Closed);
      Assert.False(next.Heartbeat);
      events[index] = Assert.IsType<SessionSseEvent>(next.Event);
    }
    return events;
  }

  private static long ReadVersion(object data) {
    using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(data));
    return document.RootElement.GetProperty("version").GetInt64();
  }

  private static string? ReadString(object data, string property) {
    using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(data));
    return document.RootElement.GetProperty(property).GetString();
  }
}
