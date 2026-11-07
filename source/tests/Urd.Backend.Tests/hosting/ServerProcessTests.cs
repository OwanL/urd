using System.Net;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

// Exercises the real command line and child-only environment, without changing this test process's configuration.
public sealed class ServerProcessTests {
  [Fact]
  public async Task CliReportsOneActualOriginAndIgnoresAmbientListenerConfiguration() {
    using var database = new TemporaryDatabase("urd CLI data with spaces");
    await using var server = new ServerProcess(Arguments(database), new Dictionary<string, string> {
      ["ASPNETCORE_URLS"] = "http://192.0.2.1:64321",
      ["DOTNET_URLS"] = "http://192.0.2.2:64322",
      ["Kestrel__Endpoints__Unexpected__Url"] = "http://192.0.2.3:64323",
      ["ASPNETCORE_ENVIRONMENT"] = "Development"
    });
    string? line = await server.Output.ReadLineAsync().WaitAsync(TestTimeout);
    Assert.NotNull(line);
    using JsonDocument ready = JsonDocument.Parse(line);
    Assert.Equal("urd-ready", ready.RootElement.GetProperty("event").GetString());
    var origin = new Uri(ready.RootElement.GetProperty("origin").GetString()!);
    Assert.Equal("http", origin.Scheme);
    Assert.Equal("127.0.0.1", origin.Host);
    Assert.InRange(origin.Port, 1, 65535);
    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TestTimeout };
    using HttpResponseMessage health = await client.GetAsync(new Uri(origin, "/health"));
    Assert.Equal(HttpStatusCode.NoContent, health.StatusCode);
    Assert.Empty(await health.Content.ReadAsByteArrayAsync());
    Assert.Empty(StoredSessions.Read(database));

    // This verifies the process protocol, not graceful shutdown (covered in-process with a real stop signal).
    await server.TerminateAsync();
    Assert.Empty(await server.Output.ReadToEndAsync().WaitAsync(TestTimeout));
  }

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
    Assert.Empty(StoredSessions.Read(database));
  }

  [Fact]
  public async Task InvalidArgumentsExitWithoutReadinessOrCreatingData() {
    using var database = new TemporaryDatabase();
    string unusedRoot = Path.Combine(Path.GetDirectoryName(database.Path)!, "not created");
    await using var server = new ServerProcess(["--data-root", unusedRoot, "--port", "65536"]);

    Assert.Equal(2, await server.WaitForExitAsync());
    Assert.Empty(await server.Output.ReadToEndAsync().WaitAsync(TestTimeout));
    Assert.Contains("Usage:", await server.ReadErrorsAsync(), StringComparison.Ordinal);
    Assert.False(Directory.Exists(unusedRoot));
  }

  private static string[] Arguments(TemporaryDatabase database) =>
      ["--data-root", Path.GetDirectoryName(database.Path)!, "--port", "0"];
}
