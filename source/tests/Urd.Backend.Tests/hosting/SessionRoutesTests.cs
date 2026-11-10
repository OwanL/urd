using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

// Keeps the real HTTP wire contract for session commands and bounded message content.
public sealed class SessionRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    PropertyNameCaseInsensitive = false,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task CreateSendReceiptSnapshotAndContentKeepTheirWireShape() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();

    using HttpResponseMessage created = await RequestAsync(server.Client, HttpMethod.Post, "/api/sessions", JsonBody("{}"));
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    AssertPrivateResponse(created);
    using JsonDocument createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
    Guid sessionId = createdBody.RootElement.GetProperty("sessionId").GetGuid();
    Assert.Equal($"/api/sessions/{sessionId}/snapshot", created.Headers.Location?.OriginalString);

    using (HttpResponseMessage emptySummary = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/positions?expectedHistoryRevision=0")) {
      Assert.Equal(HttpStatusCode.OK, emptySummary.StatusCode);
      using JsonDocument body = JsonDocument.Parse(await emptySummary.Content.ReadAsStringAsync());
      Assert.Equal(0L, body.RootElement.GetProperty("entryCount").GetInt64());
      Assert.Equal(0L, body.RootElement.GetProperty("totalWeight").GetInt64());
    }
    using (HttpResponseMessage emptyLocate = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/positions/locate?expectedHistoryRevision=0&fraction=1")) {
      Assert.Equal(HttpStatusCode.OK, emptyLocate.StatusCode);
      using JsonDocument body = JsonDocument.Parse(await emptyLocate.Content.ReadAsStringAsync());
      Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("target").ValueKind);
      Assert.Empty(body.RootElement.GetProperty("entries").EnumerateArray());
    }
    using (HttpResponseMessage emptyPrompts = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/positions/prompts?expectedHistoryRevision=0")) {
      Assert.Equal(HttpStatusCode.OK, emptyPrompts.StatusCode);
      using JsonDocument body = JsonDocument.Parse(await emptyPrompts.Content.ReadAsStringAsync());
      Assert.Empty(body.RootElement.GetProperty("prompts").EnumerateArray());
      Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("nextAfterRank").ValueKind);
    }

    using (HttpResponseMessage absent = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/message-submissions/{Guid.NewGuid()}")) {
      Assert.Equal(HttpStatusCode.OK, absent.StatusCode);
      AssertPrivateResponse(absent);
      using JsonDocument body = JsonDocument.Parse(await absent.Content.ReadAsStringAsync());
      Assert.Equal(JsonValueKind.Null, body.RootElement.ValueKind);
    }

    LlmSelection selection = TestLlmSelection.Default with { ReasoningEffort = "high" };
    string text = string.Concat(Enumerable.Repeat("wire content é🙂", 1200));
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, text, selection);
    using HttpResponseMessage sent = await RequestAsync(server.Client, HttpMethod.Post,
        $"/api/sessions/{sessionId}/message-submissions", JsonContent.Create(submission, options: json));
    Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
    AssertPrivateResponse(sent);
    Assert.Equal("application/json", sent.Content.Headers.ContentType?.MediaType);
    UserMessageReceipt receipt = Assert.IsType<UserMessageReceipt>(await sent.Content.ReadFromJsonAsync<UserMessageReceipt>(json));
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    Assert.Equal(submission, receipt.Submission);
    using (JsonDocument body = JsonDocument.Parse(await sent.Content.ReadAsStringAsync())) {
      JsonElement wireSelection = body.RootElement.GetProperty("submission").GetProperty("llmSelection");
      Assert.Equal(selection, wireSelection.Deserialize<LlmSelection>(json));
      Assert.Equal("high", wireSelection.GetProperty("reasoningEffort").GetString());
      Assert.Equal("custom", wireSelection.GetProperty("connection").GetProperty("route").GetString());
      Assert.Equal("openAIResponses", wireSelection.GetProperty("connection").GetProperty("api").GetString());
    }

    using (HttpResponseMessage retry = await RequestAsync(server.Client, HttpMethod.Post,
        $"/api/sessions/{sessionId}/message-submissions", JsonContent.Create(submission, options: json))) {
      Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
      AssertPrivateResponse(retry);
      Assert.Equal(receipt, await retry.Content.ReadFromJsonAsync<UserMessageReceipt>(json));
    }
    using (HttpResponseMessage conflict = await RequestAsync(server.Client, HttpMethod.Post,
        $"/api/sessions/{sessionId}/message-submissions",
        JsonContent.Create(submission with { Text = "changed" }, options: json))) {
      Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
      AssertPrivateResponse(conflict);
    }
    using (HttpResponseMessage retained = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/message-submissions/{submission.SubmissionId}")) {
      Assert.Equal(receipt, await retained.Content.ReadFromJsonAsync<UserMessageReceipt>(json));
    }

    SessionSnapshot snapshot = await HttpTest.SettledAsync(receipt.ExecutionId!.Value, () => ReadSnapshotAsync(server.Client, sessionId));
    Assert.Equal(selection, snapshot.LatestLlmSelection);
    Assert.NotNull(snapshot.FirstUserPreview);
    Assert.InRange(snapshot.FirstUserPreview.Length, 1, 256);
    Assert.StartsWith(snapshot.FirstUserPreview, text, StringComparison.Ordinal);

    string positionPath = $"/api/sessions/{sessionId}/positions?expectedHistoryRevision={snapshot.HistoryRevision}";
    using HttpResponseMessage summaryResponse = await RequestAsync(server.Client, HttpMethod.Get, positionPath);
    Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
    AssertPrivateResponse(summaryResponse);
    using JsonDocument summary = JsonDocument.Parse(await summaryResponse.Content.ReadAsStringAsync());
    JsonElement state = summary.RootElement;
    Assert.Equal(sessionId, state.GetProperty("sessionId").GetGuid());
    Assert.Equal(snapshot.HistoryRevision, state.GetProperty("historyRevision").GetInt64());
    Assert.True(state.GetProperty("positionVersion").GetInt64() > 0);
    Assert.True(state.GetProperty("entryCount").GetInt64() > 0);
    Assert.True(state.GetProperty("totalWeight").GetInt64() > 0);

    using HttpResponseMessage located = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/positions/locate?expectedHistoryRevision={snapshot.HistoryRevision}&fraction=0");
    Assert.Equal(HttpStatusCode.OK, located.StatusCode);
    AssertPrivateResponse(located);
    using JsonDocument locatedBody = JsonDocument.Parse(await located.Content.ReadAsStringAsync());
    JsonElement target = locatedBody.RootElement.GetProperty("target");
    Assert.Equal(submission.SubmissionId, target.GetProperty("entryId").GetGuid());
    Assert.Equal("user", target.GetProperty("kind").GetString());
    JsonElement firstPositioned = locatedBody.RootElement.GetProperty("entries")[0];
    Assert.Equal(submission.SubmissionId,
        firstPositioned.GetProperty("descriptor").GetProperty("entry").GetProperty("entryId").GetGuid());
    Assert.True(firstPositioned.GetProperty("position").GetProperty("rank").GetInt64() >= 0);

    using HttpResponseMessage weighted = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/positions/locate?expectedHistoryRevision={snapshot.HistoryRevision}&fraction=0.5");
    Assert.Equal(HttpStatusCode.OK, weighted.StatusCode);
    using JsonDocument weightedBody = JsonDocument.Parse(await weighted.Content.ReadAsStringAsync());
    JsonElement weightedTarget = weightedBody.RootElement.GetProperty("target");
    long offset = (long)Math.Floor(state.GetProperty("totalWeight").GetInt64() * 0.5);
    long prefixWeight = weightedTarget.GetProperty("prefixWeight").GetInt64();
    long targetWeight = weightedTarget.GetProperty("weight").GetInt64();
    Assert.InRange(offset, prefixWeight, prefixWeight + targetWeight - 1);

    using HttpResponseMessage prompts = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/positions/prompts?expectedHistoryRevision={snapshot.HistoryRevision}&limit=1");
    Assert.Equal(HttpStatusCode.OK, prompts.StatusCode);
    AssertPrivateResponse(prompts);
    using JsonDocument promptBody = JsonDocument.Parse(await prompts.Content.ReadAsStringAsync());
    JsonElement prompt = Assert.Single(promptBody.RootElement.GetProperty("prompts").EnumerateArray());
    Assert.Equal(receipt.ExecutionId, prompt.GetProperty("executionId").GetGuid());
    Assert.Equal(submission.SubmissionId, prompt.GetProperty("entryId").GetGuid());
    Assert.Equal(JsonValueKind.Null, promptBody.RootElement.GetProperty("nextAfterRank").ValueKind);

    using HttpResponseMessage activated = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/positions/prompts/{receipt.ExecutionId}/{submission.SubmissionId}"
        + $"?expectedHistoryRevision={snapshot.HistoryRevision}");
    Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
    AssertPrivateResponse(activated);
    using JsonDocument activatedBody = JsonDocument.Parse(await activated.Content.ReadAsStringAsync());
    Assert.Equal(submission.SubmissionId, activatedBody.RootElement.GetProperty("target").GetProperty("entryId").GetGuid());

    foreach (string fraction in new[] { "-0.1", "1.1", "NaN", "Infinity" }) {
      using HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Get,
          $"/api/sessions/{sessionId}/positions/locate?expectedHistoryRevision={snapshot.HistoryRevision}&fraction={fraction}");
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    foreach (string query in new[] { "limit=0", "limit=61", "limit=", "afterRank=-1", "afterRank=" }) {
      using HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Get,
          $"/api/sessions/{sessionId}/positions/prompts?expectedHistoryRevision={snapshot.HistoryRevision}&{query}");
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    using (HttpResponseMessage stale = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/positions?expectedHistoryRevision={snapshot.HistoryRevision + 1}"))
      Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    using (HttpResponseMessage missingSession = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{Guid.NewGuid()}/positions?expectedHistoryRevision=0"))
      Assert.Equal(HttpStatusCode.NotFound, missingSession.StatusCode);
    using HttpResponseMessage otherCreated = await RequestAsync(server.Client, HttpMethod.Post,
        "/api/sessions", JsonBody("{}"));
    using JsonDocument otherSessionBody = JsonDocument.Parse(await otherCreated.Content.ReadAsStringAsync());
    Guid otherSessionId = otherSessionBody.RootElement.GetProperty("sessionId").GetGuid();
    using (HttpResponseMessage wrongSession = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{otherSessionId}/positions/prompts/{receipt.ExecutionId}/{submission.SubmissionId}"
        + $"?expectedHistoryRevision=0"))
      Assert.Equal(HttpStatusCode.NotFound, wrongSession.StatusCode);

    using HttpResponseMessage historyResponse = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/history?mode=latest&expectedHistoryRevision={snapshot.HistoryRevision}");
    TranscriptHistoryPage history = (await historyResponse.Content.ReadFromJsonAsync<TranscriptHistoryPage>(json))!;
    TranscriptEntryRef textEntry = Assert.Single(history.Entries,
        entry => entry.Entry.Kind == TranscriptEntryKind.Text).Entry;
    using (HttpResponseMessage wrongKind = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/positions/prompts/{textEntry.ExecutionId}/{textEntry.EntryId}"
        + $"?expectedHistoryRevision={snapshot.HistoryRevision}"))
      Assert.Equal(HttpStatusCode.NotFound, wrongKind.StatusCode);

    var message = new MessageRef(receipt.ExecutionId.Value, submission.SubmissionId, MessageRole.User, snapshot.HistoryRevision);
    var cursor = new MessageContentCursor(message, 0, 0);
    string contentPath = $"/api/sessions/{sessionId}/content?executionId={cursor.Message.ExecutionId}"
        + $"&messageId={cursor.Message.MessageId}&role=user&historyRevision={cursor.Message.HistoryRevision}"
        + "&segmentIndex=0&utf8Offset=0";
    using HttpResponseMessage content = await RequestAsync(server.Client, HttpMethod.Get, contentPath);
    Assert.Equal(HttpStatusCode.OK, content.StatusCode);
    AssertPrivateResponse(content);
    MessageContentPage page = Assert.IsType<MessageContentPage>(await content.Content.ReadFromJsonAsync<MessageContentPage>(json));
    Assert.Equal(cursor, page.From);
    Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(page.Text), 1, 16 * 1024);
    Assert.True(page.HasMore);
    var savedText = new System.Text.StringBuilder(page.Text);
    while (page.HasMore) {
      MessageContentCursor next = page.Next;
      string nextPath = $"/api/sessions/{sessionId}/content?executionId={next.Message.ExecutionId}"
          + $"&messageId={next.Message.MessageId}&role=user&historyRevision={next.Message.HistoryRevision}"
          + $"&segmentIndex={next.SegmentIndex}&utf8Offset={next.Utf8Offset}";
      using HttpResponseMessage remainder = await RequestAsync(server.Client, HttpMethod.Get, nextPath);
      Assert.Equal(HttpStatusCode.OK, remainder.StatusCode);
      page = Assert.IsType<MessageContentPage>(await remainder.Content.ReadFromJsonAsync<MessageContentPage>(json));
      Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(page.Text), 1, 16 * 1024);
      savedText.Append(page.Text);
    }
    Assert.Equal(text, savedText.ToString());
  }

  [Fact]
  public async Task NotSentListingSurvivesLaterRunAndReloadWithSourceOnlyOnReceiptRoute() {
    using var database = new TemporaryDatabase();
    Guid sessionId, run = Guid.NewGuid();
    string text = string.Concat(Enumerable.Repeat("not sent 🙂", 4000));
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, text, TestLlmSelection.Default);
    UserMessageReceipt receipt;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      sessionId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      receipt = store.UserMessages.Admit(sessionId, 0, submission, true, run).Receipt;
      Guid request = store.Executions.ClaimNextRequest(sessionId, run, 0)!.RequestId;
      Assert.True(store.LlmResponses.CompleteRequest(sessionId, run, 0, new(request, ExecutionOutcome.Failed)));
      Assert.NotNull(store.Executions.TrySettle(sessionId, run, ExecutionOutcome.Failed));
    }

    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    var later = new UserMessageSubmission(Guid.NewGuid(), 0, "later run", TestLlmSelection.Default);
    using (HttpResponseMessage accepted = await RequestAsync(server.Client, HttpMethod.Post,
        $"/api/sessions/{sessionId}/message-submissions", JsonContent.Create(later, options: json))) {
      UserMessageReceipt next = (await accepted.Content.ReadFromJsonAsync<UserMessageReceipt>(json))!;
      await HttpTest.SettledAsync(next.ExecutionId!.Value, () => ReadSnapshotAsync(server.Client, sessionId));
    }
    using (HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/not-sent")) {
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      AssertPrivateResponse(response);
      NotSentPage page = (await response.Content.ReadFromJsonAsync<NotSentPage>(json))!;
      NotSentInput input = Assert.Single(page.Inputs);
      Assert.Equal(submission.SubmissionId, input.SubmissionId);
      Assert.Equal(run, input.ExecutionId);
      Assert.InRange(input.Preview.Length, 1, 256);
      Assert.StartsWith(input.Preview, text, StringComparison.Ordinal);
      Assert.Null(page.NextCursor);
      Assert.DoesNotContain(text, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
    using (HttpResponseMessage source = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/message-submissions/{submission.SubmissionId}"))
      Assert.Equal(receipt, await source.Content.ReadFromJsonAsync<UserMessageReceipt>(json));
    using (HttpResponseMessage retry = await RequestAsync(server.Client, HttpMethod.Post,
        $"/api/sessions/{sessionId}/message-submissions", JsonContent.Create(submission, options: json)))
      Assert.Equal(receipt, await retry.Content.ReadFromJsonAsync<UserMessageReceipt>(json));
    foreach (string cursor in new[] { "", "-1", "0", "9223372036854775808" }) {
      using HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Get,
          $"/api/sessions/{sessionId}/not-sent?cursor={cursor}");
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    using HttpResponseMessage missing = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{Guid.NewGuid()}/not-sent");
    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
  }

  private static ServerOptions Options(TemporaryDatabase database) => new(Path.GetDirectoryName(database.Path)!, 0);

  private static async Task<SessionSnapshot> ReadSnapshotAsync(HttpClient client, Guid sessionId) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, $"/api/sessions/{sessionId}/snapshot");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertPrivateResponse(response);
    return Assert.IsType<SessionSnapshot>(await response.Content.ReadFromJsonAsync<SessionSnapshot>(json));
  }
}
