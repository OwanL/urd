using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

public sealed class RateLimitSessionExecutionTests {
  [Theory]
  [InlineData(HttpStatusCode.TooManyRequests)]
  [InlineData(HttpStatusCode.ServiceUnavailable)]
  public async Task EarlyHttpRejectionsBeforeAndAfterToolExecutionRetryWithoutRepeatingTheTool(HttpStatusCode status) {
    using var first = new ScriptedOpenAIHandler(ProviderModelStreams.Responses(
        [ProviderModelStreams.Message, ProviderModelStreams.Call]));
    using var done = new ScriptedOpenAIHandler(ProviderModelStreams.Responses());
    using var firstTransport = new HttpMessageInvoker(first, false);
    using var doneTransport = new HttpMessageInvoker(done, false);
    var clock = new RetryClock();
    int attempts = 0, effects = 0;
    var handler = new ControlledProviderHandler((request, cancellation) => Interlocked.Increment(ref attempts) switch {
      1 or 3 => Task.FromResult(Rejected(request, status, 1)),
      2 => firstTransport.SendAsync(request, cancellation),
      4 => doneTransport.SendAsync(request, cancellation),
      _ => throw new InvalidOperationException("Unexpected provider request.")
    });
    await using var fixture = await Fixture.OpenAsync(handler, () => Interlocked.Increment(ref effects), clock);
    Task<SessionExecutionResult> execution = fixture.SendAsync();
    clock.Advance(await clock.NextDelayAsync());
    clock.Advance(await clock.NextDelayAsync());

    SessionExecutionResult result = await execution;

    Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
    Assert.Null((await fixture.Session.ReadSnapshotAsync()).RateLimitRetryAt);
    Assert.Equal(1, effects);
    Assert.Equal(4, handler.Requests.Count);
    Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
    Assert.Equal(handler.Requests[2].Body, handler.Requests[3].Body);
    Assert.Contains("function_call_output", handler.Requests[2].Body, StringComparison.Ordinal);
    Assert.Single(StoredTools.Read(fixture.Database));
  }

  [Fact]
  public async Task RateLimitBeyondWaitBudgetFailsWithoutRepeatingTheTool() {
    using var first = new ScriptedOpenAIHandler(ProviderModelStreams.Responses(
        [ProviderModelStreams.Message, ProviderModelStreams.Call]));
    using var firstTransport = new HttpMessageInvoker(first, false);
    int attempts = 0, effects = 0;
    var handler = new ControlledProviderHandler((request, cancellation) => Interlocked.Increment(ref attempts) switch {
      1 => firstTransport.SendAsync(request, cancellation),
      2 => Task.FromResult(RateLimited(request, 301)),
      _ => throw new InvalidOperationException("The retry wait budget was exceeded.")
    });
    await using var fixture = await Fixture.OpenAsync(handler, () => Interlocked.Increment(ref effects));

    SessionExecutionResult result = await fixture.SendAsync();

    Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
    Assert.NotNull(result.Error);
    Assert.Equal(1, effects);
    Assert.Equal(2, handler.Requests.Count);
    Assert.Contains("function_call_output", handler.Requests[1].Body, StringComparison.Ordinal);
    Assert.Single(StoredTools.Read(fixture.Database));
    Assert.False((await fixture.Session.ReadSnapshotAsync()).AdmissionFenced);
  }

  [Fact]
  public async Task RepeatedThrottlingUsesOneWindowWithoutAnAttemptCap() {
    var clock = new RetryClock();
    var handler = new ControlledProviderHandler((request, _) => Task.FromResult(RateLimited(request, 26)));
    await using var fixture = await Fixture.OpenAsync(handler, () => { }, clock);
    Task<SessionExecutionResult> execution = fixture.SendAsync();

    for (int i = 0; i < 11; i++) {
      RetryClock.Delay delay = await clock.NextDelayAsync();
      Assert.Equal(TimeSpan.FromSeconds(26), delay.Due);
      clock.Advance(delay);
    }

    SessionExecutionResult result = await execution;
    Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
    Assert.Contains("five-minute retry window", result.Error, StringComparison.Ordinal);
    Assert.Equal(12, handler.Requests.Count);
    var usage = fixture.Store.ReadUsage(fixture.Session.Id, DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow.AddMinutes(1)).Recent;
    Assert.Equal(12, usage.Count);
    Assert.All(usage, attempt => Assert.NotNull(attempt.FinishedAt));
  }

