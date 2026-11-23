using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class SessionSnoozeRoutesTests {
  [Fact]
  public async Task ListsSnoozedChatsAndRestoresOnlyTheObservedDeadline() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();

    using HttpResponseMessage created = await RequestAsync(server.Client, HttpMethod.Post, "/api/sessions", JsonBody("{}"));
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    Guid id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
    string change = $"/api/sessions/{id:D}/snooze";
    DateTimeOffset until = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddHours(1);

    await AssertChangeAsync(change, new { until = until.AddHours(-2) }, HttpStatusCode.BadRequest);
    await AssertChangeAsync(change, new { until, expectedUntil = until }, HttpStatusCode.BadRequest);
    await AssertChangeAsync($"/api/sessions/{Guid.NewGuid():D}/snooze", new { until }, HttpStatusCode.NotFound);
    await AssertChangeAsync(change, new { until }, HttpStatusCode.NoContent);
    SnoozedSessions snoozed = await ReadSnoozedAsync();
    Assert.True(snoozed.Now < until);
    Assert.Equal(new SnoozedSession(id, until, null), Assert.Single(snoozed.Sessions));

    await AssertChangeAsync(change, new { expectedUntil = until.AddMinutes(-1) }, HttpStatusCode.Conflict);
    Assert.Single((await ReadSnoozedAsync()).Sessions);
    await AssertChangeAsync(change, new { expectedUntil = until }, HttpStatusCode.NoContent);
    Assert.Empty((await ReadSnoozedAsync()).Sessions);

    async Task AssertChangeAsync(string path, object body, HttpStatusCode expected) {
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Post, path,
          JsonBody(JsonSerializer.Serialize(body)));
      Assert.Equal(expected, response.StatusCode);
      AssertPrivateResponse(response);
    }

    async Task<SnoozedSessions> ReadSnoozedAsync() {
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get, "/api/sessions/snoozed");
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      AssertPrivateResponse(response);
      return (await response.Content.ReadFromJsonAsync<SnoozedSessions>())!;
    }
  }
}
