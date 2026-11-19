using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

public sealed class SessionEventHubTests {
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
