using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.OpenAIStreams;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

public sealed class ProviderModelRetryTests {
  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task NativeDiagnosticRetriesPreStream503WithSeparateUsageAttempts(LlmApi api) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    int calls = 0;
    string stream = api == LlmApi.OpenAIResponses ? ResponseStream() : ChatStream();
    var handler = new ControlledProviderHandler((request, _) => {
      int call = Interlocked.Increment(ref calls);
      var response = call == 1
          ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) {
              Content = new StringContent("{\"error\":{\"message\":\"unavailable\"}}", Encoding.UTF8, "application/json")
            }
          : new HttpResponseMessage(HttpStatusCode.OK) {
              Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
            };
      response.RequestMessage = request;
      return Task.FromResult(response);
    });
    var clock = new RetryClock();
    var manager = new ProviderManager(store, () => new Uri("http://localhost/callback"), handler, retryClock: clock);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(new("Fixture", api,
          new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.None));
      ProviderModelTestResult result = await manager.TestModelAsync(connection.Id, "fixture-model").WaitAsync(TestTimeout);
      Assert.Equal("hello", result.Text);
      Assert.Equal(2, calls);
      Assert.Single(clock.Delays);
      var attempts = store.ReadUsage(null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)).Recent;
      Assert.Equal(2, attempts.Count);
      Assert.All(attempts, attempt => {
        Assert.Equal("provider test", attempt.Purpose);
        Assert.NotNull(attempt.FinishedAt);
      });
      Assert.Equal(2, attempts.Select(attempt => attempt.Id).Distinct().Count());
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  [Fact]
  public async Task NativeDiagnosticDoesNotRetryStreamErrorMentioning503() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var handler = new ScriptedOpenAIHandler(ResponseStream("error").Replace("synthetic-private", "HTTP 503", StringComparison.Ordinal));
    var clock = new RetryClock();
    var manager = new ProviderManager(store, () => new Uri("http://localhost/callback"), handler, retryClock: clock);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(new("Fixture", LlmApi.OpenAIResponses,
          new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.None));
      await Assert.ThrowsAsync<ProviderOperationException>(() => manager.TestModelAsync(connection.Id, "fixture-model"));
      Assert.Single(handler.Requests);
      Assert.Empty(clock.Delays);
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  private sealed class RetryClock : TimeProvider {
    internal ConcurrentQueue<TimeSpan> Delays { get; } = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      Delays.Enqueue(dueTime);
      return base.CreateTimer(callback, state, TimeSpan.Zero, period);
    }
  }
}
