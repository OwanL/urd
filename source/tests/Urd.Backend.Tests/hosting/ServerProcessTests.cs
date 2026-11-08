using System.Net;
using Urd.Backend.Tests.Support;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

public sealed class ServerProcessTests {
  [Fact]
  public async Task AnotherProcessCannotOpenTheRunningServersDataDirectory() {
    using var database = new TemporaryDatabase("urd exclusive process data");
    await using var owner = new RunningServer(new ServerOptions(Path.GetDirectoryName(database.Path)!, 0));
    await owner.WaitForReadyAsync();
    await using var competing = new ServerProcess(Arguments(database));

    Assert.Null(await competing.Output.ReadLineAsync().WaitAsync(TestTimeout));
    Assert.Equal(1, await competing.WaitForExitAsync());
    Assert.Empty(await competing.Output.ReadToEndAsync().WaitAsync(TestTimeout));
    Assert.Contains("urd.lock", await competing.ReadErrorsAsync(), StringComparison.Ordinal);
    using HttpResponseMessage health = await owner.Client.GetAsync("/health");
    Assert.Equal(HttpStatusCode.NoContent, health.StatusCode);
  }

  private static string[] Arguments(TemporaryDatabase database) =>
      ["--data-root", Path.GetDirectoryName(database.Path)!, "--port", "0"];
}
