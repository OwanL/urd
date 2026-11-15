using Microsoft.Extensions.AI;
using Xunit;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Context;
using Urd.Core.Providers;

namespace Urd.Backend.Tests.Compaction;

public sealed class HistoryCompactorTests {
  private static readonly StartingContext emptyStartingContext = new([], []);

  [Fact]
  public void PrepareValidatesStartingContextAndItsContents() {
    var history = new ModelHistory([]);

    Assert.Throws<ArgumentNullException>(() => HistoryCompactor.Prepare(history, TestLlmSelection.Default, null!, CompactionReason.Manual));
    Assert.Throws<ArgumentNullException>(() => HistoryCompactor.Prepare(history, TestLlmSelection.Default, new(null!, []), CompactionReason.Manual));
    Assert.Throws<ArgumentNullException>(() => HistoryCompactor.Prepare(history, TestLlmSelection.Default, new([], null!), CompactionReason.Manual));
  }

  [Fact]
  public void PrepareIncludesStartingInstructionsInContextEstimate() {
    var history = new ModelHistory([
      Entry(1, 0, true, ChatRole.User, "old"),
      Entry(2, 0, true, ChatRole.User, "current")
    ]);
    var startingContext = new StartingContext([new ChatMessage(ChatRole.System, new string('i', 1_000))], []);

    HistoryCompactionPlan emptyPlan = HistoryCompactor.Prepare(history, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual)!;
    HistoryCompactionPlan instructionPlan = HistoryCompactor.Prepare(history, TestLlmSelection.Default, startingContext, CompactionReason.Manual)!;

    Assert.True(instructionPlan.EstimatedContextTokensBefore > emptyPlan.EstimatedContextTokensBefore);
  }

  [Fact]
  public void ThresholdUsesObservedTokensAndUnknownWindowsSkipAutomaticCompaction() {
    var context = new ModelHistory([
      Entry(1, 0, true, ChatRole.User, "old"),
      Entry(1, 1, false, ChatRole.Assistant, "answer"),
      Entry(2, 0, true, ChatRole.User, "current")
    ], UsageBasedContextTokens: 900);
    LlmSelection selection = TestLlmSelection.Default with { ContextWindow = 1_000 };

    HistoryCompactionPlan? plan = HistoryCompactor.Prepare(context, selection, emptyStartingContext, CompactionReason.Threshold);

    Assert.Equal(2, plan?.FirstRetainedMessageIndex);
    Assert.Null(HistoryCompactor.Prepare(context, selection with { ContextWindow = null }, emptyStartingContext, CompactionReason.Threshold));
  }

  [Theory]
  [InlineData(null, null, 900, null)]
  [InlineData(null, 200, 200, null)]
  [InlineData(null, 200, 201, 1)]
  [InlineData(1_000, 200, 201, 1)]
  [InlineData(1_000, 100, 101, 2)]
  [InlineData(1_000, 5_000, 801, 1)]
  public void ConfiguredTriggerRespectsHeadroomAndLimitsRetainedHistory(int? contextWindow, int? trigger,
      long observedTokens, int? expectedStart) {
    var history = new ModelHistory([
      Entry(1, 0, true, ChatRole.User, new string('o', 2_000)),
      Entry(2, 0, true, ChatRole.User, new string('m', 120)),
      Entry(3, 0, true, ChatRole.User, new string('n', 120))
    ], UsageBasedContextTokens: observedTokens);
    LlmSelection selection = TestLlmSelection.Default with { ContextWindow = contextWindow };

    HistoryCompactionPlan? plan = HistoryCompactor.Prepare(history, selection, emptyStartingContext,
        CompactionReason.Threshold, compactionTriggerTokenCount: trigger);

    Assert.Equal(expectedStart, plan?.FirstRetainedMessageIndex);
    if (plan is null) return;
    long retainedTokens = ContextTokenBudget.EstimateMessages(history.Messages.Skip(plan.FirstRetainedMessageIndex)
        .Select(message => message.Message));
    int effectiveTrigger = ContextTokenBudget.CompactionTrigger(contextWindow, trigger)!.Value;
    Assert.True(retainedTokens <= effectiveTrigger / 2);
  }

  [Fact]
  public void ManualCompactionUsesConfiguredRetentionWithoutWaitingForTrigger() {
    var history = new ModelHistory([
      Entry(1, 0, true, ChatRole.User, "old"),
      Entry(2, 0, true, ChatRole.User, new string('m', 120)),
      Entry(3, 0, true, ChatRole.User, new string('n', 120))
    ], UsageBasedContextTokens: 50);

    Assert.Null(HistoryCompactor.Prepare(history, TestLlmSelection.Default, emptyStartingContext,
        CompactionReason.Threshold, compactionTriggerTokenCount: 100));
    Assert.Equal(2, HistoryCompactor.Prepare(history, TestLlmSelection.Default, emptyStartingContext,
        CompactionReason.Manual, compactionTriggerTokenCount: 100)?.FirstRetainedMessageIndex);
  }

