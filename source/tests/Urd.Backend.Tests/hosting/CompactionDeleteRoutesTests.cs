using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

public sealed class CompactionDeleteRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task DeleteCancelsQueuedOperationAndDuplicateReturnsSameReceipt() {
    using var database = new TemporaryDatabase();
    var entered = Signal();
    var release = Signal();
    int calls = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref calls) == 2
        ? Paused(entered, release, token) : Complete(token));
    await using var server = new RunningServer(RunningServer.OptionsForIsolatedWorkspace(database), createClient: () => client);
    try {
      await server.WaitForReadyAsync();
      Guid sessionId = await CreateSessionAsync(server.Client);
      UserMessageReceipt first = await SendAsync(server.Client, sessionId, new string('x', 100_000));
      await SettledAsync(first.ExecutionId!.Value, () => ReadSnapshotAsync(server.Client, sessionId));
      await SendAsync(server.Client, sessionId, "busy");
      await entered.Task.WaitAsync(TestTimeout);

      var request = new ManualCompactionRequest(Guid.NewGuid(), 0, TestLlmSelection.Default with { ContextWindow = 8_192 });
      string path = $"/api/sessions/{sessionId}/compactions/operations/{request.OperationId}";
      using (HttpResponseMessage queued = await RequestAsync(server.Client, HttpMethod.Post,
          $"/api/sessions/{sessionId}/compactions", JsonContent.Create(request, options: json))) {
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        Assert.True(Assert.IsType<CompactionReceipt>(await queued.Content.ReadFromJsonAsync<CompactionReceipt>(json)).Queued);
      }
      CompactionReceipt cancelled;
      using (HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Delete, path, JsonBody("{}"))) {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertPrivateResponse(response);
        cancelled = Assert.IsType<CompactionReceipt>(await response.Content.ReadFromJsonAsync<CompactionReceipt>(json));
        Assert.Equal(request.OperationId, cancelled.ExecutionId);
        Assert.False(cancelled.Queued);
        Assert.Equal(ExecutionOutcome.Cancelled, cancelled.Outcome);
      }
      using (HttpResponseMessage duplicate = await RequestAsync(server.Client, HttpMethod.Delete, path, JsonBody("{}"))) {
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        AssertPrivateResponse(duplicate);
        Assert.Equal(cancelled, await duplicate.Content.ReadFromJsonAsync<CompactionReceipt>(json));
      }
      Assert.Null((await ReadSnapshotAsync(server.Client, sessionId)).QueuedCompactionId);
    } finally {
      release.TrySetResult();
    }
  }

  [Fact]
  public async Task DeleteRejectsInvalidAndUnavailableOperations() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsForIsolatedWorkspace(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateSessionAsync(server.Client);
    string PathFor(Guid session, Guid operation) => $"/api/sessions/{session}/compactions/operations/{operation}";

    using (HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Delete,
        PathFor(sessionId, Guid.Empty), JsonBody("{}"))) {
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
      AssertPrivateResponse(invalid);
    }
    using (HttpResponseMessage missingSession = await RequestAsync(server.Client, HttpMethod.Delete,
        PathFor(Guid.NewGuid(), Guid.NewGuid()), JsonBody("{}"))) {
      Assert.Equal(HttpStatusCode.NotFound, missingSession.StatusCode);
      AssertPrivateResponse(missingSession);
    }
    using (HttpResponseMessage missingOperation = await RequestAsync(server.Client, HttpMethod.Delete,
        PathFor(sessionId, Guid.NewGuid()), JsonBody("{}"))) {
      Assert.Equal(HttpStatusCode.Conflict, missingOperation.StatusCode);
      AssertPrivateResponse(missingOperation);
    }
    using (HttpResponseMessage invalidBody = await RequestAsync(server.Client, HttpMethod.Delete,
        PathFor(sessionId, Guid.NewGuid()), JsonBody("{\"unexpected\":true}"))) {
      Assert.Equal(HttpStatusCode.BadRequest, invalidBody.StatusCode);
      AssertPrivateResponse(invalidBody);
    }
    using HttpResponseMessage unmarked = await RequestAsync(server.Client, HttpMethod.Delete,
        PathFor(sessionId, Guid.NewGuid()), JsonBody("{}"), marker: false);
    Assert.Equal(HttpStatusCode.Forbidden, unmarked.StatusCode);
    AssertPrivateResponse(unmarked);
  }

  private static async Task<Guid> CreateSessionAsync(HttpClient client) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, "/api/sessions", JsonBody("{}"));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return body.RootElement.GetProperty("sessionId").GetGuid();
  }

  private static async Task<UserMessageReceipt> SendAsync(HttpClient client, Guid sessionId, string text) {
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, text, TestLlmSelection.Default);
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post,
        $"/api/sessions/{sessionId}/message-submissions", JsonContent.Create(submission, options: json));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return Assert.IsType<UserMessageReceipt>(await response.Content.ReadFromJsonAsync<UserMessageReceipt>(json));
  }

  private static async Task<SessionSnapshot> ReadSnapshotAsync(HttpClient client, Guid sessionId) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, $"/api/sessions/{sessionId}/snapshot");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return Assert.IsType<SessionSnapshot>(await response.Content.ReadFromJsonAsync<SessionSnapshot>(json));
  }
}