  [Theory]
  [InlineData(HttpStatusCode.TooManyRequests, 60)]
  [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
  public async Task StopDuringBackoffCancelsTheWaitAndClearsRetryStatus(HttpStatusCode status, int retryAfter) {
    var clock = new RetryClock();
    var handler = new ControlledProviderHandler((request, _) => Task.FromResult(Rejected(request, status, retryAfter)));
    await using var fixture = await Fixture.OpenAsync(handler, () => { }, clock);
    Task<SessionExecutionResult> execution = fixture.SendAsync();
    RetryClock.Delay delay = await clock.NextDelayAsync();

    DateTimeOffset? retryAt = status == HttpStatusCode.TooManyRequests ? clock.GetUtcNow() + delay.Due : null;
    Assert.Equal(retryAt, (await fixture.Session.ReadSnapshotAsync()).RateLimitRetryAt);
    await fixture.Session.StopExecutionAsync().WaitAsync(TestTimeout);
    Assert.Equal(ExecutionOutcome.Cancelled, (await execution).Outcome);
    clock.Advance(delay);
    Assert.Null((await fixture.Session.ReadSnapshotAsync()).RateLimitRetryAt);
    Assert.Single(handler.Requests);
  }

  [Theory]
  [InlineData(HttpStatusCode.TooManyRequests, 60)]
  [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
  public async Task BindingChangeDuringBackoffCancelsTheWaitWithoutSendingAnotherRequest(HttpStatusCode status, int retryAfter) {
    var clock = new RetryClock();
    var handler = new ControlledProviderHandler((request, _) => Task.FromResult(Rejected(request, status, retryAfter)));
    await using var fixture = await Fixture.OpenAsync(handler, () => { }, clock);
    Task<SessionExecutionResult> execution = fixture.SendAsync();
    RetryClock.Delay delay = await clock.NextDelayAsync();

    await fixture.ChangeEndpointAsync().WaitAsync(TestTimeout);
    Assert.NotEqual(ExecutionOutcome.Completed, (await execution).Outcome);
    clock.Advance(delay);
    Assert.Null((await fixture.Session.ReadSnapshotAsync()).RateLimitRetryAt);
    Assert.Single(handler.Requests);
  }

  [Fact]
  public async Task ParentReceivesTheRecoveredChildReportAlongsideTheExhaustedChildError() {
    const string healthyTask = "recover the carmine branch";
    const string limitedTask = "recover the violet branch";
    const string report = "carmine branch recovered";
    const string model = "fixture-model";
    var clock = new RetryClock();
    using var database = new TemporaryDatabase();
    using var store = UrdStore.Open(database.Path);
    using var success = new ScriptedOpenAIHandler(ProviderModelStreams.Responses(
        [ProviderModelStreams.Message.Replace("hello", report, StringComparison.Ordinal)], text: report));
    using var successTransport = new HttpMessageInvoker(success, false);
    int healthyAttempts = 0;
    var handler = new ControlledProviderHandler(RespondAsync);
    using var providers = new ProviderManager(store, () => new Uri("http://127.0.0.1:1234/auth/callback"), handler,
        retryClock: clock);
    IChatClient? childClient = null;
    FunctionResultContent[] reports = [];
    using var fixtureClient = new ScriptedChatClient(Route);
    var sessions = new SessionManager(store, _ => fixtureClient, [], Path.GetDirectoryName(database.Path)!,
        new ModelSelector(providers));
    try {
      ProviderConnection connection = await providers.AddCustomAsync(new("fan-out fixture", LlmApi.OpenAIResponses,
          new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.None));
      await providers.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      childClient = providers.CreateChatClient(connection);
      new PreferenceManager(store).SetBucketModels(ModelBucket.Small, [new(connection.Id, model)]);
      PrimarySession parent = await sessions.CreatePrimaryAsync();
      var selection = new LlmSelection(connection, model, null, ModelFamily.Other, false);
      UserMessageReceipt receipt = await parent.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "fan out", selection));
      Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
      clock.Advance(await clock.NextDelayAsync());

      SessionExecutionResult result = await parent.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout);
      Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
      Assert.Equal(report, Assert.Single(reports, item => item.CallId == "healthy").Result);
      object? error = Assert.Single(reports, item => item.CallId == "limited").Result;
      Assert.Contains("five-minute retry window", Assert.IsType<string>(error), StringComparison.Ordinal);
      Assert.Equal(3, handler.Requests.Count(request => request.Method == HttpMethod.Post));
    } finally {
      await sessions.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      childClient?.Dispose();
      await providers.ShutdownAsync().WaitAsync(TestTimeout);
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      if (request.Purpose == "title") return TextUpdates(["Fan-out fixture"], token);
      if (request.Purpose?.StartsWith("pruning.", StringComparison.Ordinal) == true)
        return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
      if (request.LastUserText is healthyTask or limitedTask)
        return childClient!.GetStreamingResponseAsync(request.Messages, request.Options, token);
      reports = [.. request.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()];
      return reports.Length == 0 ? ToolBatch(token, Delegate("healthy", healthyTask), Delegate("limited", limitedTask))
          : TextUpdates(["Parent used the reports."], token);
    }

