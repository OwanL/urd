using System.Net;
using System.Net.Sockets;
using Urd.Backend.Tests.Support;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

// A real startup callback failure still closes the listener and releases the directory lease.
public sealed class ServerLifecycleTests {
  [Fact]
  public async Task ThrowingReadyCallbackPropagatesAfterListenerAndLeaseAreReleased() {
    var database = new TemporaryDatabase("urd callback failure data");
    var expected = new InvalidOperationException("ready callback failed");
    Uri? published = null;
    var server = new RunningServer(Options(database), origin => {
      published = origin;
      throw expected;
    });
    RunningServer? restarted = null;
    try {
      InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(server.WaitForCompletionAsync);
      Assert.Same(expected, actual);
      Assert.NotNull(published);
      await AssertListenerClosedAsync(published);
      AssertLeaseFree(database);

      restarted = new RunningServer(Options(database));
      await restarted.WaitForReadyAsync();
      using HttpResponseMessage health = await restarted.Client.GetAsync("/health");
      Assert.Equal(HttpStatusCode.NoContent, health.StatusCode);
    } finally {
      await Task.WhenAll(server.DisposeAsync().AsTask(), restarted?.DisposeAsync().AsTask() ?? Task.CompletedTask);
      database.Dispose();
    }
  }

  private static ServerOptions Options(TemporaryDatabase database) =>
      new(Path.GetDirectoryName(database.Path)!, 0);

  private static async Task AssertListenerClosedAsync(Uri origin) {
    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TestTimeout };
    await Assert.ThrowsAsync<HttpRequestException>(async () => {
      using HttpResponseMessage response = await client.GetAsync(new Uri(origin, "/health"));
    });
  }

  private static void AssertLeaseFree(TemporaryDatabase database) {
    using var lease = new FileStream(Path.Combine(Path.GetDirectoryName(database.Path)!, "urd.lock"),
        FileMode.Open, FileAccess.ReadWrite, FileShare.None);
  }
}
