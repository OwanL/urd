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

    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "one addressed input", TestLlmSelection.Default);
    UserMessageReceipt[] receipts = await Task.WhenAll(SubmitAsync(server.Client, first, submission),
        SubmitAsync(other, first, submission));
    Assert.Equal(receipts[0], receipts[1]);
    Assert.Equal(MessageAcceptance.Accepted, receipts[0].Acceptance);
    Assert.Equal(first, receipts[0].SessionId);
    Assert.Equal(submission, receipts[0].Submission);
    Guid executionId = Assert.IsType<Guid>(receipts[0].ExecutionId);
    SessionSnapshot snapshot = await SettledAsync(other, first, executionId);
    Assert.Equal(ExecutionOutcome.Completed, snapshot.LastSettlement!.Outcome);
    Assert.Equal(receipts[0], await ReceiptAsync(other, first, submission.SubmissionId));
    Assert.Empty((await SnapshotAsync(server.Client, second)).Messages);
    Assert.Equal(2, StoredSessions.Read(database).Count);
    Assert.Single(StoredSubmissions.Read(database));
    Assert.Single(StoredExecutions.Read(database));
    Assert.Single(StoredExecutions.ReadRequests(database));
    Assert.Equal("Hello from Urd.", StoredLlmResponses.ReadText(database));
  }

  [Fact]
  public async Task UnknownSendUsesReceiptAndOriginalRetryWithoutReplacingItsDecision() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "original\0🙂", TestLlmSelection.Default);
    Assert.Null(await ReceiptAsync(server.Client, sessionId, submission.SubmissionId));
    // The caller deliberately discards an acknowledgement, then reconciles through the read route.
    using (HttpResponseMessage ignored = await RequestAsync(server.Client, HttpMethod.Post,
        SubmissionsPath(sessionId), JsonContent.Create(submission, options: json))) {
      Assert.Equal(HttpStatusCode.OK, ignored.StatusCode);
    }

    UserMessageReceipt retained = Assert.IsType<UserMessageReceipt>(await ReceiptAsync(server.Client, sessionId, submission.SubmissionId));
    Assert.Equal(submission, retained.Submission);
    Assert.Equal(MessageAcceptance.Accepted, retained.Acceptance);
    await SettledAsync(server.Client, sessionId, retained.ExecutionId!.Value);
    Assert.Equal(retained, await SubmitAsync(server.Client, sessionId, submission));
    await AssertStatusAsync(server.Client, HttpMethod.Post, SubmissionsPath(sessionId), HttpStatusCode.Conflict,
        JsonContent.Create(submission with { Text = "changed" }, options: json));
    await AssertStatusAsync(server.Client, HttpMethod.Post, SubmissionsPath(sessionId), HttpStatusCode.Conflict,
        JsonContent.Create(submission with { ExpectedHistoryRevision = 1 }, options: json));
    Assert.Equal(retained, await ReceiptAsync(server.Client, sessionId, submission.SubmissionId));
    Assert.Single(StoredSubmissions.Read(database));
    Assert.Single(StoredExecutions.Read(database));
    Assert.Single(StoredExecutions.ReadRequests(database));
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
    var llm = new ScriptedChatClient(Response);
    await using var server = new RunningServer(Options(database), createClient: () => llm);
    await server.WaitForReadyAsync();
    try {
      Guid sessionId = await CreateAsync(server.Client);
      UserMessageReceipt origin = await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, "start", TestLlmSelection.Default));
      await entered.Task.WaitAsync(TestTimeout);
      var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "steer through HTTP", TestLlmSelection.Default);
      UserMessageReceipt[] receipts = await Task.WhenAll(SubmitAsync(server.Client, sessionId, submission),
          SubmitAsync(server.Client, sessionId, submission));
      Assert.Equal(receipts[0], receipts[1]);
      Assert.Equal(MessageAcceptance.Accepted, receipts[0].Acceptance);
      Assert.Equal(origin.ExecutionId, receipts[0].ExecutionId);
      Assert.Equal(receipts[0], await ReceiptAsync(server.Client, sessionId, submission.SubmissionId));
      Assert.Contains((await SnapshotAsync(server.Client, sessionId)).Messages,
          message => message.Message.MessageId == submission.SubmissionId && message.Preview == submission.Text);
      Assert.Equal(1, llm.Calls);

      release.TrySetResult();
      SessionSnapshot settled = await SettledAsync(server.Client, sessionId, origin.ExecutionId!.Value);
      Assert.Equal(ExecutionOutcome.Completed, settled.LastSettlement!.Outcome);
      Assert.Equal(2, llm.Calls);
      Assert.Equal(new[] { "start", submission.Text }, llm.LastRequest
          .Where(message => message.Role == ChatRole.User).Select(message => message.Text));
      Assert.Equal(receipts[0], await SubmitAsync(server.Client, sessionId, submission));
      Assert.Equal(2, StoredExecutions.ReadRequests(database).Count);
      Assert.Single(StoredExecutions.Read(database));
    } finally {
      release.TrySetResult();
    }
  }

  [Fact]
  public async Task StaleRevisionRefusalIsDurableEvenAfterAnotherSubmissionCompletes() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    var stale = new UserMessageSubmission(Guid.NewGuid(), 1, "wrong revision", TestLlmSelection.Default);
    UserMessageReceipt refused = await SubmitAsync(server.Client, sessionId, stale);
    Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
    Assert.Equal(MessageRefusalReason.StaleRevision, refused.RefusalReason);
    Assert.Null(refused.ExecutionId);
    Assert.Empty(StoredExecutions.Read(database));
    Assert.Empty((await SnapshotAsync(server.Client, sessionId)).Messages);

    UserMessageReceipt accepted = await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, "fresh command", TestLlmSelection.Default));
    await SettledAsync(server.Client, sessionId, accepted.ExecutionId!.Value);
    Assert.Equal(refused, await SubmitAsync(server.Client, sessionId, stale));
    Assert.Equal(refused, await ReceiptAsync(server.Client, sessionId, stale.SubmissionId));
    Assert.Equal(2, StoredSubmissions.Read(database).Count);
    Assert.Single(StoredExecutions.Read(database));
    Assert.Single(StoredExecutions.ReadRequests(database));
  }

  [Fact]
  public async Task UnicodeContentRoundTripsBeyondPreviewAndChecksItsSessionAndRevision() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    Guid otherId = await CreateAsync(server.Client);
    string text = "start\0" + string.Concat(Enumerable.Repeat("é🙂", 2000));
    UserMessageReceipt accepted = await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, text, TestLlmSelection.Default));
    SessionSnapshot snapshot = await SettledAsync(server.Client, sessionId, accepted.ExecutionId!.Value);
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
    string selection = JsonSerializer.Serialize(TestLlmSelection.Default, json);
    foreach (string body in new[] { "{}", "null", "[]", "{", $"{{\"text\":\"missing IDs\",\"llmSelection\":{selection}}}",
        $"{{\"submissionId\":\"{Guid.NewGuid()}\",\"text\":\"missing revision\",\"llmSelection\":{selection}}}",
        $"{{\"submissionId\":\"{Guid.NewGuid()}\",\"expectedHistoryRevision\":0,\"llmSelection\":{selection}}}" }) {
      await AssertStatusAsync(server.Client, HttpMethod.Post, SubmissionsPath(sessionId), HttpStatusCode.BadRequest,
          JsonBody(body));
    }
    await AssertStatusAsync(server.Client, HttpMethod.Post, SubmissionsPath(sessionId), HttpStatusCode.UnsupportedMediaType,
        new StringContent("{}", Encoding.UTF8, "text/plain"));
    foreach (string charset in new[] { "bogus", "utf-7" }) {
      foreach (string path in new[] { "/api/sessions", SubmissionsPath(sessionId) }) {
        string body = path == "/api/sessions" ? "{}"
            : JsonSerializer.Serialize(new UserMessageSubmission(Guid.NewGuid(), 0, "not admitted", TestLlmSelection.Default), json);
        StringContent content = JsonBody(body);
        content.Headers.ContentType!.CharSet = charset;
        await AssertStatusAsync(server.Client, HttpMethod.Post, path, HttpStatusCode.UnsupportedMediaType, content);
      }
    }
    Assert.Equal(new StoredSessionRow(sessionId, 0), Assert.Single(StoredSessions.Read(database)));
    Assert.Empty(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
    Assert.Empty(StoredExecutions.ReadRequests(database));

    foreach ((Encoding encoding, string? charset) in new (Encoding, string?)[] {
        (Encoding.UTF8, null), (Encoding.UTF8, "\"utf-8\""), (Encoding.Unicode, "\"utf-16\"") }) {
      var createContent = new StringContent("{}", encoding, "application/json");
      createContent.Headers.ContentType!.CharSet = charset;
      using HttpResponseMessage created = await RequestAsync(server.Client, HttpMethod.Post, "/api/sessions", createContent);
      Assert.Equal(HttpStatusCode.Created, created.StatusCode);
      AssertPrivateResponse(created);
      using JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
      Guid createdId = body.RootElement.GetProperty("sessionId").GetGuid();
      var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "supported é🙂", TestLlmSelection.Default);
      var submissionContent = new StringContent(JsonSerializer.Serialize(submission, json), encoding, "application/json");
      submissionContent.Headers.ContentType!.CharSet = charset;
      using HttpResponseMessage submitted = await RequestAsync(server.Client, HttpMethod.Post,
          SubmissionsPath(createdId), submissionContent);
      UserMessageReceipt receipt = Assert.IsType<UserMessageReceipt>(await ReadAsync<UserMessageReceipt>(submitted));
      Assert.Equal(submission, receipt.Submission);
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      await SettledAsync(server.Client, createdId, receipt.ExecutionId!.Value);
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
    await AssertStatusAsync(server.Client, HttpMethod.Post, SubmissionsPath(sessionId), HttpStatusCode.RequestEntityTooLarge,
        JsonContent.Create(new UserMessageSubmission(Guid.NewGuid(), 0, new string('é', 8193), TestLlmSelection.Default), options: json));
    Assert.Empty(StoredSubmissions.Read(database));
    Assert.Single(StoredSessions.Read(database));

    UserMessageReceipt boundary = await SubmitAsync(server.Client, sessionId,
        new(Guid.NewGuid(), 0, new string('é', 8192), TestLlmSelection.Default));
    Assert.Equal(MessageAcceptance.Accepted, boundary.Acceptance);
    await SettledAsync(server.Client, sessionId, boundary.ExecutionId!.Value);
    Assert.Single(StoredSubmissions.Read(database));
  }

  [Fact]
  public async Task MalformedIdentifiersCursorsAndReadBodiesAreNotConfusedWithMissingRows() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    foreach (string id in new[] { "not-a-guid", Guid.Empty.ToString() }) {
      await AssertStatusAsync(server.Client, HttpMethod.Get, $"/api/sessions/{id}/snapshot", HttpStatusCode.BadRequest);
      await AssertStatusAsync(server.Client, HttpMethod.Get, $"{SubmissionsPath(sessionId)}/{id}", HttpStatusCode.BadRequest);
    }
    await AssertStatusAsync(server.Client, HttpMethod.Get, $"/api/sessions/{Guid.NewGuid()}/snapshot", HttpStatusCode.NotFound);
    await AssertStatusAsync(server.Client, HttpMethod.Get, $"/api/sessions/{sessionId}/content", HttpStatusCode.BadRequest);
    await AssertStatusAsync(server.Client, HttpMethod.Get, $"/api/sessions/{sessionId}/snapshot", HttpStatusCode.BadRequest,
        new StringContent("body"));
    await AssertStatusAsync(server.Client, HttpMethod.Get, $"{SubmissionsPath(sessionId)}/{Guid.NewGuid()}", HttpStatusCode.BadRequest,
        new StringContent("body"));
    await AssertStatusAsync(server.Client, HttpMethod.Post, SubmissionsPath(sessionId), HttpStatusCode.BadRequest,
        JsonContent.Create(new UserMessageSubmission(Guid.Empty, 0, "empty ID", TestLlmSelection.Default), options: json));
    await AssertStatusAsync(server.Client, HttpMethod.Post, SubmissionsPath(sessionId), HttpStatusCode.BadRequest,
        JsonContent.Create(new UserMessageSubmission(Guid.NewGuid(), -1, "negative revision", TestLlmSelection.Default), options: json));
    Assert.Empty(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
  }

  [Fact]
  public async Task ForeignAndUnmarkedRequestsCannotReadPrivateStateOrCauseSideEffects() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    UserMessageReceipt accepted = await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, "private text", TestLlmSelection.Default));
    SessionSnapshot snapshot = await SettledAsync(server.Client, sessionId, accepted.ExecutionId!.Value);
    MessagePreview user = Assert.Single(snapshot.Messages, row => row.Message.Role == MessageRole.User);
    List<StoredSessionRow> sessions = StoredSessions.Read(database);
    List<StoredSubmission> decisions = StoredSubmissions.Read(database);
    List<StoredExecution> executions = StoredExecutions.Read(database);
    List<StoredRequest> requests = StoredExecutions.ReadRequests(database);
    string output = StoredLlmResponses.ReadText(database);
    string[] privateReads = [$"/api/sessions/{sessionId}/snapshot", $"{SubmissionsPath(sessionId)}/{accepted.Submission.SubmissionId}",
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
    foreach (string path in new[] { "/api/sessions", SubmissionsPath(sessionId) }) {
      using HttpResponseMessage simple = await RequestAsync(server.Client, HttpMethod.Post, path,
          new StringContent("{}"), marker: false, suppliedOrigin: "http://foreign.example");
      Assert.Equal(HttpStatusCode.Forbidden, simple.StatusCode);
      AssertPrivateResponse(simple);
      using HttpResponseMessage foreign = await RequestAsync(server.Client, HttpMethod.Post, path,
          JsonContent.Create(new UserMessageSubmission(Guid.NewGuid(), 0, "forbidden", TestLlmSelection.Default), options: json),
          suppliedOrigin: "http://foreign.example");
      Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
      AssertPrivateResponse(foreign);
    }
    using (var preflight = new HttpRequestMessage(HttpMethod.Options, SubmissionsPath(sessionId))) {
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
    Assert.Equal(decisions, StoredSubmissions.Read(database));
    Assert.Equal(executions, StoredExecutions.Read(database));
    Assert.Equal(requests, StoredExecutions.ReadRequests(database));
    Assert.Equal(output, StoredLlmResponses.ReadText(database));
  }

  [Fact]
  public async Task RestartPreservesHttpDecisionsContentAndExactRetryWithoutReplay() {
    using var database = new TemporaryDatabase();
    UserMessageReceipt accepted, refused;
    SessionSnapshot before;
    Guid sessionId;
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "retained over restart\0🙂", TestLlmSelection.Default);
    await using (var first = new RunningServer(Options(database))) {
      await first.WaitForReadyAsync();
      sessionId = await CreateAsync(first.Client);
      accepted = await SubmitAsync(first.Client, sessionId, submission);
      before = await SettledAsync(first.Client, sessionId, accepted.ExecutionId!.Value);
      refused = await SubmitAsync(first.Client, sessionId, new(Guid.NewGuid(), 1, "retained refusal", TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
    }
    List<StoredExecution> executions = StoredExecutions.Read(database);
    List<StoredRequest> requests = StoredExecutions.ReadRequests(database);
    string output = StoredLlmResponses.ReadText(database);
    await using var restarted = new RunningServer(Options(database));
    await restarted.WaitForReadyAsync();
    SessionSnapshot after = await SnapshotAsync(restarted.Client, sessionId);
    Assert.Equal(before.LastSettlement, after.LastSettlement);
    Assert.Equal(before.HistoryRevision, after.HistoryRevision);
    Assert.Equal(before.Messages, after.Messages);
    Assert.Equal(accepted, await ReceiptAsync(restarted.Client, sessionId, submission.SubmissionId));
    Assert.Equal(refused, await ReceiptAsync(restarted.Client, sessionId, refused.Submission.SubmissionId));
    Assert.Equal(accepted, await SubmitAsync(restarted.Client, sessionId, submission));
    Assert.Equal(refused, await SubmitAsync(restarted.Client, sessionId, refused.Submission));
    MessagePreview user = Assert.Single(after.Messages, row => row.Message.Role == MessageRole.User);
    Assert.Equal(submission.Text, (await ContentAsync(restarted.Client, sessionId, new(user.Message, 0, 0))).Text);
    Assert.Equal(executions, StoredExecutions.Read(database));
    Assert.Equal(requests, StoredExecutions.ReadRequests(database));
    Assert.Equal(output, StoredLlmResponses.ReadText(database));
    Assert.Equal(2, StoredSubmissions.Read(database).Count);
  }

  [Fact]
  public async Task RenamedWireContractRejectsLegacyRoutesAndRequiredFieldNames() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    Guid submissionId = Guid.NewGuid();
    string selection = JsonSerializer.Serialize(TestLlmSelection.Default, json);
    await AssertStatusAsync(server.Client, HttpMethod.Post, $"/api/sessions/{sessionId}/inputs",
        HttpStatusCode.NotFound, JsonBody("{}"));
    await AssertStatusAsync(server.Client, HttpMethod.Get, $"/api/sessions/{sessionId}/inputs/{submissionId}",
        HttpStatusCode.NotFound);
    await AssertStatusAsync(server.Client, HttpMethod.Post, $"/api/sessions/{sessionId}/runs/{Guid.NewGuid()}/stop",
        HttpStatusCode.NotFound, JsonBody("{}"));
    await AssertStatusAsync(server.Client, HttpMethod.Post, SubmissionsPath(sessionId), HttpStatusCode.BadRequest,
        JsonBody($"{{\"inputId\":\"{submissionId}\",\"expectedHistoryRevision\":0,\"text\":\"legacy\",\"llmSelection\":{selection}}}"));
    Assert.Empty(StoredSubmissions.Read(database));
    Assert.Null(await ReceiptAsync(server.Client, sessionId, submissionId));

    using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Post, SubmissionsPath(sessionId),
        JsonBody($"{{\"submissionId\":\"{submissionId}\",\"expectedHistoryRevision\":0,\"text\":\"new wire shape\",\"llmSelection\":{selection}}}"));
    UserMessageReceipt receipt = Assert.IsType<UserMessageReceipt>(await ReadAsync<UserMessageReceipt>(response));
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    AssertReceiptJson(body.RootElement, receipt);
    Assert.Equal(new(submissionId, 0, "new wire shape", TestLlmSelection.Default), receipt.Submission);
    SessionSnapshot snapshot = await SettledAsync(server.Client, sessionId, receipt.ExecutionId!.Value);
    MessagePreview user = Assert.Single(snapshot.Messages, row => row.Message.Role == MessageRole.User);
    string contentPath = ContentPath(sessionId, new(user.Message, 0, 0));
    await AssertStatusAsync(server.Client, HttpMethod.Get,
        contentPath.Replace("executionId=", "runId=", StringComparison.Ordinal), HttpStatusCode.BadRequest);
    Assert.Equal("new wire shape", (await ContentAsync(server.Client, sessionId, new(user.Message, 0, 0))).Text);
  }

  private static ServerOptions Options(TemporaryDatabase database) => new(Path.GetDirectoryName(database.Path)!, 0);

  private static HttpClient NewClient(Uri origin) =>
      new(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = origin, Timeout = TestTimeout };

  private static string SubmissionsPath(Guid sessionId) => $"/api/sessions/{sessionId}/message-submissions";

  private static string ContentPath(Guid sessionId, MessageContentCursor cursor) =>
      $"/api/sessions/{sessionId}/content?executionId={cursor.Message.ExecutionId}&messageId={cursor.Message.MessageId}"
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

  private static async Task<UserMessageReceipt> SubmitAsync(HttpClient client, Guid sessionId, UserMessageSubmission submission) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, SubmissionsPath(sessionId),
        JsonContent.Create(submission, options: json));
    UserMessageReceipt receipt = Assert.IsType<UserMessageReceipt>(await ReadAsync<UserMessageReceipt>(response));
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    AssertReceiptJson(body.RootElement, receipt);
    return receipt;
  }

  private static async Task<UserMessageReceipt?> ReceiptAsync(HttpClient client, Guid sessionId, Guid submissionId) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, $"{SubmissionsPath(sessionId)}/{submissionId}");
    UserMessageReceipt? receipt = await ReadAsync<UserMessageReceipt>(response);
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    if (receipt is null)
      Assert.Equal(JsonValueKind.Null, body.RootElement.ValueKind);
    else
      AssertReceiptJson(body.RootElement, receipt);
    return receipt;
  }

  private static async Task<SessionSnapshot> SnapshotAsync(HttpClient client, Guid sessionId) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, $"/api/sessions/{sessionId}/snapshot");
    SessionSnapshot snapshot = Assert.IsType<SessionSnapshot>(await ReadAsync<SessionSnapshot>(response));
    Assert.Equal(sessionId, snapshot.SessionId);
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    JsonElement root = body.RootElement;
    AssertProperties(root, "sessionId", "historyRevision", "admissionFenced", "unsettledExecutionId",
        "stopRequested", "lastSettlement", "messages", "hasOlderMessages", "latestLlmSelection");
    Assert.Equal(snapshot.UnsettledExecutionId, root.GetProperty("unsettledExecutionId").Deserialize<Guid?>());
    JsonElement settlement = root.GetProperty("lastSettlement");
    if (snapshot.LastSettlement is not null) {
      AssertProperties(settlement, "executionId", "outcome");
      Assert.Equal(snapshot.LastSettlement.ExecutionId, settlement.GetProperty("executionId").GetGuid());
    }
    foreach (JsonElement preview in root.GetProperty("messages").EnumerateArray()) {
      AssertMessageRefJson(preview.GetProperty("message"));
      AssertCursorJson(preview.GetProperty("availableThrough"));
    }
    return snapshot;
  }

  private static async Task<MessageContentPage> ContentAsync(HttpClient client, Guid sessionId, MessageContentCursor cursor) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, ContentPath(sessionId, cursor));
    MessageContentPage page = Assert.IsType<MessageContentPage>(await ReadAsync<MessageContentPage>(response));
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    foreach (string name in new[] { "from", "next", "availableThrough" }) {
      JsonElement position = body.RootElement.GetProperty(name);
      AssertCursorJson(position);
      Assert.Equal(cursor.Message.ExecutionId, position.GetProperty("message").GetProperty("executionId").GetGuid());
    }
    return page;
  }

  private static void AssertProperties(JsonElement value, params string[] names) =>
      Assert.Equal(names.Order(StringComparer.Ordinal),
          value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

  private static void AssertReceiptJson(JsonElement value, UserMessageReceipt receipt) {
    AssertProperties(value, "sessionId", "submission", "acceptance", "refusalReason", "executionId");
    Assert.Equal(receipt.SessionId, value.GetProperty("sessionId").GetGuid());
    Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(receipt.Acceptance.ToString()),
        value.GetProperty("acceptance").GetString());
    Assert.Equal(receipt.ExecutionId, value.GetProperty("executionId").Deserialize<Guid?>());
    Assert.Equal(receipt.RefusalReason, value.GetProperty("refusalReason").Deserialize<MessageRefusalReason?>(json));
    JsonElement submission = value.GetProperty("submission");
    AssertProperties(submission, "submissionId", "expectedHistoryRevision", "text", "llmSelection");
    Assert.Equal(receipt.Submission.SubmissionId, submission.GetProperty("submissionId").GetGuid());
    Assert.Equal(receipt.Submission.ExpectedHistoryRevision, submission.GetProperty("expectedHistoryRevision").GetInt64());
    Assert.Equal(receipt.Submission.Text, submission.GetProperty("text").GetString());
    Assert.Equal(receipt.Submission.LlmSelection,
        submission.GetProperty("llmSelection").Deserialize<Urd.Core.Providers.LlmSelection>(json));
  }

  private static void AssertMessageRefJson(JsonElement value) =>
      AssertProperties(value, "executionId", "messageId", "role", "historyRevision");

  private static void AssertCursorJson(JsonElement value) {
    AssertProperties(value, "message", "segmentIndex", "utf8Offset");
    AssertMessageRefJson(value.GetProperty("message"));
  }

  private static async Task<SessionSnapshot> SettledAsync(HttpClient client, Guid sessionId, Guid executionId) {
    using var deadline = new CancellationTokenSource(TestTimeout);
    SessionSnapshot snapshot = await SnapshotAsync(client, sessionId).WaitAsync(deadline.Token);
    while (snapshot.LastSettlement?.ExecutionId != executionId) {
      await Task.Delay(10, deadline.Token);
      snapshot = await SnapshotAsync(client, sessionId).WaitAsync(deadline.Token);
    }
    Assert.Null(snapshot.UnsettledExecutionId);
    return snapshot;
  }
}