  [Fact]
  public void BoundarySelectionPrefersWholeTurnsAndOldestAffordableCut() {
    var history = new ModelHistory([
      Entry(1, 0, true, ChatRole.User, "old"),
      Entry(1, 1, false, ChatRole.Assistant, "earlier assistant"),
      Entry(2, 0, true, ChatRole.User, "middle turn"),
      Entry(3, 0, true, ChatRole.User, "latest turn")
    ]);

    HistoryCompactionPlan? plan = HistoryCompactor.Prepare(history, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual);

    Assert.Equal(2, plan?.FirstRetainedMessageIndex);
  }

  [Fact]
  public void PreviousBoundaryMustAdvance() {
    var previousLocation = new HistoryMessageLocation(Guid.NewGuid(), 1, 0, 0);
    var history = new ModelHistory([
      Entry(0, 0, true, ChatRole.User, "summarized"),
      Entry(1, 0, true, ChatRole.User, "at previous boundary"),
      Entry(2, 0, true, ChatRole.User, "new turn")
    ], new SavedHistoryCompaction(Guid.NewGuid(), previousLocation, "summary"));

    HistoryCompactionPlan? plan = HistoryCompactor.Prepare(history, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual);

    Assert.Equal(2, plan?.FirstRetainedMessageIndex);
    Assert.True(plan!.FirstRetainedMessage.Location.IsAfter(previousLocation));
  }

  [Fact]
  public void LocationsCompareExecutionRequestAndMessageOrderRegardlessOfIds() {
    var location = new HistoryMessageLocation(Guid.NewGuid(), 4, 2, 3);

    Assert.True(new HistoryMessageLocation(Guid.NewGuid(), 5, 0, 0).IsAfter(location));
    Assert.True(new HistoryMessageLocation(Guid.NewGuid(), 4, 3, 0).IsAfter(location));
    Assert.True(new HistoryMessageLocation(Guid.NewGuid(), 4, 2, 4).IsAfter(location));
    Assert.False(new HistoryMessageLocation(Guid.NewGuid(), 3, 9, 9).IsAfter(location));
    Assert.False(new HistoryMessageLocation(Guid.NewGuid(), 4, 1, 9).IsAfter(location));
    var sameOrderDifferentId = new HistoryMessageLocation(Guid.NewGuid(), 4, 2, 3);
    Assert.False(sameOrderDifferentId.IsAfter(location));
    Assert.False(location.IsAfter(sameOrderDifferentId));
  }

  [Fact]
  public void OverflowForcesProgressWhileKeepingNewestTurn() {
    var context = new ModelHistory([
      Entry(0, 0, true, ChatRole.User, "older"),
      Entry(1, 0, true, ChatRole.User, new string('o', 2_500)),
      Entry(2, 0, true, ChatRole.User, new string('n', 2_500))
    ]);
    LlmSelection selection = TestLlmSelection.Default with { ContextWindow = 1_000 };

    Assert.Null(HistoryCompactor.Prepare(context, selection, emptyStartingContext, CompactionReason.Manual));
    HistoryCompactionPlan? plan = HistoryCompactor.Prepare(context, selection, emptyStartingContext, CompactionReason.Overflow);

    Assert.Equal(2, plan?.FirstRetainedMessageIndex);
    Assert.Equal(new string('n', 2_500), plan?.FirstRetainedMessage.Message.Contents.OfType<TextContent>().Single().Text);
  }

  [Fact]
  public void OversizedTurnCanCutBeforeAssistantButNeverBeforeToolResult() {
    var context = new ModelHistory([
      Entry(1, 0, true, ChatRole.User, new string('u', 100_000)),
      Entry(1, 1, false, ChatRole.Assistant, "call", new FunctionCallContent("call", "search", new Dictionary<string, object?>())),
      Entry(1, 2, false, ChatRole.Tool, "", new FunctionResultContent("call", new string('r', 100_000))),
      Entry(1, 3, false, ChatRole.Assistant, "answer")
    ]);

    HistoryCompactionPlan? plan = HistoryCompactor.Prepare(context, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual);

    Assert.Equal(3, plan?.FirstRetainedMessageIndex);
    Assert.Equal(ChatRole.Assistant, plan?.FirstRetainedMessage.Message.Role);
    Assert.DoesNotContain(plan!.History.Messages.Skip(plan.FirstRetainedMessageIndex).Take(1),
        entry => entry.Message.Role == ChatRole.Tool);
  }

