using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
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
    MessagePreview user = Assert.Single(snapshot.Messages, row => row.Message.Role == MessageRole.User);
    Assert.True(user.Truncated);
    var cursor = new MessageContentCursor(user.Message, 0, 0);
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

  private static ServerOptions Options(TemporaryDatabase database) => new(Path.GetDirectoryName(database.Path)!, 0);

  private static async Task<SessionSnapshot> ReadSnapshotAsync(HttpClient client, Guid sessionId) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, $"/api/sessions/{sessionId}/snapshot");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertPrivateResponse(response);
    return Assert.IsType<SessionSnapshot>(await response.Content.ReadFromJsonAsync<SessionSnapshot>(json));
  }
}