    async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellation) {
      if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) {
        Content = JsonContent.Create(new { @object = "list", data = new[] { new { id = model, @object = "model" } } })
      };
      string body = await request.Content!.ReadAsStringAsync(cancellation);
      if (body.Contains(limitedTask, StringComparison.Ordinal)) return RateLimited(request, 301);
      Assert.Contains(healthyTask, body, StringComparison.Ordinal);
      return Interlocked.Increment(ref healthyAttempts) == 1 ? RateLimited(request, 1)
          : await successTransport.SendAsync(request, cancellation);
    }

    static FunctionCallContent Delegate(string id, string task) => new(id, "subagent", new Dictionary<string, object?> {
      ["bucket"] = "small", ["includeUserInput"] = false, ["task"] = task
    });
  }

  private static HttpResponseMessage RateLimited(HttpRequestMessage request, int seconds) =>
      Rejected(request, HttpStatusCode.TooManyRequests, seconds);

  private static HttpResponseMessage Rejected(HttpRequestMessage request, HttpStatusCode status, int seconds) => new(status) {
    RequestMessage = request,
    Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)) },
    Content = new StringContent(status == HttpStatusCode.TooManyRequests
        ? """{"error":{"message":"rate limited","type":"rate_limit_error"}}"""
        : """{"error":{"message":"unavailable","type":"server_error"}}""")
  };

  // Tests advance only after the actual retry timer is armed. No wall-clock waits or live servers.
  private sealed class RetryClock : TimeProvider {
    private readonly Channel<Delay> scheduled = Channel.CreateUnbounded<Delay>();
    private long ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      Assert.Equal(Timeout.InfiniteTimeSpan, period);
      var delay = new Delay(callback, state, dueTime);
      Assert.True(scheduled.Writer.TryWrite(delay));
      return delay;
    }

    internal Task<Delay> NextDelayAsync() => scheduled.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

    internal void Advance(Delay delay) {
      Interlocked.Add(ref ticks, delay.Due.Ticks);
      delay.Fire();
    }

    internal sealed class Delay(TimerCallback callback, object? state, TimeSpan due) : ITimer {
      private TimerCallback? pending = callback;
      internal TimeSpan Due { get; } = due;
      internal void Fire() => Interlocked.Exchange(ref pending, null)?.Invoke(state);
      public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
      public void Dispose() => Interlocked.Exchange(ref pending, null);
      public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
  }

  private sealed class Fixture : IAsyncDisposable {
    private readonly UrdStore store;
    private readonly ProviderManager manager;
    private readonly IChatClient client;
    private readonly ProviderConnection connection;

    internal TemporaryDatabase Database { get; }
    internal PrimarySession Session { get; }
    internal UrdStore Store => store;

    private Fixture(TemporaryDatabase database, UrdStore store, ProviderManager manager, IChatClient client,
        ProviderConnection connection, PrimarySession session) {
      Database = database;
      this.store = store;
      this.manager = manager;
      this.client = client;
      this.connection = connection;
      Session = session;
    }

    internal static async Task<Fixture> OpenAsync(HttpMessageHandler handler, Action effect, TimeProvider? retryClock = null) {
      var database = new TemporaryDatabase();
      var store = UrdStore.Open(database.Path);
      var manager = new ProviderManager(store, () => new Uri("http://127.0.0.1:1234/auth/callback"), handler,
          retryClock: retryClock);
      ProviderConnection connection = await manager.AddCustomAsync(new("rate limit fixture", LlmApi.OpenAIResponses,
          new Uri("http://fixture.invalid/v1/"), AuthenticationMethod.None));
      IChatClient client = manager.CreateChatClient(connection);
      var tool = new TestAgentTool("lookup", (_, _, _) => {
        effect();
        return Task.FromResult(new ToolOutput("lookup result", []));
      });
      SessionRecord record = store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)!);
      var session = new PrimarySession(record, store, _ => client, new ToolCatalog([tool]));
      return new(database, store, manager, client, connection, session);
    }

    internal Task<ProviderConnection> ChangeEndpointAsync() => manager.UpdateCustomAsync(connection.Id,
        new(connection.Name, connection.Api, new Uri("https://replacement.invalid/v1/"), connection.Authentication));

    internal async Task<SessionExecutionResult> SendAsync() {
      var selection = new LlmSelection(connection, "fixture-model", null, ModelFamily.Other, false);
      UserMessageReceipt receipt = await Session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "go", selection));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      return await Session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout);
    }

    public async ValueTask DisposeAsync() {
      try {
        await Session.StopAcceptingUserMessagesAsync();
        await Session.StopExecutionAsync().WaitAsync(TestTimeout);
        await manager.ShutdownAsync().WaitAsync(TestTimeout);
      } finally {
        client.Dispose();
        manager.Dispose();
        store.Dispose();
        Database.Dispose();
      }
    }
  }
}