  [Fact]
  public async Task SummaryIsIterativeSafeBoundedAndDoesNotDisposeBorrowedClient() {
    var previous = new SavedHistoryCompaction(Guid.NewGuid(), new(Guid.NewGuid(), 0, 0, 0), "Files read: src/old.cs");
    var context = new ModelHistory([
      Entry(1, 0, true, ChatRole.User, new string('x', 5_000),
          new DataContent(new byte[] { 1, 2, 3 }, "image/png")),
      Entry(1, 1, false, ChatRole.Assistant, "", new FunctionCallContent("call", "search", new Dictionary<string, object?> { ["query"] = "x" })),
      Entry(1, 2, false, ChatRole.Tool, "", new FunctionResultContent("call", new string('r', 3_000))),
      Entry(2, 0, true, ChatRole.User, "current")
    ], previous);
    HistoryCompactionPlan plan = HistoryCompactor.Prepare(context, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual)!;
    var client = new SummaryClient("Goal: keep context.\nFiles read: src/old.cs");

    GeneratedCompactionSummary result = await HistoryCompactor.SummarizeAsync(client, TestLlmSelection.Default, plan, default);

    Assert.True(result.EstimatedContextTokensAfter < plan.EstimatedContextTokensBefore);
    Assert.Equal(1, client.Requests);
    Assert.Empty(client.Options!.Tools!);
    Assert.Equal(8_192, client.Options.MaxOutputTokens);
    Assert.Contains("Files read: src/old.cs", client.Prompt);
    Assert.Contains("[image or binary content omitted]", client.Prompt);
    Assert.Contains("…[truncated]", client.Prompt);
    Assert.Equal(0, client.DisposeCalls);
  }

  [Fact]
  public async Task SummaryRejectsNonreducingOrIncompleteResponses() {
    var history = new ModelHistory([
      Entry(1, 0, true, ChatRole.User, new string('x', 2_000)),
      Entry(2, 0, true, ChatRole.User, "current")
    ]);
    HistoryCompactionPlan plan = HistoryCompactor.Prepare(history, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual)!;

    await Assert.ThrowsAsync<InvalidDataException>(() => HistoryCompactor.SummarizeAsync(
        new SummaryClient(new string('s', 4_000)), TestLlmSelection.Default, plan, default));
    await Assert.ThrowsAsync<InvalidDataException>(() => HistoryCompactor.SummarizeAsync(
        new SummaryClient("summary", finishReason: ChatFinishReason.Length), TestLlmSelection.Default, plan, default));
    await Assert.ThrowsAsync<InvalidDataException>(() => HistoryCompactor.SummarizeAsync(
        new SummaryClient("summary", includeToolCall: true), TestLlmSelection.Default, plan, default));
  }

  [Fact]
  public async Task CancellationProducesNoCompactionResult() {
    var context = new ModelHistory([
      Entry(1, 0, true, ChatRole.User, new string('x', 2_000)),
      Entry(2, 0, true, ChatRole.User, "current")
    ]);
    HistoryCompactionPlan plan = HistoryCompactor.Prepare(context, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual)!;
    var client = new SummaryClient(waitForCancellation: true);
    using var cancellation = new CancellationTokenSource();
    Task<GeneratedCompactionSummary> pending = HistoryCompactor.SummarizeAsync(client, TestLlmSelection.Default, plan, cancellation.Token);
    await cancellation.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    Assert.Equal(1, client.Requests);
  }

  private static HistoryMessage Entry(long run, int index, bool startsTurn, ChatRole role, string text,
      params AIContent[] contents) {
    ChatMessage message;
    if (contents.Length == 0)
      message = new ChatMessage(role, text);
    else if (text.Length == 0)
      message = new ChatMessage(role, contents);
    else
      message = new ChatMessage(role, [new TextContent(text), .. contents]);
    return new HistoryMessage(new(Guid.NewGuid(), run, 0, index), startsTurn, message);
  }

  private sealed class SummaryClient(string? summary = null, bool waitForCancellation = false,
      ChatFinishReason? finishReason = null, bool includeToolCall = false) : IChatClient {
    internal int Requests { get; private set; }
    internal int DisposeCalls { get; private set; }
    internal string Prompt { get; private set; } = "";
    internal ChatOptions? Options { get; private set; }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) {
      Requests++;
      Prompt = string.Join("\n", messages.SelectMany(message => message.Contents.OfType<TextContent>()
          .Select(content => content.Text)));
      Options = options;
      if (waitForCancellation) return WaitForCancellation(cancellationToken);
      ChatMessage response = includeToolCall
          ? new ChatMessage(ChatRole.Assistant, [new TextContent(summary!),
              new FunctionCallContent("summary-call", "lookup", new Dictionary<string, object?>())])
          : new ChatMessage(ChatRole.Assistant, summary!);
      return Task.FromResult(new ChatResponse(response) { FinishReason = finishReason ?? ChatFinishReason.Stop });
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() => DisposeCalls++;

    private static async Task<ChatResponse> WaitForCancellation(CancellationToken cancellationToken) {
      var completion = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
      using CancellationTokenRegistration registration = cancellationToken.Register(
          () => completion.TrySetCanceled(cancellationToken));
      return await completion.Task.ConfigureAwait(false);
    }
  }
}
