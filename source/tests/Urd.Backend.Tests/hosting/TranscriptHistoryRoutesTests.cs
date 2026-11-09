using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class TranscriptHistoryRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    PropertyNameCaseInsensitive = false,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task ReadsAllHistoryModesAsPrivateMetadataOnlyResponses() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateSessionAsync(server.Client);
    UserMessageReceipt receipt = await SendAsync(server.Client, sessionId, 0, "private prompt text");
    SessionSnapshot snapshot = await SettledAsync(receipt.ExecutionId!.Value,
        () => ReadSnapshotAsync(server.Client, sessionId));
    long revision = snapshot.HistoryRevision;

    string latestPath = HistoryPath(sessionId, "latest", revision);
    using (HttpResponseMessage latest = await RequestAsync(server.Client, HttpMethod.Get, latestPath)) {
      Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
      AssertPrivateResponse(latest);
      Assert.Equal("no-store", latest.Headers.CacheControl?.ToString());
      using JsonDocument body = JsonDocument.Parse(await latest.Content.ReadAsStringAsync());
      JsonElement root = body.RootElement;
      Assert.Equal(sessionId, root.GetProperty("sessionId").GetGuid());
      Assert.Equal(revision, root.GetProperty("historyRevision").GetInt64());
      JsonElement entries = root.GetProperty("entries");
      Assert.NotEmpty(entries.EnumerateArray());
      Assert.Equal("user", entries[0].GetProperty("entry").GetProperty("kind").GetString());
      Assert.Equal(receipt.ExecutionId, entries[0].GetProperty("entry").GetProperty("executionId").GetGuid());
      Assert.DoesNotContain("private prompt text", root.ToString(), StringComparison.Ordinal);
      Assert.DoesNotContain("Hello from Urd.", root.ToString(), StringComparison.Ordinal);
      Assert.All(entries.EnumerateArray(), entry => {
        Assert.False(entry.TryGetProperty("arguments", out _));
        Assert.False(entry.TryGetProperty("result", out _));
        if (entry.TryGetProperty("text", out JsonElement facts)) {
          Assert.False(facts.TryGetProperty("content", out _));
          Assert.False(facts.TryGetProperty("preview", out _));
        }
      });

      JsonElement user = entries[0].GetProperty("entry");
      string anchor = $"&executionId={user.GetProperty("executionId").GetGuid()}"
          + $"&entryId={user.GetProperty("entryId").GetGuid()}&kind=user";
      foreach (string mode in new[] { "before", "after", "around" }) {
        using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get,
            HistoryPath(sessionId, mode, revision) + anchor);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertPrivateResponse(response);
        TranscriptHistoryPage page = Assert.IsType<TranscriptHistoryPage>(
            await response.Content.ReadFromJsonAsync<TranscriptHistoryPage>(json));
        Assert.Equal(revision, page.HistoryRevision);
      }
    }

    using HttpResponseMessage unmarked = await RequestAsync(server.Client, HttpMethod.Get, latestPath, marker: false);
    Assert.Equal(HttpStatusCode.Forbidden, unmarked.StatusCode);
    AssertPrivateResponse(unmarked);
  }

  [Fact]
  public async Task RevisionConflictPrecedesMissingAnchorAndWrongSessionAnchorIsNotReplacedByLatest() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid firstSession = await CreateSessionAsync(server.Client);
    UserMessageReceipt receipt = await SendAsync(server.Client, firstSession, 0, "first");
    SessionSnapshot firstSnapshot = await SettledAsync(receipt.ExecutionId!.Value,
        () => ReadSnapshotAsync(server.Client, firstSession));
    TranscriptHistoryPage firstPage = await ReadHistoryAsync(server.Client,
        HistoryPath(firstSession, "latest", firstSnapshot.HistoryRevision));
    TranscriptEntryRef anchor = Assert.Single(firstPage.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.User).Entry;

    string missingAnchor = $"&executionId={Guid.NewGuid()}&entryId={Guid.NewGuid()}&kind=user";
    using (HttpResponseMessage conflict = await RequestAsync(server.Client, HttpMethod.Get,
        HistoryPath(firstSession, "around", firstSnapshot.HistoryRevision + 1) + missingAnchor))
      Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

    Guid otherSession = await CreateSessionAsync(server.Client);
    using (HttpResponseMessage notFound = await RequestAsync(server.Client, HttpMethod.Get,
        HistoryPath(otherSession, "around", 0) + $"&executionId={anchor.ExecutionId}&entryId={anchor.EntryId}&kind=user"))
      Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
  }

  [Theory]
  [InlineData("unknown", "&expectedHistoryRevision=0")]
  [InlineData("latest", "")]
  [InlineData("latest", "&expectedHistoryRevision=0&executionId=00000000-0000-0000-0000-000000000001")]
  [InlineData("before", "&expectedHistoryRevision=0&executionId=00000000-0000-0000-0000-000000000001")]
  [InlineData("after", "&expectedHistoryRevision=0&entryId=00000000-0000-0000-0000-000000000001&kind=user")]
  [InlineData("around", "&expectedHistoryRevision=0&executionId=00000000-0000-0000-0000-000000000001&entryId=00000000-0000-0000-0000-000000000001&kind=invalid")]
  public async Task RejectsInvalidModesMissingRevisionAndPartialOrInvalidAnchors(string mode, string query) {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateSessionAsync(server.Client);

    using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{sessionId}/history?mode={mode}{query}");
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    AssertPrivateResponse(response);
  }

  [Fact]
  public async Task NormalSendAdmitsUserEntryFirstInHistory() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateSessionAsync(server.Client);
    UserMessageReceipt receipt = await SendAsync(server.Client, sessionId, 0, "admission order");
    SessionSnapshot snapshot = await SettledAsync(receipt.ExecutionId!.Value,
        () => ReadSnapshotAsync(server.Client, sessionId));
    TranscriptHistoryPage page = await ReadHistoryAsync(server.Client,
        HistoryPath(sessionId, "latest", snapshot.HistoryRevision));

    Assert.Equal(TranscriptEntryKind.User, page.Entries[0].Entry.Kind);
    Assert.Equal(receipt.ExecutionId, page.Entries[0].Entry.ExecutionId);
  }

  private static ServerOptions Options(TemporaryDatabase database) => new(Path.GetDirectoryName(database.Path)!, 0);

  private static async Task<Guid> CreateSessionAsync(HttpClient client) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, "/api/sessions", JsonBody("{}"));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    AssertPrivateResponse(response);
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return body.RootElement.GetProperty("sessionId").GetGuid();
  }

  private static async Task<UserMessageReceipt> SendAsync(HttpClient client, Guid sessionId, long revision, string text) {
    var submission = new UserMessageSubmission(Guid.NewGuid(), revision, text, TestLlmSelection.Default);
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post,
        $"/api/sessions/{sessionId}/message-submissions", JsonContent.Create(submission, options: json));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertPrivateResponse(response);
    return Assert.IsType<UserMessageReceipt>(await response.Content.ReadFromJsonAsync<UserMessageReceipt>(json));
  }

  private static async Task<TranscriptHistoryPage> ReadHistoryAsync(HttpClient client, string path) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, path);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertPrivateResponse(response);
    return Assert.IsType<TranscriptHistoryPage>(await response.Content.ReadFromJsonAsync<TranscriptHistoryPage>(json));
  }

  private static async Task<SessionSnapshot> ReadSnapshotAsync(HttpClient client, Guid sessionId) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, $"/api/sessions/{sessionId}/snapshot");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return Assert.IsType<SessionSnapshot>(await response.Content.ReadFromJsonAsync<SessionSnapshot>(json));
  }

  private static string HistoryPath(Guid sessionId, string mode, long revision) =>
      $"/api/sessions/{sessionId}/history?mode={mode}&expectedHistoryRevision={revision}";
}
