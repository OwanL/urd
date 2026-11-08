using System.Net;
using System.Net.Sockets;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

// Real loopback listeners exercise the production composition and its resource ownership.
public sealed class ServerLifecycleTests {
  [Fact]
  public async Task ReadyOriginServesIndependentClientsAndEnforcesHealthAdmission() {
    var database = new TemporaryDatabase("urd server lifecycle data");
    var server = new RunningServer(Options(database));
    try {
      Uri origin = await server.WaitForReadyAsync();
      Assert.Equal("http", origin.Scheme);
      Assert.Equal("127.0.0.1", origin.Host);
      Assert.InRange(origin.Port, 1, 65535);
      using var independent = NewClient(origin);
      await Task.WhenAll(AssertHealthAsync(server.Client), AssertHealthAsync(independent));
      await AssertHealthAsync(independent, HttpMethod.Head);

      using var allowedOrigin = new HttpRequestMessage(HttpMethod.Get, "/health");
      allowedOrigin.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
      await AssertStatusAsync(server.Client, allowedOrigin, HttpStatusCode.NoContent);
      await AssertStatusAsync(server.Client, new(HttpMethod.Post, "/health"), HttpStatusCode.MethodNotAllowed);
      await AssertStatusAsync(server.Client, new(HttpMethod.Get, "/health") {
        Content = new StringContent("body")
      }, HttpStatusCode.BadRequest);
      await AssertStatusAsync(server.Client, new(HttpMethod.Get, "/api/unknown"), HttpStatusCode.Forbidden);
      using var unknownApi = new HttpRequestMessage(HttpMethod.Get, "/api/unknown");
      unknownApi.Headers.Add("X-Urd-Client", "browser");
      await AssertStatusAsync(server.Client, unknownApi, HttpStatusCode.NotFound);

      using var hostileOrigin = new HttpRequestMessage(HttpMethod.Get, "/health");
      hostileOrigin.Headers.Add("Origin", "http://foreign.example");
      await AssertStatusAsync(server.Client, hostileOrigin, HttpStatusCode.Forbidden);
      using var hostileHost = new HttpRequestMessage(HttpMethod.Get, "/health");
      hostileHost.Headers.Host = $"foreign.example:{origin.Port}";
      await AssertStatusAsync(server.Client, hostileHost, HttpStatusCode.Forbidden);
    } finally {
      await server.DisposeAsync();
      database.Dispose();
    }
  }

  [Fact]
  public async Task SeparateRootsRunTogetherWhileSameRootFailsWithoutDisturbingItsOwner() {
    var firstDatabase = new TemporaryDatabase("urd first server data");
    var secondDatabase = new TemporaryDatabase("urd second server data");
    var first = new RunningServer(Options(firstDatabase));
    var second = new RunningServer(Options(secondDatabase));
    var competing = new RunningServer(Options(firstDatabase));
    try {
      Uri[] origins = await Task.WhenAll(first.WaitForReadyAsync(), second.WaitForReadyAsync());
      Assert.NotEqual(origins[0].Port, origins[1].Port);
      await Assert.ThrowsAsync<IOException>(competing.WaitForCompletionAsync);
      Assert.False(competing.IsReady);
      await Task.WhenAll(AssertHealthAsync(first.Client), AssertHealthAsync(second.Client));
      Assert.Single(Directory.GetFiles(DataRoot(firstDatabase), "urd.sqlite"));
      Assert.Single(Directory.GetFiles(DataRoot(secondDatabase), "urd.sqlite"));
    } finally {
      await Task.WhenAll(first.DisposeAsync().AsTask(), second.DisposeAsync().AsTask(),
          competing.DisposeAsync().AsTask());
      firstDatabase.Dispose();
      secondDatabase.Dispose();
    }
  }

  [Fact]
  public async Task CancellationClosesListenerReleasesLeaseAndRestartPreservesSettledHistoryWithoutReplay() {
    var database = new TemporaryDatabase("urd persisted server data");
    RunningServer? first = null, restarted = null;
    try {
      await SeedHistoryAsync(database);
      List<StoredSessionRow> sessions = StoredSessions.Read(database);
      List<StoredSubmission> decisions = StoredSubmissions.Read(database);
      List<StoredExecution> executions = StoredExecutions.Read(database);
      List<StoredRequest> requests = StoredExecutions.ReadRequests(database);
      string output = StoredLlmResponses.ReadText(database);
      Assert.Equal("done", output);

      first = new RunningServer(Options(database));
      Uri firstOrigin = await first.WaitForReadyAsync();
      await AssertHealthAsync(first.Client);
      first.Cancel();
      await first.WaitForCompletionAsync();
      await AssertListenerClosedAsync(firstOrigin);
      AssertLeaseFree(database);
      Assert.Equal(sessions, StoredSessions.Read(database));
      Assert.Equal(decisions, StoredSubmissions.Read(database));

      restarted = new RunningServer(Options(database));
      Uri restartedOrigin = await restarted.WaitForReadyAsync();
      await AssertHealthAsync(restarted.Client);
      restarted.Cancel();
      await restarted.WaitForCompletionAsync();
      await AssertListenerClosedAsync(restartedOrigin);
      AssertLeaseFree(database);
      Assert.Equal(sessions, StoredSessions.Read(database));
      Assert.Equal(decisions, StoredSubmissions.Read(database));
      Assert.Equal(executions, StoredExecutions.Read(database));
      Assert.Equal(requests, StoredExecutions.ReadRequests(database));
      Assert.Equal(output, StoredLlmResponses.ReadText(database));

    } finally {
      await Task.WhenAll(first?.DisposeAsync().AsTask() ?? Task.CompletedTask,
          restarted?.DisposeAsync().AsTask() ?? Task.CompletedTask);
      database.Dispose();
    }
  }

