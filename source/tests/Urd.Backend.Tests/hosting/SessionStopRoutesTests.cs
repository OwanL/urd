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
using Urd.Core.Storage;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

// Real HTTP Stop acknowledges saved intent, while snapshots retain the exact run until cleanup joins.
public sealed class SessionStopRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task DisconnectedStopAndRetriesAcknowledgeWhileCallbackAndCleanupRemainHeld() {
    var entered = Signal();
    var callbackEntered = Signal();
    var callbackRelease = Signal();
    var callbackExited = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    int callbacks = 0;
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      // Retain the registration so disposing it does not couple the two cleanup barriers.
      token.Register(() => {
        Interlocked.Increment(ref callbacks);
        callbackEntered.TrySetResult();
        callbackRelease.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
        callbackExited.TrySetResult();
      });
      try {
        yield return Text("working");
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
      } finally {
        cleanupEntered.TrySetResult();
        await cleanupRelease.Task;
      }
    }

    using var database = new TemporaryDatabase();
    var model = new ScriptedChatClient(Response);
    await using var server = new RunningServer(Options(database), createClient: () => model);
    Uri origin = await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    InputReceipt accepted = await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, "start"));
    Guid runId = accepted.RunId!.Value;
    try {
      await entered.Task.WaitAsync(TestTimeout);
      var steering = new InputCommand(Guid.NewGuid(), 0, "pending steering");
      InputReceipt queued = await SubmitAsync(server.Client, sessionId, steering);
      Assert.Equal(InputDecision.Accepted, queued.Decision);
      Assert.Equal(runId, queued.RunId);
      await StopWithoutReadingAcknowledgementAsync(origin, sessionId, runId, callbackEntered.Task);
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      for (int retry = 0; retry < 2; retry++)
        Assert.Equal(new StopReceipt(runId, StopStatus.Requested), await StopAsync(server.Client, sessionId, runId));
      Assert.Equal(1, Volatile.Read(ref callbacks));
      Assert.Equal(queued, await SubmitAsync(server.Client, sessionId, steering));
      StoredDelivery retired = Assert.Single(StoredDeliveries.Read(database), row => row.InputId == steering.InputId);
      Assert.Null(retired.RequestId);
      Assert.Equal(ExecutionOutcome.Cancelled, retired.Outcome);
      StoredRun saved = Assert.Single(StoredRuns.Read(database));
      Assert.True(saved.Stopping);
      Assert.Null(saved.Outcome);
      SessionSnapshot held = await SnapshotAsync(server.Client, sessionId);
      Assert.Equal(runId, held.UnsettledRunId);
      Assert.True(held.StopRequested);
      Assert.Null(held.LastSettlement);
      InputReceipt refused = await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, "too soon"));
      Assert.Equal(InputDecision.Refused, refused.Decision);
      Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);

      callbackRelease.TrySetResult();
      await callbackExited.Task.WaitAsync(TestTimeout);
      Assert.Equal(runId, (await SnapshotAsync(server.Client, sessionId)).UnsettledRunId);
      Assert.Null(Assert.Single(StoredRuns.Read(database)).Outcome);
      Assert.Equal(1, model.Calls);
    } finally {
      callbackRelease.TrySetResult();
      cleanupRelease.TrySetResult();
    }

    SessionSnapshot settled = await SettledAsync(server.Client, sessionId, runId);
    Assert.Equal(ExecutionOutcome.Cancelled, settled.LastSettlement!.Outcome);
    Assert.Equal(new StopReceipt(runId, StopStatus.Settled, ExecutionOutcome.Cancelled),
        await StopAsync(server.Client, sessionId, runId));
    Assert.Equal(1, model.Calls);
    Assert.Single(StoredRuns.ReadRequests(database));
  }

  [Fact]
  public async Task StaleAndWrongSessionStopsLeaveReplacementAndIndependentWorkRunning() {
    var independentEntered = Signal();
    var independentRelease = Signal();
    var replacementEntered = Signal();
    var replacementRelease = Signal();
    CancellationToken independentToken = default;
    CancellationToken replacementToken = default;
    int requests = 0;
    IAsyncEnumerable<ChatResponseUpdate> Response(CancellationToken token) {
      switch (Interlocked.Increment(ref requests)) {
        case 1:
          return Complete(token);
        case 2:
          independentToken = token;
          return Paused(independentEntered, independentRelease, token);
        default:
          replacementToken = token;
          return Paused(replacementEntered, replacementRelease, token);
      }
    }

    using var database = new TemporaryDatabase();
    var model = new ScriptedChatClient(Response);
    await using var server = new RunningServer(Options(database), createClient: () => model);
    await server.WaitForReadyAsync();
    try {
      Guid target = await CreateAsync(server.Client);
      Guid independent = await CreateAsync(server.Client);
      Guid original = (await SubmitAsync(server.Client, target, new(Guid.NewGuid(), 0, "original"))).RunId!.Value;
      await SettledAsync(server.Client, target, original);
      Guid otherRun = (await SubmitAsync(server.Client, independent, new(Guid.NewGuid(), 0, "independent"))).RunId!.Value;
      await independentEntered.Task.WaitAsync(TestTimeout);
      Guid replacement = (await SubmitAsync(server.Client, target, new(Guid.NewGuid(), 0, "replacement"))).RunId!.Value;
      await replacementEntered.Task.WaitAsync(TestTimeout);

      Assert.Equal(new StopReceipt(original, StopStatus.Settled, ExecutionOutcome.Completed),
          await StopAsync(server.Client, target, original));
      foreach ((Guid sessionId, Guid runId) in new[] {
          (target, otherRun), (independent, replacement), (target, Guid.NewGuid()), (Guid.NewGuid(), replacement) }) {
        using HttpResponseMessage missing = await RequestAsync(server.Client, HttpMethod.Post, StopPath(sessionId, runId), JsonBody("{}"));
        AssertStatus(missing, HttpStatusCode.NotFound);
      }
      Assert.False(replacementToken.IsCancellationRequested);
      Assert.False(independentToken.IsCancellationRequested);
      SessionSnapshot active = await SnapshotAsync(server.Client, target);
      Assert.Equal(replacement, active.UnsettledRunId);
      Assert.Equal(original, active.LastSettlement!.RunId);
      Assert.False(active.StopRequested);
      Assert.Equal(otherRun, (await SnapshotAsync(server.Client, independent)).UnsettledRunId);

      Assert.Equal(new StopReceipt(replacement, StopStatus.Requested), await StopAsync(server.Client, target, replacement));
      Assert.Equal(ExecutionOutcome.Cancelled, (await SettledAsync(server.Client, target, replacement)).LastSettlement!.Outcome);
      Assert.False(independentToken.IsCancellationRequested);
      Assert.False((await SnapshotAsync(server.Client, independent)).StopRequested);
      Assert.Null(Assert.Single(StoredRuns.Read(database), row => row.RunId == otherRun).Outcome);
      independentRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed, (await SettledAsync(server.Client, independent, otherRun)).LastSettlement!.Outcome);
      Assert.Equal(3, model.Calls);
    } finally {
      independentRelease.TrySetResult();
      replacementRelease.TrySetResult();
    }
  }

  [Fact]
  public async Task ReopenedOrphanReturnsUnavailableWithoutMutationOrReplay() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid();
    Guid runId = Guid.NewGuid();
    using (SessionStore store = SessionStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(sessionId);
      store.Inputs.Admit(sessionId, 0, new(Guid.NewGuid(), 0, "orphan"), true, runId);
      Assert.NotNull(store.Runs.ClaimNextRequest(sessionId, runId, 0));
      store.Inputs.Admit(sessionId, 0, new(Guid.NewGuid(), 0, "pending"), true, Guid.NewGuid(), runId);
    }
    List<StoredRun> runs = StoredRuns.Read(database);
    List<StoredRequest> requests = StoredRuns.ReadRequests(database);
    List<StoredDelivery> deliveries = StoredDeliveries.Read(database);
    List<StoredDecision> inputs = StoredInputs.Read(database);
    var model = new ScriptedChatClient(_ => throw new InvalidOperationException("Stop must not replay orphan work."));
    await using var server = new RunningServer(Options(database), createClient: () => model);
    await server.WaitForReadyAsync();
    for (int retry = 0; retry < 2; retry++) {
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Post, StopPath(sessionId, runId), JsonBody("{}"));
      AssertStatus(response, HttpStatusCode.Conflict);
      Assert.Equal(new StopReceipt(runId, StopStatus.Unavailable), await ReadAsync<StopReceipt>(response));
    }
    SessionSnapshot snapshot = await SnapshotAsync(server.Client, sessionId);
    Assert.Equal(runId, snapshot.UnsettledRunId);
    Assert.True(snapshot.AdmissionFenced);
    Assert.False(snapshot.StopRequested);
    Assert.Null(snapshot.LastSettlement);
    Assert.Equal(runs, StoredRuns.Read(database));
    Assert.Equal(requests, StoredRuns.ReadRequests(database));
    Assert.Equal(deliveries, StoredDeliveries.Read(database));
    Assert.Equal(inputs, StoredInputs.Read(database));
    Assert.Equal(0, model.Calls);
  }

  [Fact]
  public async Task StorageFailureReturns503ButStillCancelsLocallyAndRejectsLateOutputAndWork() {
    var entered = Signal();
    var release = Signal();
    var cancelled = Signal();
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
      yield return Text(new string('x', 16 * 1024));
      entered.TrySetResult();
      await release.Task; // An uncooperative provider attempts a late successful completion.
      yield return Text(new string('y', 16 * 1024));
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    using var database = new TemporaryDatabase();
    var model = new ScriptedChatClient(Response);
    await using var server = new RunningServer(Options(database), createClient: () => model);
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    Guid runId = (await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, "start"))).RunId!.Value;
    try {
      await entered.Task.WaitAsync(TestTimeout);
      database.Execute("""
        CREATE TRIGGER reject_stop BEFORE UPDATE OF stopping ON execution_runs
        WHEN NEW.stopping = 1
        BEGIN SELECT RAISE(ABORT, 'Injected Stop failure'); END;
        """);
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Post, StopPath(sessionId, runId), JsonBody("{}"));
      AssertStatus(response, HttpStatusCode.ServiceUnavailable);
      Assert.Empty(await response.Content.ReadAsStringAsync());
      await cancelled.Task.WaitAsync(TestTimeout);
      StoredRun saved = Assert.Single(StoredRuns.Read(database));
      Assert.False(saved.Stopping);
      Assert.Null(saved.Outcome);
      SessionSnapshot snapshot = await SnapshotAsync(server.Client, sessionId);
      Assert.True(snapshot.AdmissionFenced);
      Assert.True(snapshot.StopRequested);
      Assert.Equal(runId, snapshot.UnsettledRunId);
      Assert.Null(snapshot.LastSettlement);
      InputReceipt refused = await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, "must not run"));
      Assert.Equal(InputDecision.Refused, refused.Decision);
      Assert.Equal(InputRefusalReason.Stopping, refused.RefusalReason);
    } finally {
      database.Execute("DROP TRIGGER IF EXISTS reject_stop;");
      release.TrySetResult();
    }

    SessionSnapshot settled = await SettledAsync(server.Client, sessionId, runId);
    Assert.Equal(ExecutionOutcome.Cancelled, settled.LastSettlement!.Outcome);
    Assert.True(settled.AdmissionFenced);
    Assert.Equal(new string('x', 16 * 1024), StoredOutput.ReadText(database));
    Assert.All(StoredOutput.Read(database), chunk => Assert.False(chunk.Final));
    Assert.Equal(ExecutionOutcome.Cancelled, Assert.Single(StoredRuns.ReadRequests(database)).Outcome);
    Assert.Equal(1, model.Calls);
  }

  [Fact]
  public async Task InvalidBodiesIdentifiersAndUntrustedRequestsNeverStopTheLiveRun() {
    var entered = Signal();
    var release = Signal();
    CancellationToken modelToken = default;
    var model = new ScriptedChatClient(token => {
      modelToken = token;
      return Paused(entered, release, token);
    });
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database), createClient: () => model);
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    Guid runId = (await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, "keep running"))).RunId!.Value;
    string path = StopPath(sessionId, runId);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      foreach (string body in new[] { "", "null", "[]", "{", "true", "0", "\"stop\"", "{\"unused\":true}", "{}{}" }) {
        using HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Post, path, JsonBody(body));
        AssertStatus(invalid, HttpStatusCode.BadRequest);
      }
      foreach (string id in new[] { "not-a-guid", Guid.Empty.ToString() }) {
        foreach (string invalidPath in new[] { $"/api/sessions/{sessionId}/runs/{id}/stop", $"/api/sessions/{id}/runs/{runId}/stop" }) {
          using HttpResponseMessage invalid = await RequestAsync(server.Client, HttpMethod.Post, invalidPath, JsonBody("{}"));
          AssertStatus(invalid, HttpStatusCode.BadRequest);
        }
      }
      using (HttpResponseMessage media = await RequestAsync(server.Client, HttpMethod.Post, path, new StringContent("{}")))
        AssertStatus(media, HttpStatusCode.UnsupportedMediaType);
      foreach (string charset in new[] { "bogus", "utf-7" }) {
        StringContent content = JsonBody("{}");
        content.Headers.ContentType!.CharSet = charset;
        using HttpResponseMessage media = await RequestAsync(server.Client, HttpMethod.Post, path, content);
        AssertStatus(media, HttpStatusCode.UnsupportedMediaType);
      }
      foreach (bool chunked in new[] { false, true }) {
        using HttpResponseMessage oversized = await RequestAsync(server.Client, HttpMethod.Post, path,
            JsonBody("{\"unused\":\"" + new string('x', 128 * 1024) + "\"}"), chunked: chunked);
        AssertStatus(oversized, HttpStatusCode.RequestEntityTooLarge);
      }
      using (HttpResponseMessage unmarked = await RequestAsync(server.Client, HttpMethod.Post, path, JsonBody("{}"), marker: false))
        AssertStatus(unmarked, HttpStatusCode.Forbidden);
      foreach (string origin in new[] { "null", "http://foreign.example" }) {
        using HttpResponseMessage foreign = await RequestAsync(server.Client, HttpMethod.Post, path, JsonBody("{}"), suppliedOrigin: origin);
        AssertStatus(foreign, HttpStatusCode.Forbidden);
      }
      Assert.False(modelToken.IsCancellationRequested);
      StoredRun saved = Assert.Single(StoredRuns.Read(database));
      Assert.False(saved.Stopping);
      Assert.Null(saved.Outcome);
      Assert.False((await SnapshotAsync(server.Client, sessionId)).StopRequested);
      Assert.Single(StoredInputs.Read(database));
      Assert.Single(StoredRuns.ReadRequests(database));
      Assert.Equal(1, model.Calls);
    } finally {
      release.TrySetResult();
    }
    Assert.Equal(ExecutionOutcome.Completed, (await SettledAsync(server.Client, sessionId, runId)).LastSettlement!.Outcome);
  }

  private static ServerOptions Options(TemporaryDatabase database) => new(Path.GetDirectoryName(database.Path)!, 0);

  private static string StopPath(Guid sessionId, Guid runId) => $"/api/sessions/{sessionId}/runs/{runId}/stop";

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

  private static void AssertStatus(HttpResponseMessage response, HttpStatusCode status) {
    Assert.Equal(status, response.StatusCode);
    Assert.True(response.Headers.CacheControl?.NoStore);
    Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
  }

  private static async Task<T> ReadAsync<T>(HttpResponseMessage response) {
    Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>(json));
  }

  private static async Task<Guid> CreateAsync(HttpClient client) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, "/api/sessions", JsonBody("{}"));
    AssertStatus(response, HttpStatusCode.Created);
    return (await ReadAsync<JsonElement>(response)).GetProperty("sessionId").GetGuid();
  }

  private static async Task<InputReceipt> SubmitAsync(HttpClient client, Guid sessionId, InputCommand command) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, $"/api/sessions/{sessionId}/inputs",
        JsonContent.Create(command, options: json));
    AssertStatus(response, HttpStatusCode.OK);
    InputReceipt receipt = await ReadAsync<InputReceipt>(response);
    Assert.Equal(command, receipt.Command);
    return receipt;
  }

  private static async Task<StopReceipt> StopAsync(HttpClient client, Guid sessionId, Guid runId) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, StopPath(sessionId, runId), JsonBody("{}"));
    AssertStatus(response, HttpStatusCode.OK);
    StopReceipt receipt = await ReadAsync<StopReceipt>(response);
    Assert.Equal(runId, receipt.RunId);
    return receipt;
  }

  private static async Task<SessionSnapshot> SnapshotAsync(HttpClient client, Guid sessionId) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, $"/api/sessions/{sessionId}/snapshot");
    AssertStatus(response, HttpStatusCode.OK);
    SessionSnapshot snapshot = await ReadAsync<SessionSnapshot>(response);
    Assert.Equal(sessionId, snapshot.SessionId);
    return snapshot;
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

  private static async Task StopWithoutReadingAcknowledgementAsync(Uri origin, Guid sessionId, Guid runId, Task cancellationStarted) {
    using var deadline = new CancellationTokenSource(TestTimeout);
    using var connection = new TcpClient();
    await connection.ConnectAsync(origin.Host, origin.Port, deadline.Token);
    string request = $"POST {StopPath(sessionId, runId)} HTTP/1.1\r\nHost: {origin.Authority}\r\n"
        + "X-Urd-Client: browser\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
    await using NetworkStream stream = connection.GetStream();
    await stream.WriteAsync(Encoding.ASCII.GetBytes(request), deadline.Token);
    await cancellationStarted.WaitAsync(deadline.Token);
    // Drop the connection without consuming the acknowledgement after Core has admitted Stop.
  }
}
