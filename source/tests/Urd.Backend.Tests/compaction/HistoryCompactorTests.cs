using Microsoft.Extensions.AI;
using Xunit;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Providers;

namespace Urd.Backend.Tests.Compaction;

public sealed class HistoryCompactorTests {
  [Fact]
  public void BudgetsScaleForSmallWindowsAndEstimateVisibleContent() {
    Assert.Equal(83_616, ContextBudget.Threshold(100_000));
    Assert.Equal(400, ContextBudget.Recent(1_000));
    Assert.Equal(20_000, ContextBudget.Recent(null));
    Assert.Equal(250, ContextBudget.OutputBudget(1_000));
    Assert.Equal(8_192, ContextBudget.OutputBudget(null));
    Assert.True(ContextBudget.Estimate([new ChatMessage(ChatRole.User, new string('x', 400))]) >= 108);
  }

  [Fact]
  public void ThresholdUsesObservedTokensAndUnknownWindowsSkipAutomaticCompaction() {
    var context = new CompactionContext([
      Entry(1, 0, true, ChatRole.User, "old"),
      Entry(1, 1, false, ChatRole.Assistant, "answer"),
      Entry(2, 0, true, ChatRole.User, "current")
    ], ObservedTokens: 900);
    LlmSelection selection = TestLlmSelection.Default with { ContextWindow = 1_000 };

    CompactionPlan? plan = HistoryCompactor.Prepare(context, selection, [], [], CompactionReason.Threshold);

    Assert.Equal(2, plan?.FirstKeptIndex);
    Assert.Null(HistoryCompactor.Prepare(context, selection with { ContextWindow = null }, [], [], CompactionReason.Threshold));
  }

  [Fact]
  public void OverflowForcesProgressWhileKeepingNewestTurn() {
    var context = new CompactionContext([
      Entry(1, 0, true, ChatRole.User, new string('o', 2_500)),
      Entry(2, 0, true, ChatRole.User, new string('n', 2_500))
    ]);
    LlmSelection selection = TestLlmSelection.Default with { ContextWindow = 1_000 };

    Assert.Null(HistoryCompactor.Prepare(context, selection, [], [], CompactionReason.Manual));
    CompactionPlan? plan = HistoryCompactor.Prepare(context, selection, [], [], CompactionReason.Overflow);

    Assert.Equal(1, plan?.FirstKeptIndex);
    Assert.Equal(new string('n', 2_500), plan?.FirstKept.Message.Contents.OfType<TextContent>().Single().Text);
  }

  [Fact]
  public void OversizedTurnCanCutBeforeAssistantButNeverBeforeToolResult() {
    var context = new CompactionContext([
      Entry(1, 0, true, ChatRole.User, new string('u', 100_000)),
      Entry(1, 1, false, ChatRole.Assistant, "call", new FunctionCallContent("call", "search", new Dictionary<string, object?>())),
      Entry(1, 2, false, ChatRole.Tool, "", new FunctionResultContent("call", new string('r', 100_000))),
      Entry(1, 3, false, ChatRole.Assistant, "answer")
    ]);

    CompactionPlan? plan = HistoryCompactor.Prepare(context, TestLlmSelection.Default, [], [], CompactionReason.Manual);

    Assert.Equal(3, plan?.FirstKeptIndex);
    Assert.Equal(ChatRole.Assistant, plan?.FirstKept.Message.Role);
    Assert.DoesNotContain(plan!.Context.Entries.Skip(plan.FirstKeptIndex).Take(1), entry => entry.Message.Role == ChatRole.Tool);
  }

  [Fact]
  public async Task SummaryIsIterativeSafeBoundedAndDoesNotDisposeBorrowedClient() {
    var previous = new CompactionSummary(Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid(), Guid.NewGuid(),
        0, 0, 0, "Files read: src/old.cs", CompactionReason.Manual, 0, 0, DateTimeOffset.UtcNow);
    var context = new CompactionContext([
      Entry(1, 0, true, ChatRole.User, new string('x', 5_000),
          new DataContent(new byte[] { 1, 2, 3 }, "image/png")),
      Entry(1, 1, false, ChatRole.Assistant, "", new FunctionCallContent("call", "search", new Dictionary<string, object?> { ["query"] = "x" })),
      Entry(1, 2, false, ChatRole.Tool, "", new FunctionResultContent("call", new string('r', 3_000))),
      Entry(2, 0, true, ChatRole.User, "current")
    ], previous);
    CompactionPlan plan = HistoryCompactor.Prepare(context, TestLlmSelection.Default, [], [], CompactionReason.Manual)!;
    var client = new SummaryClient("Goal: keep context.\nFiles read: src/old.cs");

    CompactionResult result = await HistoryCompactor.SummarizeAsync(client, TestLlmSelection.Default, plan, default);

    Assert.True(result.TokensAfter < plan.TokensBefore);
    Assert.Equal(1, client.Requests);
    Assert.Empty(client.Options!.Tools!);
    Assert.Equal(8_192, client.Options.MaxOutputTokens);
    Assert.Contains("Files read: src/old.cs", client.Prompt);
    Assert.Contains("[image or binary content omitted]", client.Prompt);
    Assert.Contains("…[truncated]", client.Prompt);
    Assert.Equal(0, client.DisposeCalls);
  }

  [Fact]
  public async Task CancellationProducesNoCompactionResult() {
    var context = new CompactionContext([
      Entry(1, 0, true, ChatRole.User, new string('x', 2_000)),
      Entry(2, 0, true, ChatRole.User, "current")
    ]);
    CompactionPlan plan = HistoryCompactor.Prepare(context, TestLlmSelection.Default, [], [], CompactionReason.Manual)!;
    var client = new SummaryClient(waitForCancellation: true);
    using var cancellation = new CancellationTokenSource();
    Task<CompactionResult> pending = HistoryCompactor.SummarizeAsync(client, TestLlmSelection.Default, plan, cancellation.Token);
    await cancellation.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    Assert.Equal(1, client.Requests);
  }

  private static ContextEntry Entry(long run, int index, bool startsTurn, ChatRole role, string text,
      params AIContent[] contents) {
    ChatMessage message;
    if (contents.Length == 0)
      message = new ChatMessage(role, text);
    else if (text.Length == 0)
      message = new ChatMessage(role, contents);
    else
      message = new ChatMessage(role, [new TextContent(text), .. contents]);
    return new ContextEntry(Guid.NewGuid(), run, 0, index, startsTurn, message);
  }

  private sealed class SummaryClient(string? summary = null, bool waitForCancellation = false) : IChatClient {
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
      return waitForCancellation ? WaitForCancellation(cancellationToken) : Task.FromResult(new ChatResponse(
          new ChatMessage(ChatRole.Assistant, summary!)) { FinishReason = ChatFinishReason.Stop });
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
