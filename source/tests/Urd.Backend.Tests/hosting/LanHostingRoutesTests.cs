using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Urd.Server;
using Urd.Backend.Tests.Support;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class LanHostingRoutesTests {
  private const string Path = "/api/hosting/lan";

  [Fact]
  public async Task StatusAndDisableReturnLocalLanState() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();

    using (HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get, Path)) {
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      AssertPrivateResponse(response);
      AssertDisabled(await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    using (HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Post, Path,
        JsonBody("{\"enabled\":false}"))) {
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      AssertPrivateResponse(response);
      AssertDisabled(await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    using HttpResponseMessage current = await RequestAsync(server.Client, HttpMethod.Get, Path);
    AssertDisabled(await current.Content.ReadFromJsonAsync<JsonElement>());
  }

  [Theory]
  [InlineData("{}")]
  [InlineData("{\"enabled\":null}")]
  [InlineData("{\"enabled\":\"true\"}")]
  [InlineData("{\"enabled\":1}")]
  [InlineData("[]")]
  public async Task InvalidEnabledDoesNotChangeState(string body) {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();

    using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Post, Path, JsonBody(body));
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    AssertPrivateResponse(response);
    using HttpResponseMessage current = await RequestAsync(server.Client, HttpMethod.Get, Path);
    Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    AssertDisabled(await current.Content.ReadFromJsonAsync<JsonElement>());
  }

  [Fact]
  public void ManagementRequiresLoopbackPeerAndCanonicalAuthority() {
    var origin = new Uri("http://127.0.0.1:1234");
    var http = new DefaultHttpContext();
    http.Request.Host = new HostString("127.0.0.1", 1234);
    http.Connection.RemoteIpAddress = IPAddress.Loopback;
    Assert.True(LanHostingRoutes.IsLocal(http, origin));
    http.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.2");
    Assert.False(LanHostingRoutes.IsLocal(http, origin));
    http.Connection.RemoteIpAddress = IPAddress.Loopback;
    http.Request.Host = new HostString("192.168.1.3", 1234);
    Assert.False(LanHostingRoutes.IsLocal(http, origin));
    http.Request.Host = new HostString("127.0.0.1", 1234);
    http.Request.Headers.Origin = "http://192.168.1.3:1234";
    Assert.False(LanHostingRoutes.IsLocal(http, origin));
  }

  [Fact]
  public async Task RestoreFailureRetainsIntentAndCanBeCleared() {
    bool saved = true;
    await using var hosting = new LanHosting(new LanAccess(), 1234, () => [],
        _ => throw new InvalidOperationException(), value => saved = value, remembered: true);
    await hosting.RestoreAsync();
    LanHostingStatus failed = await hosting.StatusAsync();
    Assert.False(failed.Enabled);
    Assert.True(failed.Remembered);
    Assert.True(saved);
    Assert.NotNull(failed.StartupError);
    LanHostingStatus cleared = await hosting.ChangeAsync(false);
    Assert.False(cleared.Remembered);
    Assert.Null(cleared.StartupError);
    Assert.False(saved);
  }

  private static void AssertDisabled(JsonElement status) {
    Assert.False(status.GetProperty("enabled").GetBoolean());
    Assert.False(status.GetProperty("remembered").GetBoolean());
    Assert.Empty(status.GetProperty("origins").EnumerateArray());
    Assert.Equal(JsonValueKind.Null, status.GetProperty("startupError").ValueKind);
  }
}