  [Fact]
  public async Task ThrowingReadyCallbackPropagatesAfterListenerAndLeaseAreReleased() {
    var database = new TemporaryDatabase("urd callback failure data");
    var expected = new InvalidOperationException("ready callback failed");
    Uri? published = null;
    int callbacks = 0;
    var server = new RunningServer(Options(database), origin => {
      published = origin;
      callbacks++;
      throw expected;
    });
    RunningServer? restarted = null;
    try {
      InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
          server.WaitForCompletionAsync);
      Assert.Same(expected, actual);
      Assert.Equal(1, callbacks);
      Assert.NotNull(published);
      await AssertListenerClosedAsync(published);
      AssertLeaseFree(database);
      restarted = new RunningServer(Options(database));
      await restarted.WaitForReadyAsync();
      await AssertHealthAsync(restarted.Client);
    } finally {
      await Task.WhenAll(server.DisposeAsync().AsTask(), restarted?.DisposeAsync().AsTask() ?? Task.CompletedTask);
      database.Dispose();
    }
  }

  [Fact]
  public async Task OccupiedPortFailsWithoutReadinessAndReleasesDirectoryLease() {
    var database = new TemporaryDatabase("urd occupied port data");
    using var occupied = new TcpListener(IPAddress.Loopback, 0);
    occupied.Start();
    int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
    int callbacks = 0;
    var server = new RunningServer(new ServerOptions(DataRoot(database), port), _ => callbacks++);
    RunningServer? restarted = null;
    try {
      await Assert.ThrowsAsync<IOException>(server.WaitForCompletionAsync);
      Assert.Equal(0, callbacks);
      Assert.False(server.IsReady);
      AssertLeaseFree(database);
      occupied.Stop();
      restarted = new RunningServer(new ServerOptions(DataRoot(database), port));
      Assert.Equal(port, (await restarted.WaitForReadyAsync()).Port);
      await AssertHealthAsync(restarted.Client);
    } finally {
      await Task.WhenAll(server.DisposeAsync().AsTask(), restarted?.DisposeAsync().AsTask() ?? Task.CompletedTask);
      database.Dispose();
    }
  }

  [Fact]
  public async Task AlreadyCanceledStartupCompletesNormallyWithoutReadinessAndReleasesLease() {
    var database = new TemporaryDatabase("urd canceled startup data");
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();
    int callbacks = 0;
    var server = new RunningServer(Options(database), _ => callbacks++, cancellationToken: canceled.Token);
    RunningServer? restarted = null;
    try {
      await server.WaitForCompletionAsync();
      Assert.Equal(0, callbacks);
      Assert.False(server.IsReady);
      AssertLeaseFree(database);
      restarted = new RunningServer(Options(database));
      await restarted.WaitForReadyAsync();
      await AssertHealthAsync(restarted.Client);
    } finally {
      await Task.WhenAll(server.DisposeAsync().AsTask(), restarted?.DisposeAsync().AsTask() ?? Task.CompletedTask);
      database.Dispose();
    }
  }

  private static async Task SeedHistoryAsync(TemporaryDatabase database) {
    UrdStore store = UrdStore.Open(database.Path);
    var client = new ScriptedChatClient(ScriptedResponses.Complete);
    var manager = new SessionManager(store, _ => client);
    try {
      PrimarySession session = await manager.CreatePrimaryAsync().WaitAsync(TestTimeout);
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), 0, "retained input", TestLlmSelection.Default)).WaitAsync(TestTimeout);
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(1, client.Calls);
    } finally {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      store.Dispose();
      client.Dispose();
    }
  }

  private static string DataRoot(TemporaryDatabase database) => Path.GetDirectoryName(database.Path)!;

  private static ServerOptions Options(TemporaryDatabase database) => new(DataRoot(database), 0);

  private static HttpClient NewClient(Uri origin) =>
      new(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = origin, Timeout = TestTimeout };

  private static async Task AssertHealthAsync(HttpClient client, HttpMethod? method = null) {
    using var request = new HttpRequestMessage(method ?? HttpMethod.Get, "/health");
    using HttpResponseMessage response = await client.SendAsync(request);
    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    Assert.Empty(await response.Content.ReadAsByteArrayAsync());
  }

  private static async Task AssertStatusAsync(HttpClient client, HttpRequestMessage request,
      HttpStatusCode status) {
    using (request) {
      using HttpResponseMessage response = await client.SendAsync(request);
      Assert.Equal(status, response.StatusCode);
      Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
  }

  private static async Task AssertListenerClosedAsync(Uri origin) {
    using var client = NewClient(origin);
    await Assert.ThrowsAsync<HttpRequestException>(async () => {
      using HttpResponseMessage response = await client.GetAsync("/health");
    });
  }

  private static void AssertLeaseFree(TemporaryDatabase database) {
    using var lease = new FileStream(Path.Combine(DataRoot(database), "urd.lock"),
        FileMode.Open, FileAccess.ReadWrite, FileShare.None);
  }
}
