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
  public void CreatePlanValidatesStartingContextAndItsContents() {
    var history = new ModelHistory([]);

    Assert.Throws<ArgumentNullException>(() => HistoryCompactor.CreatePlan(history, TestLlmSelection.Default, null!, CompactionReason.Manual));
    Assert.Throws<ArgumentNullException>(() => HistoryCompactor.CreatePlan(history, TestLlmSelection.Default, new(null!, []), CompactionReason.Manual));
    Assert.Throws<ArgumentNullException>(() => HistoryCompactor.CreatePlan(history, TestLlmSelection.Default, new([], null!), CompactionReason.Manual));
  }

  [Fact]
  public void PrepareIncludesStartingInstructionsInContextEstimate() {
    var history = new ModelHistory([
      Entry(1, 0, true, ChatRole.User, "old"),
      Entry(2, 0, true, ChatRole.User, "current")
    ]);
    var startingContext = new StartingContext([new ChatMessage(ChatRole.System, new string('i', 1_000))], []);

    HistoryCompactionPlan emptyPlan = HistoryCompactor.CreatePlan(history, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual)!;
    HistoryCompactionPlan instructionPlan = HistoryCompactor.CreatePlan(history, TestLlmSelection.Default, startingContext, CompactionReason.Manual)!;

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

    HistoryCompactionPlan? plan = HistoryCompactor.CreatePlan(context, selection, emptyStartingContext, CompactionReason.Threshold);

    Assert.Equal(2, plan?.FirstRetainedMessageIndex);
    Assert.Null(HistoryCompactor.CreatePlan(context, selection with { ContextWindow = null }, emptyStartingContext, CompactionReason.Threshold));
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

    HistoryCompactionPlan? plan = HistoryCompactor.CreatePlan(history, selection, emptyStartingContext,
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

    Assert.Null(HistoryCompactor.CreatePlan(history, TestLlmSelection.Default, emptyStartingContext,
        CompactionReason.Threshold, compactionTriggerTokenCount: 100));
    Assert.Equal(2, HistoryCompactor.CreatePlan(history, TestLlmSelection.Default, emptyStartingContext,
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

    HistoryCompactionPlan? plan = HistoryCompactor.CreatePlan(history, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual);

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

    HistoryCompactionPlan? plan = HistoryCompactor.CreatePlan(history, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual);

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

  [Theory]
  [InlineData(CompactionReason.Manual)]
  [InlineData(CompactionReason.Overflow)]
  public void ManualAndOverflowCanKeepAnOverBudgetLatestTurn(CompactionReason reason) {
    var context = new ModelHistory([
      Entry(0, 0, true, ChatRole.User, "older"),
      Entry(1, 0, true, ChatRole.User, new string('o', 2_500)),
      Entry(2, 0, true, ChatRole.User, new string('n', 2_500))
    ]);
    LlmSelection selection = TestLlmSelection.Default with { ContextWindow = 1_000 };

    Assert.Null(HistoryCompactor.CreatePlan(context, selection, emptyStartingContext, CompactionReason.Threshold));
    HistoryCompactionPlan? plan = HistoryCompactor.CreatePlan(context, selection, emptyStartingContext, reason);

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

    HistoryCompactionPlan? plan = HistoryCompactor.CreatePlan(context, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual);

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
    HistoryCompactionPlan plan = HistoryCompactor.CreatePlan(context, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual)!;
    plan = plan with { BackgroundToolCalls = [new(Guid.NewGuid(), "call", "search", "{\"query\":\"x\"}")] };
    using var client = CreateClient("Goal: keep context.\nFiles read: src/old.cs");

    GeneratedCompactionSummary result = await HistoryCompactor.GenerateSummaryAsync(client, TestLlmSelection.Default, plan, default);

    Assert.True(result.EstimatedContextTokensAfter < plan.EstimatedContextTokensBefore);
    Assert.Equal("Goal: keep context.\nFiles read: src/old.cs", result.Summary);
    Assert.Equal(ContextTokenBudget.EstimateMessages(
        new[] { ModelHistory.SummaryMessage(plan.AppendBackgroundToolCalls(result.Summary)) }
            .Concat(context.Messages.Skip(plan.FirstRetainedMessageIndex).Select(entry => entry.Message))),
        result.EstimatedContextTokensAfter);
    IReadOnlyList<ChatMessage> request = Assert.Single(client.Requests);
    ChatOptions options = Assert.IsType<ChatOptions>(Assert.Single(client.Options));
    string prompt = string.Join("\n", request.SelectMany(message => message.Contents.OfType<TextContent>()
        .Select(content => content.Text)));
    Assert.Empty(options.Tools!);
    Assert.Equal(8_192, options.MaxOutputTokens);
    Assert.Contains("Files read: src/old.cs", prompt);
    Assert.Contains("[image or binary content omitted]", prompt);
    Assert.Contains("…[truncated]", prompt);
    Assert.Equal(0, client.DisposeCalls);
  }

  [Fact]
  public async Task SummaryRejectsNonreducingOrIncompleteResponses() {
    var history = new ModelHistory([
      Entry(1, 0, true, ChatRole.User, new string('x', 2_000)),
      Entry(2, 0, true, ChatRole.User, "current")
    ]);
    HistoryCompactionPlan plan = HistoryCompactor.CreatePlan(history, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual)!;

    await Assert.ThrowsAsync<InvalidDataException>(() => HistoryCompactor.GenerateSummaryAsync(
        CreateClient(new string('s', 4_000)), TestLlmSelection.Default, plan, default));
    await Assert.ThrowsAsync<InvalidDataException>(() => HistoryCompactor.GenerateSummaryAsync(
        CreateClient("summary", finishReason: ChatFinishReason.Length), TestLlmSelection.Default, plan, default));
    await Assert.ThrowsAsync<InvalidDataException>(() => HistoryCompactor.GenerateSummaryAsync(
        CreateClient("summary", includeToolCall: true), TestLlmSelection.Default, plan, default));
  }

  [Fact]
  public async Task CancellationProducesNoCompactionResult() {
    var context = new ModelHistory([
      Entry(1, 0, true, ChatRole.User, new string('x', 2_000)),
      Entry(2, 0, true, ChatRole.User, "current")
    ]);
    HistoryCompactionPlan plan = HistoryCompactor.CreatePlan(context, TestLlmSelection.Default, emptyStartingContext, CompactionReason.Manual)!;
    using var client = new ScriptedChatClient(_ => throw new NotSupportedException(), WaitForCancellation);
    using var cancellation = new CancellationTokenSource();
    Task<GeneratedCompactionSummary> pending = HistoryCompactor.GenerateSummaryAsync(client, TestLlmSelection.Default, plan, cancellation.Token);
    await cancellation.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    Assert.Single(client.Requests);
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

  private static ScriptedChatClient CreateClient(string summary, ChatFinishReason? finishReason = null,
      bool includeToolCall = false) => new(_ => throw new NotSupportedException(), _ => {
    var text = new TextContent(summary) { RawRepresentation = new object() };
    ChatMessage response = includeToolCall
        ? new ChatMessage(ChatRole.Assistant, [text,
            new FunctionCallContent("summary-call", "lookup", new Dictionary<string, object?>())])
        : new ChatMessage(ChatRole.Assistant, [text]);
    return Task.FromResult(new ChatResponse(response) { FinishReason = finishReason ?? ChatFinishReason.Stop });
  });

  private static async Task<ChatResponse> WaitForCancellation(CancellationToken cancellationToken) {
    var completion = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
    using CancellationTokenRegistration registration = cancellationToken.Register(
        () => completion.TrySetCanceled(cancellationToken));
    return await completion.Task.ConfigureAwait(false);
  }
}
