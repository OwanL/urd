using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

public sealed class BitwardenRoutesTests {
  [Fact]
  public async Task UnavailableExtensionAndMissingSessionsDoNotOpenOrClosePages() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid primary = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid child = store.Sessions.CreateSubagent(Guid.NewGuid(), primary, "child").Id;
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("Must not run."));
    await using var server = await ViewerServer.StartAsync(manager);

    using (HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get, $"/api/sessions/{primary:D}/browser")) {
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      AssertPrivateResponse(response);
      using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      Assert.False(body.RootElement.GetProperty("bitwarden").GetBoolean());
      Assert.Empty(body.RootElement.GetProperty("pages").EnumerateArray());
    }

    foreach (Guid id in new[] { primary, child, Guid.NewGuid() }) {
      string path = $"/api/sessions/{id:D}/browser/bitwarden";
      using HttpResponseMessage open = await RequestAsync(server.Client, HttpMethod.Post, path);
      Assert.Equal(HttpStatusCode.NotFound, open.StatusCode);
      AssertPrivateResponse(open);
      using HttpResponseMessage close = await RequestAsync(server.Client, HttpMethod.Delete, $"{path}/{Guid.NewGuid():D}");
      Assert.Equal(HttpStatusCode.NotFound, close.StatusCode);
      AssertPrivateResponse(close);
    }

    foreach (HttpMethod method in new[] { HttpMethod.Post, HttpMethod.Delete }) {
      string path = $"/api/sessions/{primary:D}/browser/bitwarden";
      if (method == HttpMethod.Delete)
        path += $"/{Guid.NewGuid():D}";
      using HttpResponseMessage response = await RequestAsync(server.Client, method, path, marker: false);
      Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      AssertPrivateResponse(response);
    }
  }

  [Fact]
  public async Task LanClientsCannotOpenOrCloseBitwarden() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid primary = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("Must not run."));
    await using var server = await ViewerServer.StartAsync(manager);

    foreach (HttpMethod method in new[] { HttpMethod.Post, HttpMethod.Delete }) {
      string path = $"/api/sessions/{primary:D}/browser/bitwarden";
      if (method == HttpMethod.Delete)
        path += $"/{Guid.NewGuid():D}";
      using var request = new HttpRequestMessage(method, path);
      request.Headers.Host = $"192.168.1.3:{server.Origin.Port}";
      request.Headers.Add("Origin", $"http://192.168.1.3:{server.Origin.Port}");
      request.Headers.Add("X-Urd-Client", "browser");
      using HttpResponseMessage response = await server.Client.SendAsync(request);
      Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      AssertPrivateResponse(response);
    }
  }

  private sealed class ViewerServer(WebApplication app, HttpClient client, Uri origin) : IAsyncDisposable {
    public HttpClient Client { get; } = client;
    public Uri Origin { get; } = origin;

    public static async Task<ViewerServer> StartAsync(SessionManager manager) {
      Uri? origin = null;
      var lan = new LanAccess();
      lan.Replace([IPAddress.Parse("192.168.1.3")]);
      var builder = WebApplication.CreateBuilder();
      builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
      WebApplication app = builder.Build();
      app.Use(next => new LocalBrowserGate(next, () => origin, lan).InvokeAsync);
      app.UseWebSockets();
      app.MapBrowserViewer(manager, () => origin);
      await app.StartAsync().WaitAsync(TestTimeout);
      origin = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
      var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = origin, Timeout = TestTimeout };
      return new ViewerServer(app, client, origin);
    }

    public async ValueTask DisposeAsync() {
      Client.Dispose();
      await app.DisposeAsync();
    }
  }
}
