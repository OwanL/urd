using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Sessions;

public sealed class SessionIdentityTests {
  [Fact]
  public async Task ManagerReturnsCanonicalHandleAndMissingLookupNeverInserts() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(_ =>
        throw new InvalidOperationException("Identity operations must not start a request."));
    var manager = new SessionManager(store, _ => client);
    var createdNotices = new List<Guid>();
    manager.SessionCreated += createdNotices.Add;
    Assert.NotEqual(Guid.Empty, manager.Boot);
    Guid missing = Guid.NewGuid();

    await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.GetPrimaryAsync(missing));
    Assert.Null(store.Sessions.Read(missing));

    PrimarySession created = await manager.CreatePrimaryAsync();
    Assert.Same(created, await manager.GetPrimaryAsync(created.Id));
    Assert.Equal(new[] { created.Id }, createdNotices);
    Assert.Single(StoredSessions.Read(database));
    Assert.Equal(0, client.Calls);
  }
}
