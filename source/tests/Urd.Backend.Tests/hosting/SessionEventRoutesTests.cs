using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class SessionEventRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task StreamAnnouncesCreationAndRegistrationWithoutReconnect() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
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

    Guid missingId = Guid.NewGuid();
    using HttpResponseMessage registered = await UpdateAsync(server.Client, streamId,
        [missingId, sessionId], [missingId, sessionId]);
    Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
    var handshake = new (string Name, JsonElement Data)[5];
    for (int index = 0; index < handshake.Length; index++)
      handshake[index] = await ReadEventAsync(reader);
    Assert.Equal(2, handshake.Count(frame => frame.Name == "browser"));
    Assert.Contains(handshake, frame => frame.Name == "browser" && frame.Data.GetProperty("session").GetGuid() == sessionId);
    JsonElement missing = Assert.Single(handshake, frame => frame.Name == "status"
        && frame.Data.GetProperty("session").GetGuid() == missingId).Data;
    Assert.Equal("unavailable", missing.GetProperty("status").GetString());
    JsonElement status = Assert.Single(handshake, frame => frame.Name == "status"
        && frame.Data.GetProperty("session").GetGuid() == sessionId).Data;
    Assert.Equal("idle", status.GetProperty("status").GetString());
    Assert.Equal(connected.GetProperty("boot").GetGuid(), status.GetProperty("boot").GetGuid());
    JsonElement change = Assert.Single(handshake, frame => frame.Name == "change").Data;
    Assert.Equal(sessionId, change.GetProperty("session").GetGuid());
    Assert.Equal(status.GetProperty("version").GetInt64(), change.GetProperty("version").GetInt64());

    using HttpResponseMessage unwatched = await UpdateAsync(server.Client, streamId, [sessionId], []);
    Assert.Equal(HttpStatusCode.NoContent, unwatched.StatusCode);
    Assert.Equal(sessionId, (await ReadEventAsync(reader, "browser")).GetProperty("session").GetGuid());
    Assert.Equal("idle", (await ReadEventAsync(reader, "status")).GetProperty("status").GetString());
    await AssertNoEventAsync(reader, TimeSpan.FromMilliseconds(200));

    stream.Dispose();
    using HttpResponseMessage stale = await WaitForDisconnectedAsync(server.Client, streamId, sessionId);
    Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
  }

  [Fact]
  public async Task WatchedSessionEventPageStreamsTransientToolProgressAndItsClear() {
    var entered = Signal();
    var release = Signal();
    var tool = new TestAgentTool("progress", async (_, context, cancellation) => {
      context.ReportProgress(new ToolOutput("partial output", []));
      entered.TrySetResult();
      await release.Task.WaitAsync(cancellation);
      return new ToolOutput("final output", []);
    });
    using var database = new TemporaryDatabase();
    using var store = UrdStore.Open(database.Path);
    int requests = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? ToolBatch(token, new FunctionCallContent("progress-call", "progress", new Dictionary<string, object?>()))
        : Complete(token));
    await using var sessions = new SessionManager(store, _ => client, [tool], Path.GetDirectoryName(database.Path)!);
    PrimarySession session = await sessions.CreatePrimaryAsync();
    using var page = new SessionEventPage(sessions.BootId, _ => { });
    await page.UpdateAsync(sessions, [session.Id], [session.Id], CancellationToken.None);
    using var deadline = new CancellationTokenSource(TestTimeout);
    Assert.Equal("browser", (await page.ReadNextAsync(deadline.Token)).Event?.Name);
    Assert.Equal("status", (await page.ReadNextAsync(deadline.Token)).Event?.Name);
    Assert.Equal("change", (await page.ReadNextAsync(deadline.Token)).Event?.Name);

    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
        new(Guid.NewGuid(), 0, "start", TestLlmSelection.Default));
    try {
      await entered.Task.WaitAsync(TestTimeout);
      JsonElement progress = await ReadProgressAsync(page, text: "partial output");
      Assert.Equal(session.Id, progress.GetProperty("session").GetGuid());
      Assert.Equal(receipt.ExecutionId!.Value, progress.GetProperty("executionId").GetGuid());
      Assert.Equal("progress-call", progress.GetProperty("callId").GetString());
    } finally {
      release.TrySetResult();
    }

    await session.WaitForExecutionFinishAsync(receipt.ExecutionId.Value).WaitAsync(TestTimeout);
    JsonElement cleared = await ReadProgressAsync(page, text: null);
    Assert.Equal("progress-call", cleared.GetProperty("callId").GetString());
  }

  [Fact]
  public async Task EventRoutesKeepTheBrowserGateAndBoundRegistrationSets() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
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

  private static async Task<JsonElement> ReadProgressAsync(SessionEventPage page, string? text) {
    using var deadline = new CancellationTokenSource(TestTimeout);
    while (true) {
      SessionSseRead next = await page.ReadNextAsync(deadline.Token);
      if (next.Event?.Name != "tool-progress")
        continue;
      using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(next.Event.Data));
      JsonElement data = document.RootElement.Clone();
      JsonElement actualText = data.GetProperty("text");
      if (text is null)
        Assert.Equal(JsonValueKind.Null, actualText.ValueKind);
      else
        Assert.Equal(text, actualText.GetString());
      return data;
    }
  }

  private static Task<HttpResponseMessage> OpenStreamAsync(HttpClient client) =>
      RequestAsync(client, HttpMethod.Get, "/api/events", completionOption: HttpCompletionOption.ResponseHeadersRead);

  private static async Task<HttpResponseMessage> WaitForDisconnectedAsync(HttpClient client, Guid streamId, Guid sessionId) {
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
    (string name, JsonElement data) = await ReadEventAsync(reader);
    Assert.Equal(expectedName, name);
    return data;
  }

  private static async Task<(string Name, JsonElement Data)> ReadEventAsync(StreamReader reader) {
    string name = await ReadEventNameAsync(reader);
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
    return (name, document.RootElement.Clone());
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
}
