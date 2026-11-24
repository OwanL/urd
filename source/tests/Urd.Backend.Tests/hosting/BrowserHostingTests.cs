using System.Text.Json;
using Urd.Backend.Tests.Support;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

public sealed class BrowserHostingTests {
  [Fact]
  public async Task ResourcesOfferBrowserWithoutLaunchingIt() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsForIsolatedWorkspace(database));
    await server.WaitForReadyAsync();
    using var response = await HttpTest.RequestAsync(server.Client, HttpMethod.Get, "/api/preferences/resources");
    response.EnsureSuccessStatusCode();
    using var resources = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    string?[] tools = resources.RootElement.GetProperty("tools").EnumerateArray()
        .Select(tool => tool.GetProperty("name").GetString()).ToArray();
    Assert.Contains("browser", tools);
    Assert.DoesNotContain("playwright", tools);
  }
}
