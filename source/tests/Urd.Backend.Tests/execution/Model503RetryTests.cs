using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

public sealed class Model503RetryTests {
  private const string TitleInstruction = "Name this session from its first user message";
  private const string Prompt = "orbit compass launch";

  [Theory]
  [InlineData("1", false, 2, "completed", "Orbit title")]
  [InlineData("60", false, 1, "fallback", Prompt)]
  [InlineData("0", true, 2, "fallback", Prompt)]
  public async Task Title503RetriesOnceWithinRetryAfterBudget(string retryAfter, bool alwaysFail,
      int expectedAttempts, string expectedState, string expectedTitle) {
    using var database = new TemporaryDatabase();
    using var store = UrdStore.Open(database.Path);
    var clock = new RetryClock();
    int titleAttempts = 0;
    var handler = new ControlledProviderHandler(async (request, cancellation) => {
      if (request.Method == HttpMethod.Get)
        return new HttpResponseMessage(HttpStatusCode.OK) {
          RequestMessage = request,
          Content = new StringContent("""
              {"object":"list","data":[
                {"id":"aux-model","object":"model","created":1700000000,"owned_by":"fixture","context_window":32000},
                {"id":"main-model","object":"model","created":1700000000,"owned_by":"fixture","context_window":32000}
              ]}
              """, Encoding.UTF8, "application/json")
        };
      string body = await request.Content!.ReadAsStringAsync(cancellation);
      bool title = body.Contains(TitleInstruction, StringComparison.Ordinal);
      bool main = body.Contains("main-model", StringComparison.Ordinal);
      if (title && (Interlocked.Increment(ref titleAttempts) == 1 || alwaysFail))
        return Unavailable(request, retryAfter);

      string answer = (title, main) switch {
        (true, _) => "Orbit title",
        (_, true) => "done",
        _ => "{\"keptIds\":[]}"
      };
      string item = ProviderModelStreams.Message.Replace("\"hello\"", JsonSerializer.Serialize(answer), StringComparison.Ordinal);
      string stream = ProviderModelStreams.Responses([item], text: answer, model: main ? "main-model" : "aux-model");
      return new HttpResponseMessage(HttpStatusCode.OK) {
        RequestMessage = request,
        Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(stream))) { Headers = { ContentType = new("text/event-stream") } }
      };
    });
    using var providers = CreateProviderManager(store, handler, clock);
    ProviderConnection connection = await ConnectAsync(providers);
    await providers.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
    new PreferenceManager(store).SetBucketModels(ModelBucket.Small, [new(connection.Id, "aux-model")]);
    using IChatClient client = providers.CreateChatClient(connection);
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)),
        store, _ => client, new ToolCatalog([]), modelSelector: new ModelSelector(providers));
    TaskCompletionSource titlePublished = Signal();
    using IDisposable subscription = await session.SubscribeAsync(_ => {
      if (store.Snapshots.ReadPersisted(session.Id).Title is not null)
        titlePublished.TrySetResult();
    }, watched: true);
    try {
      var selection = new LlmSelection(connection, "main-model", null, ModelFamily.Other, false);
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, Prompt, selection));
      Guid execution = Assert.IsType<Guid>(receipt.ExecutionId);
      SessionExecutionResult result = await session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout);
      Assert.True(result.Outcome == ExecutionOutcome.Completed, result.Error);
      await titlePublished.Task.WaitAsync(TestTimeout);

      var titleRequests = handler.Requests.Where(item => item.Body.Contains(TitleInstruction, StringComparison.Ordinal)).ToArray();
      Assert.Equal(expectedAttempts, titleRequests.Length);
      if (expectedAttempts == 2)
        Assert.Equal(titleRequests[0].Body, titleRequests[1].Body);
      Assert.Single(handler.Requests, item => item.Body.Contains("main-model", StringComparison.Ordinal));
      Assert.Equal(expectedTitle, store.Snapshots.ReadPersisted(session.Id).Title);
      Assert.Equal(expectedState, Assert.Single(database.ReadRows(
          $"SELECT state FROM session_prepass_details WHERE run_id = '{execution:D}' AND category = 'title';",
          row => row.GetString(0))));
      if (retryAfter == "1")
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(clock.Delays));
      else if (expectedAttempts == 1)
        Assert.Empty(clock.Delays);
      else
        Assert.InRange(Assert.Single(clock.Delays), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(999));
    } finally {
      await session.StopAcceptingUserMessagesAsync();
      await session.StopExecutionAsync().WaitAsync(TestTimeout);
      await providers.ShutdownAsync().WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task OrdinaryRequestRetriesPreStreamHttp503WithSameWireRequest() {
    using var database = new TemporaryDatabase();
    using var store = UrdStore.Open(database.Path);
    var clock = new RetryClock();
    int attempts = 0;
    var handler = new ControlledProviderHandler((request, _) => Task.FromResult(
        Interlocked.Increment(ref attempts) == 1 ? Unavailable(request, "1") : StreamingResponse(request)));
    using var providers = CreateProviderManager(store, handler, clock);
    using IChatClient client = providers.CreateChatClient(await ConnectAsync(providers));

    try {
      var response = await client.GetResponseAsync(
          [new(ChatRole.User, "go")], new() { ModelId = "main-model" }).WaitAsync(TestTimeout);

      Assert.Equal("hello", response.Text);
      Assert.Equal(2, handler.Requests.Count);
      Assert.Equal(handler.Requests[0], handler.Requests[1]);
      Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(clock.Delays));
    } finally {
      await providers.ShutdownAsync().WaitAsync(TestTimeout);
    }
  }

  [Theory]
  [InlineData("2", 2)]
  [InlineData("3", 1)]
  public async Task OrdinaryRequestLimitsPreStreamHttp503Retries(string retryAfter, int expectedAttempts) {
    using var database = new TemporaryDatabase();
    using var store = UrdStore.Open(database.Path);
    var clock = new RetryClock();
    var handler = new ControlledProviderHandler((request, _) => Task.FromResult(Unavailable(request, retryAfter)));
    using var providers = CreateProviderManager(store, handler, clock);
    using IChatClient client = providers.CreateChatClient(await ConnectAsync(providers));

    try {
      var failure = await Assert.ThrowsAsync<ProviderOperationException>(() => client.GetResponseAsync(
          [new(ChatRole.User, "go")], new() { ModelId = "main-model" }).WaitAsync(TestTimeout));

      Assert.Equal(ProviderFailure.ProviderUnavailable, failure.Failure);
      Assert.Contains("HTTP 503", failure.Message, StringComparison.Ordinal);
      Assert.Equal(expectedAttempts, handler.Requests.Count);
      if (expectedAttempts == 2) {
        Assert.Equal(handler.Requests[0], handler.Requests[1]);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(clock.Delays));
      } else {
        Assert.Empty(clock.Delays);
      }
    } finally {
      await providers.ShutdownAsync().WaitAsync(TestTimeout);
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ConnectionFailuresAndCatalogRejectionsDoNotRetry(bool catalog) {
    using var database = new TemporaryDatabase();
    using var store = UrdStore.Open(database.Path);
    var clock = new RetryClock();
    var handler = new ControlledProviderHandler((request, _) => catalog
        ? Task.FromResult(Unavailable(request, "1"))
        : Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ConnectionError, "Connection failed")));
    using var providers = CreateProviderManager(store, handler, clock);
    ProviderConnection connection = await ConnectAsync(providers);
    using IChatClient client = providers.CreateChatClient(connection);

    try {
      var failure = await Assert.ThrowsAsync<ProviderOperationException>(() => catalog
          ? providers.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout)
          : client.GetResponseAsync([new(ChatRole.User, "go")], new() { ModelId = "main-model" }).WaitAsync(TestTimeout));

      Assert.Equal(ProviderFailure.ProviderUnavailable, failure.Failure);
      Assert.Single(handler.Requests);
      Assert.Empty(clock.Delays);
    } finally {
      await providers.ShutdownAsync().WaitAsync(TestTimeout);
    }
  }

  private static HttpResponseMessage StreamingResponse(HttpRequestMessage request) => new(HttpStatusCode.OK) {
    RequestMessage = request,
    Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(ProviderModelStreams.Responses(model: "main-model")))) {
      Headers = { ContentType = new("text/event-stream") }
    }
  };

  private static ProviderManager CreateProviderManager(UrdStore store, HttpMessageHandler handler, TimeProvider retryClock) =>
      new(store, () => new Uri("http://127.0.0.1:1234/auth/callback"), handler, retryClock: retryClock);

  private static Task<ProviderConnection> ConnectAsync(ProviderManager providers) => providers.AddCustomAsync(
      new("retry fixture", LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.None)).WaitAsync(TestTimeout);

  private static HttpResponseMessage Unavailable(HttpRequestMessage request, string retryAfter) {
    var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) {
      RequestMessage = request,
      Content = new StringContent("""{"error":{"message":"unavailable","type":"server_error"}}""", Encoding.UTF8, "application/json")
    };
    response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
    return response;
  }

  private sealed class RetryClock : TimeProvider {
    internal ConcurrentQueue<TimeSpan> Delays { get; } = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      Delays.Enqueue(dueTime);
      return base.CreateTimer(callback, state, TimeSpan.Zero, period);
    }
  }
}
