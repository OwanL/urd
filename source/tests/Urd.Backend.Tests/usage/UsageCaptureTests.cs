using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Usage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Usage;

public sealed class UsageCaptureTests {
  [Fact]
  public async Task SessionManagerCountsOverflowAndCompactionSeparatelyAndHistoryEditsDoNotEraseUsage() {
    using var database = new TemporaryDatabase("urd-usage-capture");
    int calls = 0;
    IAsyncEnumerable<ChatResponseUpdate> Respond(CancellationToken token) {
      int call = Interlocked.Increment(ref calls);
      if (call == 2) return Overflow(token);
      string text = call == 3 ? "summary" : "answer";
      return Complete(text, call * 10, call, token);
    }
    using var client = new ScriptedChatClient(Respond);
    Guid sessionId;
    Guid[] retainedUsageIds;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var manager = new SessionManager(store,
          connection => new UsageChatClient(client, store.Usage, connection));
      try {
        PrimarySession session = await manager.CreatePrimaryAsync();
        sessionId = session.Id;
        UserMessageReceipt first = await session.SubmitUserMessageAsync(
            new(Guid.NewGuid(), 0, "old context " + new string('x', 100_000), TestLlmSelection.Default));
        Assert.Equal(MessageAcceptance.Accepted, first.Acceptance);
        Guid firstExecution = Assert.IsType<Guid>(first.ExecutionId);
        Assert.Equal(ExecutionOutcome.Completed,
            (await session.WaitForExecutionFinishAsync(firstExecution).WaitAsync(TestTimeout)).Outcome);

        UserMessageReceipt overflowed = await session.SubmitUserMessageAsync(
            new(Guid.NewGuid(), 0, "retry after overflow", TestLlmSelection.Default));
        Assert.Equal(MessageAcceptance.Accepted, overflowed.Acceptance);
        Assert.Equal(ExecutionOutcome.Completed,
            (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(overflowed.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
        Assert.Equal(4, client.Calls); // Main request, rejected overflow, summary, retried main request.

        UsageSummary captured = store.ReadUsage(session.Id);
        Assert.Equal(4, captured.Totals.Requests);
        Assert.Equal(3, captured.Recent.Count(request => request.Purpose == "conversation"));
        UsageRequest summary = Assert.Single(captured.Recent, request => request.Purpose == "compaction");
        Assert.Equal<long?>(30, summary.Tokens!.InputTokens);
        Assert.Single(captured.Recent, request => request.ExecutionId == firstExecution);
        Assert.Contains(captured.Recent, request => request.Tokens?.InputTokens == 20); // Failed overflow still consumed usage.
        retainedUsageIds = captured.Recent.Select(request => request.Id).ToArray();
      } finally {
        await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      }
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    var restored = new SessionManager(reopened,
        connection => new UsageChatClient(client, reopened.Usage, connection));
    try {
      PrimarySession session = await restored.GetPrimaryAsync(sessionId);
      Assert.Equal(4, reopened.ReadUsage(sessionId).Totals.Requests);
      TranscriptHistoryPage history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
      MessageRef target = history.Entries.Last(entry => entry.Entry.Kind == TranscriptEntryKind.User).Text!.From.Message;
      MessageEditReceipt edit = await session.EditUserMessageAsync(
          new(Guid.NewGuid(), target, "edited after reopen", TestLlmSelection.Default));
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(edit.ExecutionId).WaitAsync(TestTimeout)).Outcome);

      UsageSummary afterEdit = reopened.ReadUsage(sessionId);
      Assert.Equal(5, afterEdit.Totals.Requests);
      Assert.All(retainedUsageIds, id => Assert.Contains(afterEdit.Recent, request => request.Id == id));
      Assert.Contains(afterEdit.Recent, request => request.ExecutionId == edit.ExecutionId);
    } finally {
      await restored.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task StopSettlesObservedUsageInsteadOfDiscardingIt() {
    using var database = new TemporaryDatabase("urd-usage-stop");
    using UrdStore store = UrdStore.Open(database.Path);
    var observed = Signal();
    async IAsyncEnumerable<ChatResponseUpdate> Held([EnumeratorCancellation] CancellationToken token) {
      yield return new() { Contents = [new UsageContent(Details(17, 2))] };
      observed.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
    using var client = new ScriptedChatClient(Held);
    var manager = new SessionManager(store,
        connection => new UsageChatClient(client, store.Usage, connection));
    try {
      PrimarySession session = await manager.CreatePrimaryAsync();
      UserMessageReceipt accepted = await session.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, "stop this request", TestLlmSelection.Default));
      Guid execution = Assert.IsType<Guid>(accepted.ExecutionId);
      await observed.Task.WaitAsync(TestTimeout);
      await session.RequestStopAsync(execution).WaitAsync(TestTimeout);
      Assert.Equal(ExecutionOutcome.Cancelled,
          (await session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);

      UsageRequest captured = Assert.Single(store.ReadUsage(session.Id).Recent);
      Assert.NotNull(captured.FinishedAt);
      Assert.Equal<long?>(17, captured.Tokens!.InputTokens);
      Assert.Equal<long?>(2, captured.Tokens.OutputTokens);
    } finally {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task FinalUsageReplacesLiveEstimateAndMissingCacheStaysMarkedIncomplete() {
    using var database = new TemporaryDatabase("urd-usage-stream");
    using UrdStore store = UrdStore.Open(database.Path);
    var liveWritten = Signal();
    var release = Signal();
    int calls = 0;
    async IAsyncEnumerable<ChatResponseUpdate> Stream([EnumeratorCancellation] CancellationToken token) {
      if (Interlocked.Increment(ref calls) == 1) {
        yield return new() { Contents = [new TextContent("provisional output")] };
        await Task.Delay(275, token);
        yield return new(); // Checkpoint a live estimate before holding the stream.
        liveWritten.TrySetResult();
        await release.Task.WaitAsync(token);
        yield return new() { Contents = [new UsageContent(Details(20, 0))] };
      } else {
        yield return new() { Contents = [new UsageContent(new UsageDetails {
          InputTokenCount = 12, OutputTokenCount = 0, TotalTokenCount = 12
        })] };
      }
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    var inner = new ScriptedChatClient(Stream);
    var connection = new ProviderConnection(Guid.NewGuid(), "OpenAI", ProviderRoute.OpenAI,
        LlmApi.OpenAIResponses, new Uri("https://api.openai.com/v1/"), AuthenticationMethod.None);
    LlmSelection selection = TestLlmSelection.Create(connection, "gpt-4o-mini");
    Guid session = Guid.NewGuid();
    using var wrapper = new UsageChatClient(inner, store.Usage, connection);
    var context = new UsageContext(session, Guid.NewGuid(), "conversation");
    Task? first = null;
    try {
      first = ConsumeAsync(wrapper, selection.ModelId, context);
      await liveWritten.Task.WaitAsync(TestTimeout);
      UsageRequest live = Assert.Single(store.ReadUsage(session).Recent);
      Assert.True(live.IsEstimate);
      Assert.Null(live.Tokens!.CachedInputTokens);
      Assert.Null(live.Tokens.CacheWrite30mTokens);
      release.TrySetResult();
      await first.WaitAsync(TestTimeout);

      UsageRequest final = Assert.Single(store.ReadUsage(session).Recent);
      Assert.Equal(live.Id, final.Id);
      Assert.False(final.IsEstimate);
      Assert.Null(final.Problem);
      Assert.Equal<long?>(20, final.Tokens!.InputTokens);
      Assert.Equal<long?>(0, final.Tokens.OutputTokens); // A reported zero is known, not missing.
      UsageRates rates = Assert.IsType<UsageRates>(UsagePricing.Find(connection, selection.ModelId, final.StartedAt));
      long expectedNanoDollars = Assert.IsType<long>(UsagePricing.NanoDollars(rates, final.Tokens));
      Assert.Equal<decimal?>(expectedNanoDollars / 1_000_000_000m, final.CostUsd);
      Assert.Equal<long?>(0, final.Tokens.ReasoningTokens);
      Assert.Equal<long?>(20, final.Tokens.TotalTokens);
      Assert.Equal(final.CostUsd!.Value, store.ReadUsage(session).Totals.CostUsd);
      Assert.Equal(1, store.ReadUsage(session).Totals.Requests);

      await ConsumeAsync(wrapper, selection.ModelId, context);
      UsageRequest incomplete = Assert.Single(store.ReadUsage(session).Recent, request => request.Id != final.Id);
      Assert.True(incomplete.IsEstimate);
      Assert.Equal("Provider did not report complete final usage.", incomplete.Problem);
      Assert.Equal<long?>(0, incomplete.Tokens!.OutputTokens);
      Assert.Null(incomplete.Tokens.CachedInputTokens);
      wrapper.Dispose();
      Assert.Equal(0, inner.DisposeCalls); // The wrapped provider client is borrowed.
    } finally {
      release.TrySetResult();
      if (first is not null)
        await first.WaitAsync(TestTimeout);
      wrapper.Dispose();
      inner.Dispose();
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task MissingUsageNeedsResponseEvidenceBeforeQuotingACharge(bool completed) {
    using var database = new TemporaryDatabase("urd-usage-no-evidence");
    using UrdStore store = UrdStore.Open(database.Path);
    static async IAsyncEnumerable<ChatResponseUpdate> EmptyResponse() {
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }
    using var inner = new ScriptedChatClient(_ => completed
        ? EmptyResponse() : throw new InvalidOperationException("No provider response."));
    var connection = new ProviderConnection(Guid.NewGuid(), "OpenAI", ProviderRoute.OpenAI,
        LlmApi.OpenAIResponses, new Uri("https://api.openai.com/v1/"), AuthenticationMethod.None);
    var selection = TestLlmSelection.Create(connection, "gpt-4o-mini");
    Guid session = Guid.NewGuid();
    using var wrapper = new UsageChatClient(inner, store.Usage, connection);
    var context = new UsageContext(session, null, "conversation");
    if (completed)
      await ConsumeAsync(wrapper, selection.ModelId, context);
    else
      await Assert.ThrowsAsync<InvalidOperationException>(() => ConsumeAsync(wrapper, selection.ModelId, context));
    UsageRequest request = Assert.Single(store.ReadUsage(session).Recent);
    Assert.NotNull(request.FinishedAt);
    Assert.Equal(completed, request.CostUsd is > 0);
    Assert.True(request.IsEstimate);
    Assert.Equal(1, store.ReadUsage(session).Totals.UnknownRequests);
  }

  private static UsageDetails Details(long input, long output) => new() {
    InputTokenCount = input,
    OutputTokenCount = output,
    TotalTokenCount = input + output,
    CachedInputTokenCount = 0,
    ReasoningTokenCount = output / 2,
    AdditionalCounts = new AdditionalPropertiesDictionary<long> {
      ["cache_write_30m_tokens"] = 0,
      ["cache_write_12h_tokens"] = 0
    }
  };

  private static async IAsyncEnumerable<ChatResponseUpdate> Complete(string text, long input, long output,
      [EnumeratorCancellation] CancellationToken token) {
    token.ThrowIfCancellationRequested();
    yield return new() {
      Contents = [new TextContent(text), new UsageContent(Details(input, output))],
      FinishReason = ChatFinishReason.Stop
    };
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> Overflow(
      [EnumeratorCancellation] CancellationToken token) {
    yield return new() { Contents = [new UsageContent(Details(20, 2))] };
    await Task.Yield();
    token.ThrowIfCancellationRequested();
    throw new ProviderOperationException(ProviderFailure.ContextWindowExceeded);
  }

  private static async Task ConsumeAsync(UsageChatClient client, string model, UsageContext context) {
    var options = new ChatOptions { ModelId = model };
    options.AdditionalProperties = new() { [UsageContext.PropertyName] = context };
    await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
        [new ChatMessage(ChatRole.User, "capture usage")], options)) {
      // Drain the exchange without retaining provider output.
    }
  }
}
