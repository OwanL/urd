using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

public sealed class SessionEventHubTests {
  [Fact]
  public async Task AttentionIsLiveOnlyAndCoalescesPerKind() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    await using var sessions = new SessionManager(store, _ => new FakeChatClient());
    using var hub = new SessionEventHub(sessions);
    sessions.NotifyAttention(AttentionKind.Question);
    Assert.True(hub.TryCreate(out SessionEventPage page));
    using (page) {
      sessions.NotifyAttention(AttentionKind.Question);
      sessions.NotifyAttention(AttentionKind.Question);
      sessions.NotifyAttention(AttentionKind.Completion);
      foreach (string kind in new[] { "question", "completion" }) {
        SessionSseEvent message = Assert.IsType<SessionSseEvent>(
            (await page.ReadNextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))).Event);
        Assert.Equal("attention", message.Name);
        Assert.Null(message.Session);
        Assert.Equal($"{{\"kind\":\"{kind}\"}}", JsonSerializer.Serialize(message.Data));
      }
      using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => page.ReadNextAsync(timeout.Token));
    }
    Assert.True(hub.TryCreate(out SessionEventPage reconnected));
    using (reconnected) {
      using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconnected.ReadNextAsync(timeout.Token));
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
}
