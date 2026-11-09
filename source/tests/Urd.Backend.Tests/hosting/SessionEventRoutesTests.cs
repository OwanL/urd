using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class SessionEventRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task StreamAnnouncesCreationAndRegistrationWithoutReconnect() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();

    using HttpResponseMessage stream = await OpenStreamAsync(server.Client);
    Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
    AssertPrivateResponse(stream);
    Assert.Equal("identity", stream.Content.Headers.ContentEncoding.Single());
    Assert.Equal("no", stream.Headers.GetValues("X-Accel-Buffering").Single());
    using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());

    JsonElement connected = await ReadEventAsync(reader, "connected");
    Guid streamId = connected.GetProperty("stream").GetGuid();
    Assert.NotEqual(Guid.Empty, connected.GetProperty("boot").GetGuid());

    using HttpResponseMessage created = await RequestAsync(server.Client, HttpMethod.Post, "/api/sessions", JsonBody("{}"));
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    Guid sessionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
    JsonElement announced = await ReadEventAsync(reader, "session-created");
    Assert.Equal(sessionId, announced.GetProperty("session").GetGuid());
    Assert.Single(announced.EnumerateObject());

    using HttpResponseMessage registered = await UpdateAsync(server.Client, streamId, [sessionId], [sessionId]);
    Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
    JsonElement status = await ReadEventAsync(reader, "status");
    Assert.Equal(sessionId, status.GetProperty("session").GetGuid());
    Assert.Equal("idle", status.GetProperty("status").GetString());
    Assert.Equal(connected.GetProperty("boot").GetGuid(), status.GetProperty("boot").GetGuid());
    JsonElement change = await ReadEventAsync(reader, "change");
    Assert.Equal(sessionId, change.GetProperty("session").GetGuid());
    Assert.Equal(status.GetProperty("version").GetInt64(), change.GetProperty("version").GetInt64());

    using HttpResponseMessage unwatched = await UpdateAsync(server.Client, streamId, [sessionId], []);
    Assert.Equal(HttpStatusCode.NoContent, unwatched.StatusCode);
    Assert.Equal("idle", (await ReadEventAsync(reader, "status")).GetProperty("status").GetString());
    await AssertNoEventAsync(reader, TimeSpan.FromMilliseconds(200));

    stream.Dispose();
    using HttpResponseMessage stale = await WaitForDisconnectedAsync(server.Client, streamId, sessionId);
    Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
  }

  [Fact]
  public async Task UnwatchedOpenSessionReportsStatusButNotContentChanges() {
    using var database = new TemporaryDatabase();
    var client = new ScriptedChatClient(WaitForever);
    await using var server = new RunningServer(Options(database), createClient: () => client);
    await server.WaitForReadyAsync();

    using HttpResponseMessage stream = await OpenStreamAsync(server.Client);
    using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());
    JsonElement connected = await ReadEventAsync(reader, "connected");
    Guid streamId = connected.GetProperty("stream").GetGuid();

    using HttpResponseMessage created = await RequestAsync(server.Client, HttpMethod.Post, "/api/sessions", JsonBody("{}"));
    Guid sessionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
    Assert.Equal("session-created", await ReadEventNameAsync(reader));
    using HttpResponseMessage registered = await UpdateAsync(server.Client, streamId, [sessionId], []);
    Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
    Assert.Equal("idle", (await ReadEventAsync(reader, "status")).GetProperty("status").GetString());

    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "wait", TestLlmSelection.Default);
    using HttpResponseMessage sent = await RequestAsync(server.Client, HttpMethod.Post,
        $"/api/sessions/{sessionId}/message-submissions", JsonContent.Create(submission, options: json));
    Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
    Assert.Equal("running", (await ReadEventAsync(reader, "status")).GetProperty("status").GetString());
    await AssertNoEventAsync(reader, TimeSpan.FromMilliseconds(200));
  }

  [Fact]
  public async Task EventRoutesKeepTheBrowserGateAndBoundRegistrationSets() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();

    using (HttpResponseMessage denied = await RequestAsync(server.Client, HttpMethod.Get, "/api/events", marker: false)) {
      Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
      AssertPrivateResponse(denied);
    }
    using (HttpResponseMessage denied = await RequestAsync(server.Client, HttpMethod.Post,
        $"/api/events/{Guid.NewGuid()}", JsonBody("{\"open\":[],\"watched\":[]}"), marker: false)) {
      Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
      AssertPrivateResponse(denied);
    }

    using HttpResponseMessage stream = await OpenStreamAsync(server.Client);
    using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());
    Guid streamId = (await ReadEventAsync(reader, "connected")).GetProperty("stream").GetGuid();
    string tooMany = JsonSerializer.Serialize(new {
      open = Enumerable.Range(1, SessionEventQueue.MaxOpenSessions + 1).Select(_ => Guid.NewGuid()).ToArray(),
      watched = Array.Empty<Guid>()
    }, json);
    using HttpResponseMessage rejected = await UpdateAsync(server.Client, streamId, tooMany);
    Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    AssertPrivateResponse(rejected);
  }

  private static ServerOptions Options(TemporaryDatabase database) => new(Path.GetDirectoryName(database.Path)!, 0);

  private static Task<HttpResponseMessage> OpenStreamAsync(HttpClient client) {
    var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
    request.Headers.Add("X-Urd-Client", "browser");
    return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
  }

  private static Task<HttpResponseMessage> WaitForDisconnectedAsync(HttpClient client, Guid streamId, Guid sessionId) =>
      WaitForDisconnectedCoreAsync(client, streamId, sessionId);

  private static async Task<HttpResponseMessage> WaitForDisconnectedCoreAsync(HttpClient client, Guid streamId, Guid sessionId) {
    using var deadline = new CancellationTokenSource(TestAsync.TestTimeout);
    while (true) {
      HttpResponseMessage response = await UpdateAsync(client, streamId, [sessionId], []);
      if (response.StatusCode == HttpStatusCode.NotFound)
        return response;
      response.Dispose();
      await Task.Delay(10, deadline.Token);
    }
  }

  private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, Guid streamId, Guid[] open, Guid[] watched) =>
      UpdateAsync(client, streamId, JsonSerializer.Serialize(new { open, watched }, json));

  private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, Guid streamId, string jsonBody) =>
      RequestAsync(client, HttpMethod.Post, $"/api/events/{streamId}", JsonBody(jsonBody));

  private static async Task<JsonElement> ReadEventAsync(StreamReader reader, string expectedName) {
    string actualName = await ReadEventNameAsync(reader);
    Assert.Equal(expectedName, actualName);
    string? data = null;
    while (true) {
      string? line = await reader.ReadLineAsync().WaitAsync(TestAsync.TestTimeout);
      Assert.NotNull(line);
      if (line.Length == 0)
        break;
      if (line.StartsWith("data: ", StringComparison.Ordinal))
        data = line[6..];
    }
    Assert.NotNull(data);
    using JsonDocument document = JsonDocument.Parse(data);
    return document.RootElement.Clone();
  }

  private static async Task<string> ReadEventNameAsync(StreamReader reader) {
    while (true) {
      string? line = await reader.ReadLineAsync().WaitAsync(TestAsync.TestTimeout);
      Assert.NotNull(line);
      if (line.StartsWith("event: ", StringComparison.Ordinal))
        return line[7..];
    }
  }

  private static async Task AssertNoEventAsync(StreamReader reader, TimeSpan duration) {
    using var cancellation = new CancellationTokenSource(duration);
    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadLineAsync(cancellation.Token));
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> WaitForever(
      [EnumeratorCancellation] CancellationToken cancellation) {
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
    yield break;
  }
}
