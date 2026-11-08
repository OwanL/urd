using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
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
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

// Real HTTP admission must not transfer execution ownership to a connection or bypass host drain.
public sealed class SessionHttpLifetimeTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task DisconnectWithoutReadingAcknowledgementRetainsWorkAndOriginalRetryDoesNotReplay() {
    var entered = Signal();
    var finish = Signal();
    CancellationToken llmCancellation = default;
    var client = new ScriptedChatClient(token => {
      llmCancellation = token;
      return Paused(entered, finish, token);
    });
    var database = new TemporaryDatabase();
    var server = new RunningServer(new ServerOptions(Path.GetDirectoryName(database.Path)!, 0),
        createClient: () => client);
    try {
      Uri origin = await server.WaitForReadyAsync();
      server.Client.DefaultRequestHeaders.Add("X-Urd-Client", "browser");
      Guid sessionId = await CreateSessionAsync(server.Client);
      var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "survives a disconnected sender", TestLlmSelection.Default);
      await SendWithoutReadingAcknowledgementAsync(origin, sessionId, submission, entered.Task);

      UserMessageReceipt receipt = (await server.Client.GetFromJsonAsync<UserMessageReceipt>(
          $"/api/sessions/{sessionId}/message-submissions/{submission.SubmissionId}", json))!;
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      Assert.Equal(submission, receipt.Submission);
      Assert.False(llmCancellation.IsCancellationRequested);
      using HttpResponseMessage retry = await server.Client.PostAsJsonAsync(
          $"/api/sessions/{sessionId}/message-submissions", submission, json);
      Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
      Assert.Equal(receipt, await retry.Content.ReadFromJsonAsync<UserMessageReceipt>(json));
      Assert.Equal(1, client.Calls);
      Assert.Null(Assert.Single(StoredExecutions.Read(database)).Outcome);

      finish.TrySetResult();
      SessionSnapshot settled = await WaitForSettlementAsync(server.Client, sessionId, receipt.ExecutionId!.Value);
      Assert.Equal(ExecutionOutcome.Completed, settled.LastSettlement!.Outcome);
      Assert.Equal("visible", StoredLlmResponses.ReadText(database));
      Assert.Single(StoredSubmissions.Read(database));
      Assert.Single(StoredExecutions.ReadRequests(database));
      Assert.Equal(1, client.Calls);
    } finally {
      finish.TrySetResult();
      await server.DisposeAsync();
      database.Dispose();
    }

    Assert.Equal(1, client.DisposeCalls);
  }

  [Fact]
  public async Task HostStopFencesHttpAndRetainsLeaseAndClientUntilAdmittedLlmCleanupFinishes() {
    var entered = Signal();
    var cleanupEntered = Signal();
    var releaseCleanup = Signal();
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      try {
        yield return Text("started");
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
      } finally {
        cleanupEntered.TrySetResult();
        await releaseCleanup.Task;
      }
    }

    var client = new ScriptedChatClient(token => Response(token));
    var database = new TemporaryDatabase();
    string dataRoot = Path.GetDirectoryName(database.Path)!;
    var server = new RunningServer(new ServerOptions(dataRoot, 0), createClient: () => client);
    try {
      await server.WaitForReadyAsync();
      server.Client.DefaultRequestHeaders.Add("X-Urd-Client", "browser");
      Guid sessionId = await CreateSessionAsync(server.Client);
      var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "stop after HTTP admission", TestLlmSelection.Default);
      using HttpResponseMessage sent = await server.Client.PostAsJsonAsync(
          $"/api/sessions/{sessionId}/message-submissions", submission, json);
      Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
      UserMessageReceipt receipt = (await sent.Content.ReadFromJsonAsync<UserMessageReceipt>(json))!;
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      await entered.Task.WaitAsync(TestTimeout);

      server.Cancel();
      Task stopped = server.WaitForCompletionAsync();
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.False(stopped.IsCompleted);
      Assert.Equal(0, client.DisposeCalls);
      Assert.Throws<IOException>(() => {
        using var lease = new FileStream(Path.Combine(dataRoot, "urd.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
      });
      StoredExecution pending = Assert.Single(StoredExecutions.Read(database));
      Assert.True(pending.Stopping);
      Assert.Null(pending.Outcome);
      await AssertCreationBlockedAsync(server.Client);
      Assert.Single(StoredSessions.Read(database));
      Assert.Single(StoredSubmissions.Read(database));
      Assert.False(stopped.IsCompleted);

      releaseCleanup.TrySetResult();
      await stopped;
      Assert.Equal(1, client.DisposeCalls);
      Assert.Equal(ExecutionOutcome.Cancelled, Assert.Single(StoredExecutions.Read(database)).Outcome);
      using var releasedLease = new FileStream(Path.Combine(dataRoot, "urd.lock"),
          FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    } finally {
      releaseCleanup.TrySetResult();
      await server.DisposeAsync();
      database.Dispose();
    }
  }

  private static async Task<Guid> CreateSessionAsync(HttpClient client) {
    using var body = new StringContent("{}", Encoding.UTF8, "application/json");
    using HttpResponseMessage response = await client.PostAsync("/api/sessions", body);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    JsonElement created = await response.Content.ReadFromJsonAsync<JsonElement>();
    return created.GetProperty("sessionId").GetGuid();
  }

  private static async Task SendWithoutReadingAcknowledgementAsync(Uri origin, Guid sessionId,
      UserMessageSubmission submission, Task llmStarted) {
    using var deadline = new CancellationTokenSource(TestTimeout);
    using var connection = new TcpClient();
    await connection.ConnectAsync(origin.Host, origin.Port, deadline.Token);
    byte[] body = JsonSerializer.SerializeToUtf8Bytes(submission, json);
    string headers = $"POST /api/sessions/{sessionId}/message-submissions HTTP/1.1\r\n" +
        $"Host: {origin.Authority}\r\nX-Urd-Client: browser\r\nContent-Type: application/json\r\n" +
        $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
    await using NetworkStream stream = connection.GetStream();
    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token);
    await stream.WriteAsync(body, deadline.Token);
    await llmStarted.WaitAsync(deadline.Token);
    // Closing without reading a single reply byte leaves the sender's outcome unknown.
  }

  private static async Task<SessionSnapshot> WaitForSettlementAsync(HttpClient client, Guid sessionId, Guid executionId) {
    using var deadline = new CancellationTokenSource(TestTimeout);
    SessionSnapshot? snapshot = null;
    while (snapshot?.LastSettlement?.ExecutionId != executionId) {
      snapshot = await client.GetFromJsonAsync<SessionSnapshot>($"/api/sessions/{sessionId}/snapshot",
          json, deadline.Token);
      if (snapshot?.LastSettlement?.ExecutionId != executionId)
        await Task.Delay(10, deadline.Token);
    }

    return snapshot;
  }

  private static async Task AssertCreationBlockedAsync(HttpClient client) {
    try {
      using var body = new StringContent("{}", Encoding.UTF8, "application/json");
      using HttpResponseMessage response = await client.PostAsync("/api/sessions", body);
      Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    } catch (HttpRequestException) {
      // Kestrel may have already closed its listener rather than returning the gate's 503.
    }
  }
}
