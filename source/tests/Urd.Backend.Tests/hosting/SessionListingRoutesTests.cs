using System.Net;
using System.Text;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

// Discovery returns bounded persisted identities across clients and restarts, without starting work.
public sealed class SessionListingRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

  [Fact]
  public async Task BoundedPagesDiscoverEmptyChatsFromAnotherClientAndAfterReopenWithoutModelWork() {
    using var database = new TemporaryDatabase();
    var model = new ScriptedChatClient(ScriptedResponses.Complete);
    var created = new List<Guid>();
    string cursor;
    List<StoredSessionRow> retained;
    await using (var server = new RunningServer(Options(database), createClient: () => model)) {
      Uri origin = await server.WaitForReadyAsync();
      SessionIdPage empty = await ReadPageAsync(server.Client);
      Assert.Empty(empty.SessionIds);
      Assert.Null(empty.NextCursor);
      Assert.Empty(StoredSessions.Read(database));
      for (int index = 0; index < 65; index++)
        created.Add(await CreateAsync(server.Client));
      retained = StoredSessions.Read(database);

      SessionIdPage first = await ReadPageAsync(server.Client);
      Assert.Equal(created.AsEnumerable().Reverse().Take(60), first.SessionIds);
      cursor = Assert.IsType<string>(first.NextCursor);
      using var other = new HttpClient(new SocketsHttpHandler { UseProxy = false }) {
        BaseAddress = origin,
        Timeout = TestTimeout
      };
      SessionIdPage independent = await ReadPageAsync(other);
      Assert.Equal(first.SessionIds, independent.SessionIds);
      Assert.Equal(cursor, independent.NextCursor);
      SessionIdPage tail = await ReadPageAsync(other, cursor);
      Assert.Equal(created.AsEnumerable().Reverse().Skip(60), tail.SessionIds);
      Assert.Null(tail.NextCursor);
      Assert.Equal(65, first.SessionIds.Concat(tail.SessionIds).Distinct().Count());
      SessionIdPage pastOldest = await ReadPageAsync(other, "1");
      Assert.Empty(pastOldest.SessionIds);
      Assert.Null(pastOldest.NextCursor);
      Assert.Equal(retained, StoredSessions.Read(database));
      Assert.Equal(0, model.Calls);
    }

    var reopenedModel = new ScriptedChatClient(ScriptedResponses.Complete);
    await using var reopened = new RunningServer(Options(database), createClient: () => reopenedModel);
    await reopened.WaitForReadyAsync();
    SessionIdPage resumed = await ReadPageAsync(reopened.Client, cursor);
    Assert.Equal(created.AsEnumerable().Reverse().Skip(60), resumed.SessionIds);
    Assert.Null(resumed.NextCursor);
    Assert.Equal(created.AsEnumerable().Reverse().Take(60), (await ReadPageAsync(reopened.Client)).SessionIds);
    Assert.Equal(retained, StoredSessions.Read(database));
    Assert.Empty(StoredInputs.Read(database));
    Assert.Empty(StoredRuns.Read(database));
    Assert.Empty(StoredRuns.ReadRequests(database));
    Assert.Equal(0, model.Calls);
    Assert.Equal(0, reopenedModel.Calls);
  }

  [Fact]
  public async Task InvalidCursorsBodiesAndForeignOrUnmarkedReadsCannotBypassThePrivateReadRules() {
    using var database = new TemporaryDatabase();
    var model = new ScriptedChatClient(ScriptedResponses.Complete);
    await using var server = new RunningServer(Options(database), createClient: () => model);
    Uri origin = await server.WaitForReadyAsync();
    foreach (string cursor in new[] { "", "invalid", "0", "-1", "9223372036854775808" }) {
      using HttpResponseMessage response = await RequestAsync(server.Client,
          new(HttpMethod.Get, $"/api/sessions?cursor={Uri.EscapeDataString(cursor)}"));
      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
      AssertPrivateResponse(response);
    }
    // Even an outlying but valid positive Int64 boundary is a successful empty read.
    SessionIdPage empty = await ReadPageAsync(server.Client, "9223372036854775807");
    Assert.Empty(empty.SessionIds);
    Assert.Null(empty.NextCursor);
    foreach (bool chunked in new[] { false, true }) {
      var request = new HttpRequestMessage(HttpMethod.Get, "/api/sessions") {
        Content = new StringContent("body")
      };
      if (chunked)
        request.Headers.TransferEncodingChunked = true;
      using HttpResponseMessage response = await RequestAsync(server.Client, request);
      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
      AssertPrivateResponse(response);
    }
    using (HttpResponseMessage unmarked = await RequestAsync(server.Client,
        new(HttpMethod.Get, "/api/sessions"), marker: false)) {
      Assert.Equal(HttpStatusCode.Forbidden, unmarked.StatusCode);
      AssertPrivateResponse(unmarked);
    }
    foreach (string suppliedOrigin in new[] { "null", "http://foreign.example" }) {
      var request = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
      request.Headers.Add("Origin", suppliedOrigin);
      using HttpResponseMessage response = await RequestAsync(server.Client, request);
      Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      AssertPrivateResponse(response);
    }
    var wrongHost = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
    wrongHost.Headers.Host = $"foreign.example:{origin.Port}";
    using (HttpResponseMessage response = await RequestAsync(server.Client, wrongHost)) {
      Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      AssertPrivateResponse(response);
    }
    var sameOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
    sameOrigin.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
    using (HttpResponseMessage response = await RequestAsync(server.Client, sameOrigin)) {
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      AssertPrivateResponse(response);
    }
    Assert.Empty(StoredSessions.Read(database));
    Assert.Empty(StoredInputs.Read(database));
    Assert.Empty(StoredRuns.Read(database));
    Assert.Equal(0, model.Calls);
  }

  [Fact]
  public async Task StorageFailureRemainsAServiceUnavailablePrivateResponse() {
    using var database = new TemporaryDatabase();
    var model = new ScriptedChatClient(ScriptedResponses.Complete);
    await using var server = new RunningServer(Options(database), createClient: () => model);
    await server.WaitForReadyAsync();
    database.Execute("DROP TABLE session_creation;");
    using HttpResponseMessage response = await RequestAsync(server.Client, new(HttpMethod.Get, "/api/sessions"));
    Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    AssertPrivateResponse(response);
    Assert.Empty(StoredSessions.Read(database));
    Assert.Equal(0, model.Calls);
  }

  private static ServerOptions Options(TemporaryDatabase database) => new(Path.GetDirectoryName(database.Path)!, 0);

  private static async Task<HttpResponseMessage> RequestAsync(HttpClient client, HttpRequestMessage request,
      bool marker = true) {
    using (request) {
      if (marker)
        request.Headers.Add("X-Urd-Client", "browser");
      return await client.SendAsync(request);
    }
  }

  private static void AssertPrivateResponse(HttpResponseMessage response) {
    Assert.True(response.Headers.CacheControl?.NoStore);
    Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
  }

  private static async Task<SessionIdPage> ReadPageAsync(HttpClient client, string? cursor = null) {
    string path = cursor is null ? "/api/sessions" : $"/api/sessions?cursor={Uri.EscapeDataString(cursor)}";
    using HttpResponseMessage response = await RequestAsync(client, new(HttpMethod.Get, path));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertPrivateResponse(response);
    Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Assert.Equal(new[] { "nextCursor", "sessionIds" },
        body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    return Assert.IsType<SessionIdPage>(body.RootElement.Deserialize<SessionIdPage>(json));
  }

  private static async Task<Guid> CreateAsync(HttpClient client) {
    using HttpResponseMessage response = await RequestAsync(client, new(HttpMethod.Post, "/api/sessions") {
      Content = new StringContent("{}", Encoding.UTF8, "application/json")
    });
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    AssertPrivateResponse(response);
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return body.RootElement.GetProperty("sessionId").GetGuid();
  }
}
