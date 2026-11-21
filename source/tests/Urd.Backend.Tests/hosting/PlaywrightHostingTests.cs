using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

public sealed class PlaywrightHostingTests {
  [Fact]
  public void LocalOriginSettingsAllowExactLoopbackOrigins() {
    IReadOnlyList<Uri> origins = PlaywrightConfig.ParseLocalOrigins("http://localhost:3000/, https://[::1]:3443");
    Assert.Equal(new[] { "http://localhost:3000", "https://[::1]:3443" },
        origins.Select(uri => uri.GetLeftPart(UriPartial.Authority)));
  }

  [Theory]
  [InlineData("http://localhost:3000/path")]
  [InlineData("https://public.example")]
  [InlineData("http://localhost?token=fake")]
  [InlineData("file:///tmp")]
  public void LocalOriginSettingsRejectPathsAndNonLoopback(string origin) =>
      Assert.Throws<ArgumentException>(() => PlaywrightConfig.ParseLocalOrigins(origin));

  [Fact]
  public async Task DisabledRoutesRequireBrowserMarkerAndRejectStaleActions() {
    var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
    builder.WebHost.UseKestrelCore().UseUrls("http://127.0.0.1:0");
    builder.Services.AddRouting();
    await using WebApplication app = builder.Build();
    Uri? origin = null;
    app.Use(next => new LocalBrowserGate(next, () => origin).InvokeAsync);
    // Disabled routes must not need a session manager or start a browser.
    app.MapSessionBrowserSignInRoutes(null!, enabled: false);
    await app.StartAsync();
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
    origin = new Uri(Assert.Single(addresses.Addresses));
    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = origin };
    string route = $"/api/sessions/{Guid.NewGuid()}/browser-sign-in";

    using (HttpResponseMessage rejected = await client.GetAsync(route)) {
      Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
      Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
    }

    client.DefaultRequestHeaders.Add("X-Urd-Client", "browser");
    using (HttpResponseMessage response = await client.GetAsync(route)) {
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
      using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      JsonProperty property = Assert.Single(json.RootElement.EnumerateObject());
      Assert.Equal("handoff", property.Name);
      Assert.Equal(JsonValueKind.Null, property.Value.ValueKind);
    }

    foreach (string action in new[] { "open", "continue", "cancel" }) {
      using HttpResponseMessage response = await client.PostAsJsonAsync($"{route}/{Guid.NewGuid()}/{action}", new { });
      Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
  }
}
