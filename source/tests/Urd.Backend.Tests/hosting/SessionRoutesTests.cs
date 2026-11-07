using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

// Real HTTP commands and bounded reads retain Core identities and durable decisions across clients and restarts.
public sealed class SessionRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    PropertyNameCaseInsensitive = false,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task IndependentChatsAndConcurrentExactRetriesShareOnlyTheAddressedWork() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    Uri origin = await server.WaitForReadyAsync();
    using var other = NewClient(origin);
    Guid first = await CreateAsync(server.Client);
    Guid second = await CreateAsync(other);
    Assert.NotEqual(first, second);
    Assert.Empty((await SnapshotAsync(other, second)).Messages);

    var command = new InputCommand(Guid.NewGuid(), 0, "one addressed input");
    InputReceipt[] receipts = await Task.WhenAll(SubmitAsync(server.Client, first, command),
        SubmitAsync(other, first, command));
    Assert.Equal(receipts[0], receipts[1]);
    Assert.Equal(InputDecision.Accepted, receipts[0].Decision);
    Assert.Equal(first, receipts[0].SessionId);
    Assert.Equal(command, receipts[0].Command);
    Guid runId = Assert.IsType<Guid>(receipts[0].RunId);
    SessionSnapshot snapshot = await SettledAsync(other, first, runId);
    Assert.Equal(ExecutionOutcome.Completed, snapshot.LastSettlement!.Outcome);
    Assert.Equal(receipts[0], await ReceiptAsync(other, first, command.InputId));
    Assert.Empty((await SnapshotAsync(server.Client, second)).Messages);
    Assert.Equal(2, StoredSessions.Read(database).Count);
    Assert.Single(StoredInputs.Read(database));
    Assert.Single(StoredRuns.Read(database));
    Assert.Single(StoredRuns.ReadRequests(database));
    Assert.Equal("Hello from Urd.", StoredOutput.ReadText(database));
  }

  [Fact]
  public async Task UnknownSendUsesReceiptAndOriginalRetryWithoutReplacingItsDecision() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    var command = new InputCommand(Guid.NewGuid(), 0, "original\0🙂");
    Assert.Null(await ReceiptAsync(server.Client, sessionId, command.InputId));
    // The caller deliberately discards an acknowledgement, then reconciles through the read route.
    using (HttpResponseMessage ignored = await RequestAsync(server.Client, HttpMethod.Post,
        InputsPath(sessionId), JsonContent.Create(command, options: json))) {
      Assert.Equal(HttpStatusCode.OK, ignored.StatusCode);
    }

    InputReceipt retained = Assert.IsType<InputReceipt>(await ReceiptAsync(server.Client, sessionId, command.InputId));
    Assert.Equal(command, retained.Command);
    Assert.Equal(InputDecision.Accepted, retained.Decision);
    await SettledAsync(server.Client, sessionId, retained.RunId!.Value);
    Assert.Equal(retained, await SubmitAsync(server.Client, sessionId, command));
    await AssertStatusAsync(server.Client, HttpMethod.Post, InputsPath(sessionId), HttpStatusCode.Conflict,
        JsonContent.Create(command with { Text = "changed" }, options: json));
    await AssertStatusAsync(server.Client, HttpMethod.Post, InputsPath(sessionId), HttpStatusCode.Conflict,
        JsonContent.Create(command with { ExpectedHistoryRevision = 1 }, options: json));
    Assert.Equal(retained, await ReceiptAsync(server.Client, sessionId, command.InputId));
    Assert.Single(StoredInputs.Read(database));
    Assert.Single(StoredRuns.Read(database));
    Assert.Single(StoredRuns.ReadRequests(database));
  }

  [Fact]
  public async Task BusyHttpSendsBecomeDurableSteeringWithoutChangingTheRoutes() {
    var entered = Signal();
    var release = Signal();
    int requests = 0;
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      if (Interlocked.Increment(ref requests) == 1) {
        entered.TrySetResult();
        await release.Task.WaitAsync(token);
      }
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    using var database = new TemporaryDatabase();
    var model = new ScriptedChatClient(Response);
    await using var server = new RunningServer(Options(database), createClient: () => model);
    await server.WaitForReadyAsync();
    try {
      Guid sessionId = await CreateAsync(server.Client);
      InputReceipt origin = await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, "start"));
      await entered.Task.WaitAsync(TestTimeout);
      var command = new InputCommand(Guid.NewGuid(), 0, "steer through HTTP");
      InputReceipt[] receipts = await Task.WhenAll(SubmitAsync(server.Client, sessionId, command),
          SubmitAsync(server.Client, sessionId, command));
      Assert.Equal(receipts[0], receipts[1]);
      Assert.Equal(InputDecision.Accepted, receipts[0].Decision);
      Assert.Equal(origin.RunId, receipts[0].RunId);
      Assert.Equal(receipts[0], await ReceiptAsync(server.Client, sessionId, command.InputId));
      Assert.Contains((await SnapshotAsync(server.Client, sessionId)).Messages,
          message => message.Message.MessageId == command.InputId && message.Preview == command.Text);
      Assert.Equal(1, model.Calls);

      release.TrySetResult();
      SessionSnapshot settled = await SettledAsync(server.Client, sessionId, origin.RunId!.Value);
      Assert.Equal(ExecutionOutcome.Completed, settled.LastSettlement!.Outcome);
      Assert.Equal(2, model.Calls);
      Assert.Equal(new[] { "start", command.Text }, model.LastRequest
          .Where(message => message.Role == ChatRole.User).Select(message => message.Text));
      Assert.Equal(receipts[0], await SubmitAsync(server.Client, sessionId, command));
      Assert.Equal(2, StoredRuns.ReadRequests(database).Count);
      Assert.Single(StoredRuns.Read(database));
    } finally {
      release.TrySetResult();
    }
  }

  [Fact]
  public async Task StaleRevisionRefusalIsDurableEvenAfterAnotherInputCompletes() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    var stale = new InputCommand(Guid.NewGuid(), 1, "wrong revision");
    InputReceipt refused = await SubmitAsync(server.Client, sessionId, stale);
    Assert.Equal(InputDecision.Refused, refused.Decision);
    Assert.Equal(InputRefusalReason.StaleRevision, refused.RefusalReason);
    Assert.Null(refused.RunId);
    Assert.Empty(StoredRuns.Read(database));
    Assert.Empty((await SnapshotAsync(server.Client, sessionId)).Messages);

    InputReceipt accepted = await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, "fresh command"));
    await SettledAsync(server.Client, sessionId, accepted.RunId!.Value);
    Assert.Equal(refused, await SubmitAsync(server.Client, sessionId, stale));
    Assert.Equal(refused, await ReceiptAsync(server.Client, sessionId, stale.InputId));
    Assert.Equal(2, StoredInputs.Read(database).Count);
    Assert.Single(StoredRuns.Read(database));
    Assert.Single(StoredRuns.ReadRequests(database));
  }

  [Fact]
  public async Task UnicodeContentRoundTripsBeyondPreviewAndChecksItsSessionAndRevision() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    Guid otherId = await CreateAsync(server.Client);
    string text = "start\0" + string.Concat(Enumerable.Repeat("é🙂", 2000));
    InputReceipt accepted = await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, text));
    SessionSnapshot snapshot = await SettledAsync(server.Client, sessionId, accepted.RunId!.Value);
    MessagePreview user = Assert.Single(snapshot.Messages, row => row.Message.Role == MessageRole.User);
    Assert.True(user.Truncated);
    Assert.InRange(user.Preview.Length, 1, 256);
    var cursor = new MessageContentCursor(user.Message, 0, 0);
    MessageContentPage page = await ContentAsync(server.Client, sessionId, cursor);
    Assert.Equal(cursor, page.From);
    Assert.Equal(text, page.Text);
    Assert.InRange(Encoding.UTF8.GetByteCount(page.Text), 1, 16 * 1024);
    Assert.False(page.HasMore);
    Assert.True(page.IsClosed);
    Assert.False(page.FinalOutputCommitted);
    MessageContentPage tail = await ContentAsync(server.Client, sessionId, page.Next);
    Assert.Empty(tail.Text);
    Assert.Equal(page.Next, tail.Next);
    Assert.True(tail.IsClosed);
    Assert.False(tail.HasMore);
    Assert.Equal(text[6..], (await ContentAsync(server.Client, sessionId, cursor with { Utf8Offset = 6 })).Text);
    await AssertStatusAsync(server.Client, HttpMethod.Get, ContentPath(otherId, cursor), HttpStatusCode.NotFound);
    await AssertStatusAsync(server.Client, HttpMethod.Get,
        ContentPath(sessionId, cursor with { Message = user.Message with { HistoryRevision = 1 } }),
        HttpStatusCode.Conflict);
    await AssertStatusAsync(server.Client, HttpMethod.Get,
        ContentPath(sessionId, cursor with { Utf8Offset = 9 }), HttpStatusCode.BadRequest);

    MessagePreview assistant = Assert.Single(snapshot.Messages, row => row.Message.Role == MessageRole.Assistant);
    MessageContentPage output = await ContentAsync(server.Client, sessionId, new(assistant.Message, 0, 0));
    Assert.Equal("Hello from Urd.", output.Text);
    Assert.True(output.IsClosed);
    Assert.True(output.FinalOutputCommitted);
  }

  [Fact]
  public async Task InvalidJsonMediaAndRequiredMembersNeverRecordAdmission() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    await AssertStatusAsync(server.Client, HttpMethod.Post, "/api/sessions", HttpStatusCode.UnsupportedMediaType,
        new StringContent("{}"));
    await AssertStatusAsync(server.Client, HttpMethod.Post, "/api/sessions", HttpStatusCode.BadRequest,
        JsonBody("{"));
    await AssertStatusAsync(server.Client, HttpMethod.Post, "/api/sessions", HttpStatusCode.BadRequest,
        JsonBody("null"));
    foreach (string body in new[] { "{}", "null", "[]", "{", "{\"text\":\"missing IDs\"}",
        $"{{\"inputId\":\"{Guid.NewGuid()}\",\"text\":\"missing revision\"}}",
        $"{{\"inputId\":\"{Guid.NewGuid()}\",\"expectedHistoryRevision\":0}}" }) {
      await AssertStatusAsync(server.Client, HttpMethod.Post, InputsPath(sessionId), HttpStatusCode.BadRequest,
          JsonBody(body));
    }
    await AssertStatusAsync(server.Client, HttpMethod.Post, InputsPath(sessionId), HttpStatusCode.UnsupportedMediaType,
        new StringContent("{}", Encoding.UTF8, "text/plain"));
    foreach (string charset in new[] { "bogus", "utf-7" }) {
      foreach (string path in new[] { "/api/sessions", InputsPath(sessionId) }) {
        string body = path == "/api/sessions" ? "{}"
            : JsonSerializer.Serialize(new InputCommand(Guid.NewGuid(), 0, "not admitted"), json);
        StringContent content = JsonBody(body);
        content.Headers.ContentType!.CharSet = charset;
        await AssertStatusAsync(server.Client, HttpMethod.Post, path, HttpStatusCode.UnsupportedMediaType, content);
      }
    }
    Assert.Equal(new StoredSessionRow(sessionId, 0), Assert.Single(StoredSessions.Read(database)));
    Assert.Empty(StoredInputs.Read(database));
    Assert.Empty(StoredRuns.Read(database));
    Assert.Empty(StoredRuns.ReadRequests(database));

    foreach ((Encoding encoding, string? charset) in new (Encoding, string?)[] {
        (Encoding.UTF8, null), (Encoding.UTF8, "\"utf-8\""), (Encoding.Unicode, "\"utf-16\"") }) {
      var createContent = new StringContent("{}", encoding, "application/json");
      createContent.Headers.ContentType!.CharSet = charset;
      using HttpResponseMessage created = await RequestAsync(server.Client, HttpMethod.Post, "/api/sessions", createContent);
      Assert.Equal(HttpStatusCode.Created, created.StatusCode);
      AssertPrivateResponse(created);
      using JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
      Guid createdId = body.RootElement.GetProperty("sessionId").GetGuid();
      var command = new InputCommand(Guid.NewGuid(), 0, "supported é🙂");
      var inputContent = new StringContent(JsonSerializer.Serialize(command, json), encoding, "application/json");
      inputContent.Headers.ContentType!.CharSet = charset;
      using HttpResponseMessage submitted = await RequestAsync(server.Client, HttpMethod.Post,
          InputsPath(createdId), inputContent);
      InputReceipt receipt = Assert.IsType<InputReceipt>(await ReadAsync<InputReceipt>(submitted));
      Assert.Equal(command, receipt.Command);
      Assert.Equal(InputDecision.Accepted, receipt.Decision);
      await SettledAsync(server.Client, createdId, receipt.RunId!.Value);
    }
  }

  [Fact]
  public async Task BodyAndDecodedUtf8LimitsApplyBeforeAdmissionIncludingChunkedRequests() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    foreach (bool chunked in new[] { false, true }) {
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Post, "/api/sessions",
          JsonBody("{\"unused\":\"" + new string('x', 128 * 1024) + "\"}"), chunked: chunked);
      Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
      AssertPrivateResponse(response);
    }
    await AssertStatusAsync(server.Client, HttpMethod.Post, InputsPath(sessionId), HttpStatusCode.RequestEntityTooLarge,
        JsonContent.Create(new InputCommand(Guid.NewGuid(), 0, new string('é', 8193)), options: json));
    Assert.Empty(StoredInputs.Read(database));
    Assert.Single(StoredSessions.Read(database));

    InputReceipt boundary = await SubmitAsync(server.Client, sessionId,
        new(Guid.NewGuid(), 0, new string('é', 8192)));
    Assert.Equal(InputDecision.Accepted, boundary.Decision);
    await SettledAsync(server.Client, sessionId, boundary.RunId!.Value);
    Assert.Single(StoredInputs.Read(database));
  }

  [Fact]
  public async Task MalformedIdentifiersCursorsAndReadBodiesAreNotConfusedWithMissingRows() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    foreach (string id in new[] { "not-a-guid", Guid.Empty.ToString() }) {
      await AssertStatusAsync(server.Client, HttpMethod.Get, $"/api/sessions/{id}/snapshot", HttpStatusCode.BadRequest);
      await AssertStatusAsync(server.Client, HttpMethod.Get, $"{InputsPath(sessionId)}/{id}", HttpStatusCode.BadRequest);
    }
    await AssertStatusAsync(server.Client, HttpMethod.Get, $"/api/sessions/{Guid.NewGuid()}/snapshot", HttpStatusCode.NotFound);
    await AssertStatusAsync(server.Client, HttpMethod.Get, $"/api/sessions/{sessionId}/content", HttpStatusCode.BadRequest);
    await AssertStatusAsync(server.Client, HttpMethod.Get, $"/api/sessions/{sessionId}/snapshot", HttpStatusCode.BadRequest,
        new StringContent("body"));
    await AssertStatusAsync(server.Client, HttpMethod.Get, $"{InputsPath(sessionId)}/{Guid.NewGuid()}", HttpStatusCode.BadRequest,
        new StringContent("body"));
    await AssertStatusAsync(server.Client, HttpMethod.Post, InputsPath(sessionId), HttpStatusCode.BadRequest,
        JsonContent.Create(new InputCommand(Guid.Empty, 0, "empty ID"), options: json));
    await AssertStatusAsync(server.Client, HttpMethod.Post, InputsPath(sessionId), HttpStatusCode.BadRequest,
        JsonContent.Create(new InputCommand(Guid.NewGuid(), -1, "negative revision"), options: json));
    Assert.Empty(StoredInputs.Read(database));
    Assert.Empty(StoredRuns.Read(database));
  }

  [Fact]
  public async Task ForeignAndUnmarkedRequestsCannotReadPrivateStateOrCauseSideEffects() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    InputReceipt accepted = await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, "private text"));
    SessionSnapshot snapshot = await SettledAsync(server.Client, sessionId, accepted.RunId!.Value);
    MessagePreview user = Assert.Single(snapshot.Messages, row => row.Message.Role == MessageRole.User);
    List<StoredSessionRow> sessions = StoredSessions.Read(database);
    List<StoredDecision> decisions = StoredInputs.Read(database);
    List<StoredRun> runs = StoredRuns.Read(database);
    List<StoredRequest> requests = StoredRuns.ReadRequests(database);
    string output = StoredOutput.ReadText(database);
    string[] privateReads = [$"/api/sessions/{sessionId}/snapshot", $"{InputsPath(sessionId)}/{accepted.Command.InputId}",
      ContentPath(sessionId, new(user.Message, 0, 0))];
    foreach (string path in privateReads) {
      using HttpResponseMessage unmarked = await RequestAsync(server.Client, HttpMethod.Get, path, marker: false);
      Assert.Equal(HttpStatusCode.Forbidden, unmarked.StatusCode);
      AssertPrivateResponse(unmarked);
      Assert.DoesNotContain("private text", await unmarked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
      using HttpResponseMessage foreign = await RequestAsync(server.Client, HttpMethod.Get, path, suppliedOrigin: "null");
      Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
      AssertPrivateResponse(foreign);
    }
    foreach (string path in new[] { "/api/sessions", InputsPath(sessionId) }) {
      using HttpResponseMessage simple = await RequestAsync(server.Client, HttpMethod.Post, path,
          new StringContent("{}"), marker: false, suppliedOrigin: "http://foreign.example");
      Assert.Equal(HttpStatusCode.Forbidden, simple.StatusCode);
      AssertPrivateResponse(simple);
      using HttpResponseMessage foreign = await RequestAsync(server.Client, HttpMethod.Post, path,
          JsonContent.Create(new InputCommand(Guid.NewGuid(), 0, "forbidden"), options: json),
          suppliedOrigin: "http://foreign.example");
      Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
      AssertPrivateResponse(foreign);
    }
    using (var preflight = new HttpRequestMessage(HttpMethod.Options, InputsPath(sessionId))) {
      preflight.Headers.Add("Origin", "http://foreign.example");
      preflight.Headers.Add("Access-Control-Request-Method", "POST");
      preflight.Headers.Add("Access-Control-Request-Headers", "X-Urd-Client,Content-Type");
      using HttpResponseMessage response = await server.Client.SendAsync(preflight);
      Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      AssertPrivateResponse(response);
    }
    using (HttpResponseMessage unknown = await RequestAsync(server.Client, HttpMethod.Get, "/API/unknown", marker: false)) {
      Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
      AssertPrivateResponse(unknown);
    }
    await AssertStatusAsync(server.Client, HttpMethod.Get, "/api/unknown", HttpStatusCode.NotFound);
    using (HttpResponseMessage health = await server.Client.GetAsync("/health")) {
      Assert.Equal(HttpStatusCode.NoContent, health.StatusCode);
      Assert.Null(health.Headers.CacheControl);
    }
    Assert.Equal(sessions, StoredSessions.Read(database));
    Assert.Equal(decisions, StoredInputs.Read(database));
    Assert.Equal(runs, StoredRuns.Read(database));
    Assert.Equal(requests, StoredRuns.ReadRequests(database));
    Assert.Equal(output, StoredOutput.ReadText(database));
  }

  [Fact]
  public async Task RestartPreservesHttpDecisionsContentAndExactRetryWithoutReplay() {
    using var database = new TemporaryDatabase();
    InputReceipt accepted, refused;
    SessionSnapshot before;
    Guid sessionId;
    var command = new InputCommand(Guid.NewGuid(), 0, "retained over restart\0🙂");
    await using (var first = new RunningServer(Options(database))) {
      await first.WaitForReadyAsync();
      sessionId = await CreateAsync(first.Client);
      accepted = await SubmitAsync(first.Client, sessionId, command);
      before = await SettledAsync(first.Client, sessionId, accepted.RunId!.Value);
      refused = await SubmitAsync(first.Client, sessionId, new(Guid.NewGuid(), 1, "retained refusal"));
      Assert.Equal(InputDecision.Refused, refused.Decision);
    }
    List<StoredRun> runs = StoredRuns.Read(database);
    List<StoredRequest> requests = StoredRuns.ReadRequests(database);
    string output = StoredOutput.ReadText(database);
    await using var restarted = new RunningServer(Options(database));
    await restarted.WaitForReadyAsync();
    SessionSnapshot after = await SnapshotAsync(restarted.Client, sessionId);
    Assert.Equal(before.LastSettlement, after.LastSettlement);
    Assert.Equal(before.HistoryRevision, after.HistoryRevision);
    Assert.Equal(before.Messages, after.Messages);
    Assert.Equal(accepted, await ReceiptAsync(restarted.Client, sessionId, command.InputId));
    Assert.Equal(refused, await ReceiptAsync(restarted.Client, sessionId, refused.Command.InputId));
    Assert.Equal(accepted, await SubmitAsync(restarted.Client, sessionId, command));
    Assert.Equal(refused, await SubmitAsync(restarted.Client, sessionId, refused.Command));
    MessagePreview user = Assert.Single(after.Messages, row => row.Message.Role == MessageRole.User);
    Assert.Equal(command.Text, (await ContentAsync(restarted.Client, sessionId, new(user.Message, 0, 0))).Text);
    Assert.Equal(runs, StoredRuns.Read(database));
    Assert.Equal(requests, StoredRuns.ReadRequests(database));
    Assert.Equal(output, StoredOutput.ReadText(database));
    Assert.Equal(2, StoredInputs.Read(database).Count);
  }

  private static ServerOptions Options(TemporaryDatabase database) => new(Path.GetDirectoryName(database.Path)!, 0);

  private static HttpClient NewClient(Uri origin) =>
      new(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = origin, Timeout = TestTimeout };

  private static string InputsPath(Guid sessionId) => $"/api/sessions/{sessionId}/inputs";

  private static string ContentPath(Guid sessionId, MessageContentCursor cursor) =>
      $"/api/sessions/{sessionId}/content?runId={cursor.Message.RunId}&messageId={cursor.Message.MessageId}"
      + $"&role={JsonNamingPolicy.CamelCase.ConvertName(cursor.Message.Role.ToString())}"
      + $"&historyRevision={cursor.Message.HistoryRevision}&segmentIndex={cursor.SegmentIndex}&utf8Offset={cursor.Utf8Offset}";

  private static StringContent JsonBody(string body) => new(body, Encoding.UTF8, "application/json");

  private static async Task<HttpResponseMessage> RequestAsync(HttpClient client, HttpMethod method, string path,
      HttpContent? content = null, bool marker = true, string? suppliedOrigin = null, bool chunked = false) {
    using var request = new HttpRequestMessage(method, path) { Content = content };
    if (marker)
      request.Headers.Add("X-Urd-Client", "browser");
    if (suppliedOrigin is not null)
      request.Headers.Add("Origin", suppliedOrigin);
    if (chunked)
      request.Headers.TransferEncodingChunked = true;
    return await client.SendAsync(request);
  }

  private static void AssertPrivateResponse(HttpResponseMessage response) {
    Assert.True(response.Headers.CacheControl?.NoStore);
    Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
  }

  private static async Task AssertStatusAsync(HttpClient client, HttpMethod method, string path,
      HttpStatusCode status, HttpContent? content = null) {
    using HttpResponseMessage response = await RequestAsync(client, method, path, content);
    Assert.Equal(status, response.StatusCode);
    AssertPrivateResponse(response);
  }

  private static async Task<T?> ReadAsync<T>(HttpResponseMessage response) {
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertPrivateResponse(response);
    Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), json);
  }

  private static async Task<Guid> CreateAsync(HttpClient client) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, "/api/sessions", JsonBody("{}"));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    AssertPrivateResponse(response);
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Guid id = body.RootElement.GetProperty("sessionId").GetGuid();
    Assert.NotEqual(Guid.Empty, id);
    Assert.Equal($"/api/sessions/{id}/snapshot", response.Headers.Location?.OriginalString);
    return id;
  }

  private static async Task<InputReceipt> SubmitAsync(HttpClient client, Guid sessionId, InputCommand command) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, InputsPath(sessionId),
        JsonContent.Create(command, options: json));
    InputReceipt receipt = Assert.IsType<InputReceipt>(await ReadAsync<InputReceipt>(response));
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(receipt.Decision.ToString()),
        body.RootElement.GetProperty("decision").GetString());
    return receipt;
  }

  private static async Task<InputReceipt?> ReceiptAsync(HttpClient client, Guid sessionId, Guid inputId) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, $"{InputsPath(sessionId)}/{inputId}");
    return await ReadAsync<InputReceipt>(response);
  }

  private static async Task<SessionSnapshot> SnapshotAsync(HttpClient client, Guid sessionId) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, $"/api/sessions/{sessionId}/snapshot");
    SessionSnapshot snapshot = Assert.IsType<SessionSnapshot>(await ReadAsync<SessionSnapshot>(response));
    Assert.Equal(sessionId, snapshot.SessionId);
    return snapshot;
  }

  private static async Task<MessageContentPage> ContentAsync(HttpClient client, Guid sessionId, MessageContentCursor cursor) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, ContentPath(sessionId, cursor));
    return Assert.IsType<MessageContentPage>(await ReadAsync<MessageContentPage>(response));
  }

  private static async Task<SessionSnapshot> SettledAsync(HttpClient client, Guid sessionId, Guid runId) {
    using var deadline = new CancellationTokenSource(TestTimeout);
    SessionSnapshot snapshot = await SnapshotAsync(client, sessionId).WaitAsync(deadline.Token);
    while (snapshot.LastSettlement?.RunId != runId) {
      await Task.Delay(10, deadline.Token);
      snapshot = await SnapshotAsync(client, sessionId).WaitAsync(deadline.Token);
    }
    Assert.Null(snapshot.UnsettledRunId);
    return snapshot;
  }
}
