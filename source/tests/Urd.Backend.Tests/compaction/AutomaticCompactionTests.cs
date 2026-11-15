using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Compaction;

public sealed class AutomaticCompactionTests {
  private const string Summary = "Goal: retain the latest decision.";

  [Fact]
  public async Task ThresholdCompactsBeforeRequestAndKeepsSafeToolTurnAfterOneOlderCut() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    int toolRuns = 0;
    var tool = new TestAgentTool("lookup", (_, _, _) => {
      Interlocked.Increment(ref toolRuns);
      return Task.FromResult(new ToolOutput("safe tool result", []));
    });
    using var client = new TestChatClient((call, token) => call switch {
      1 => Complete(token),
      2 => ToolBatch(token, new FunctionCallContent("lookup-call", "lookup", new Dictionary<string, object?>())),
      3 or 4 => Complete(token),
      _ => throw new InvalidOperationException("Unexpected model request.")
    }, holdSummary: true);
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)), store, _ => client,
        new ToolCatalog([tool]));
    await SubmitAsync(session, new string('o', 100_000), TestLlmSelection.Default);
    await SubmitAsync(session, "tool turn", TestLlmSelection.Default);
    Assert.Equal(0, client.SummaryCalls); // Unknown windows disable automatic compaction.

    var compacting = Signal();
    using IDisposable subscription = await session.SubscribeAsync(change => {
      if (change.Status == "compacting") compacting.TrySetResult();
    }, watched: true);
    UserMessageReceipt receipt = await AcceptAsync(session, "current turn", TestLlmSelection.Default with { ContextWindow = 8_192 });
    Task<SessionExecutionResult> settlement = session.WaitForSettlementAsync(Assert.IsType<Guid>(receipt.ExecutionId));
    try {
      await client.SummaryEntered.WaitAsync(TestTimeout);
      await compacting.Task.WaitAsync(TestTimeout);
      client.ReleaseSummary();
      Assert.Equal(ExecutionOutcome.Completed, (await settlement.WaitAsync(TestTimeout)).Outcome);
    } finally {
      client.ReleaseSummary();
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(1, client.SummaryCalls);
    Assert.Equal(1, toolRuns);
    Assert.Equal(4, client.Requests.Length);
    IReadOnlyList<ChatMessage> request = client.Requests[3];
    Assert.Contains(request, message => message.Contents.OfType<TextContent>().Any(content => content.Text.Contains("[HISTORY SUMMARY:")));
    Assert.DoesNotContain(request, message => message.Contents.OfType<TextContent>()
        .Any(content => content.Text.Contains(new string('o', 128))));
    Assert.Contains(request, message => message.Role == ChatRole.Assistant &&
        message.Contents.OfType<FunctionCallContent>().Any(call => call.Name == "lookup"));
    Assert.Contains(request, message => message.Role == ChatRole.Tool &&
        message.Contents.OfType<FunctionResultContent>().Any(result => Equals(result.Result, "safe tool result")));

    long revision = (await session.ReadSnapshotAsync()).HistoryRevision;
    ModelHistory context = store.Compactions.ReadContext(session.Id, revision);
    Assert.Equal(CompactionReason.Threshold, (await session.ReadCompactionAsync(context.PreviousCompaction!.Id, revision))?.Reason);
    Assert.DoesNotContain(context.Messages, entry => entry.Message.Contents.OfType<TextContent>()
        .Any(content => content.Text.Contains(new string('o', 128))));
    Assert.Contains(context.Messages, entry => entry.Message.Role == ChatRole.Tool);
    Assert.Contains(context.Messages, entry => entry.StartsUserTurn && entry.Message.Contents.OfType<TextContent>()
        .Any(content => content.Text == "tool turn"));
    Assert.Single((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision))).Entries,
        entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
  }

  [Fact]
  public async Task ChangingTriggerDuringExecutionAffectsNextCompactionCheck() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var preferences = new PreferenceManager(store);
    preferences.SetCompactionTriggerTokenCount(int.MaxValue);
    var entered = Signal();
    var release = Signal();
    int toolRuns = 0;
    var tool = new TestAgentTool("lookup", async (_, _, cancellation) => {
      Interlocked.Increment(ref toolRuns);
      entered.TrySetResult();
      await release.Task.WaitAsync(cancellation);
      return new ToolOutput("safe tool result", []);
    });
    using var client = new TestChatClient((call, token) => call switch {
      1 => ToolBatch(token, new FunctionCallContent("lookup-call", "lookup", new Dictionary<string, object?>())),
      2 or 3 => Complete(token),
      _ => throw new InvalidOperationException("Unexpected model request.")
    });
    var manager = new SessionManager(store, _ => client, [tool], Path.GetDirectoryName(database.Path)!);
    try {
      PrimarySession session = await manager.CreatePrimaryAsync();
      UserMessageReceipt receipt = await AcceptAsync(session, new string('o', 100_000), TestLlmSelection.Default);
      await entered.Task.WaitAsync(TestTimeout);
      Assert.Equal(0, client.SummaryCalls);
      preferences.SetCompactionTriggerTokenCount(1_000);
      release.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForSettlementAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);

      Assert.Equal(1, client.SummaryCalls);
      Assert.Equal(1, toolRuns);
      Assert.Equal(2, client.Requests.Length);
      IReadOnlyList<ChatMessage> request = client.Requests[1];
      Assert.Contains(request, message => message.Contents.OfType<TextContent>()
          .Any(content => content.Text.Contains("[HISTORY SUMMARY:")));
      Assert.DoesNotContain(request, message => message.Contents.OfType<TextContent>()
          .Any(content => content.Text.Contains(new string('o', 128))));
      Assert.Contains(request, message => message.Role == ChatRole.Assistant &&
          message.Contents.OfType<FunctionCallContent>().Any(call => call.Name == "lookup" && call.CallId == "lookup-call"));
      Assert.Contains(request, message => message.Role == ChatRole.Tool &&
          message.Contents.OfType<FunctionResultContent>().Any(result => result.CallId == "lookup-call" &&
              Equals(result.Result, "safe tool result")));

      preferences.SetCompactionTriggerTokenCount(null);
      await SubmitAsync(session, new string('n', 100_000), TestLlmSelection.Default);
      Assert.Equal(3, client.Requests.Length);
      Assert.Equal(1, client.SummaryCalls);
      Assert.Equal(1, toolRuns);
      Assert.Contains(client.Requests[2], message => message.Role == ChatRole.User &&
          message.Contents.OfType<TextContent>().Any(content => content.Text.Contains(new string('n', 128))));
    } finally {
      release.TrySetResult();
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task EditingSteeringInvalidatesAnEarlierMarkerThatSummarizedThatSameRequest() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var release = Signal();
    var tool = new TestAgentTool("lookup", async (_, _, cancellation) => {
      entered.TrySetResult();
      await release.Task.WaitAsync(cancellation);
      return new ToolOutput("kept result", []);
    });
    using var client = new TestChatClient((call, token) => call == 1
        ? ToolBatch(token, new FunctionCallContent("lookup", "lookup", new Dictionary<string, object?>())) : Complete(token));
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)), store, _ => client,
        new ToolCatalog([tool]));
    var selection = TestLlmSelection.Default with { ContextWindow = 8_192 };
    try {
      var initial = await AcceptAsync(session, "start", selection);
      await entered.Task.WaitAsync(TestTimeout);
      var removed = new UserMessageSubmission(Guid.NewGuid(), 0, new string('x', 100_000), selection);
      await session.SubmitUserMessageAsync(removed);
      await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "last steering input", selection));
      release.TrySetResult();
      await session.WaitForSettlementAsync(initial.ExecutionId!.Value).WaitAsync(TestTimeout);
      var context = store.Compactions.ReadContext(session.Id, 0);
      Assert.Equal(1, context.PreviousCompaction!.FirstRetainedMessageLocation.RequestIndexInExecution);
      Assert.Equal(1, context.PreviousCompaction.FirstRetainedMessageLocation.MessageIndexInRequest); // The first steering input was summarized.
      var history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
      var target = Assert.Single(history.Entries, entry => entry.Entry.EntryId == removed.SubmissionId).Text!.From.Message;
      Assert.Contains(history.Entries.TakeWhile(entry => entry.Entry.EntryId != removed.SubmissionId),
          entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);

      var edited = await session.EditUserMessageAsync(new(Guid.NewGuid(), target, "new steering", TestLlmSelection.Default));
      await session.WaitForSettlementAsync(edited.ExecutionId).WaitAsync(TestTimeout);
      Assert.Null(store.Compactions.ReadContext(session.Id, edited.HistoryRevision).PreviousCompaction);
      Assert.DoesNotContain(client.Requests[^1], message => message.Text?.Contains(Summary, StringComparison.Ordinal) == true);
      Assert.Equal(new[] { "start", "new steering" }, client.Requests[^1]
          .Where(message => message.Role == ChatRole.User).Select(message => message.Text));
      Assert.DoesNotContain((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, edited.HistoryRevision))).Entries,
          entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
    } finally {
      release.TrySetResult();
      await session.FenceUserMessagesAsync();
      await session.StopExecutionAsync().WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task ContextWindowOverflowSummarizesAndRetriesWithoutRepeatingUserOrTool() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    int toolRuns = 0;
    var tool = new TestAgentTool("lookup", (_, _, _) => {
      Interlocked.Increment(ref toolRuns);
      return Task.FromResult(new ToolOutput("overflow result", []));
    });
    using var client = new TestChatClient((call, token) => call switch {
      1 => ToolBatch(token, new FunctionCallContent("overflow-call", "lookup", new Dictionary<string, object?>())),
      2 => Fail(ProviderFailure.ContextWindowExceeded, false, token),
      3 => Complete(token),
      _ => throw new InvalidOperationException("Unexpected model request.")
    });
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)), store, _ => client,
        new ToolCatalog([tool]));
    UserMessageReceipt receipt = await AcceptAsync(session, new string('u', 100_000), TestLlmSelection.Default);

    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForSettlementAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(3, client.Requests.Length);
    Assert.Equal(1, client.SummaryCalls);
    Assert.Equal(1, toolRuns);
    Assert.Single(StoredTools.Read(database));
    long revision = (await session.ReadSnapshotAsync()).HistoryRevision;
    Guid compactionId = store.Compactions.ReadContext(session.Id, revision).PreviousCompaction!.Id;
    Assert.Equal(CompactionReason.Overflow, (await session.ReadCompactionAsync(compactionId, revision))?.Reason);
    Assert.Equal(2, StoredExecutions.ReadRequests(database).Count);
    Assert.Contains(client.Requests[2], message => message.Contents.OfType<TextContent>()
        .Any(content => content.Text.Contains("[HISTORY SUMMARY:")));
    Assert.Contains(client.Requests[2], message => message.Role == ChatRole.Tool &&
        message.Contents.OfType<FunctionResultContent>().Any(result => Equals(result.Result, "overflow result")));
    Assert.Single((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest,
        (await session.ReadSnapshotAsync()).HistoryRevision))).Entries, entry => entry.Entry.Kind == TranscriptEntryKind.User);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task NonOverflowAndPartialOutputOverflowAreNeverRetried(bool partialOutput) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new TestChatClient((call, token) => call switch {
      1 => Complete(token),
      2 => Fail(partialOutput ? ProviderFailure.ContextWindowExceeded : ProviderFailure.ProviderUnavailable,
          partialOutput, token),
      _ => throw new InvalidOperationException("A failed request must not be retried.")
    });
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);
    await SubmitAsync(session, new string('o', 100_000), TestLlmSelection.Default);
    UserMessageReceipt receipt = await AcceptAsync(session, "current", TestLlmSelection.Default);

    Assert.Equal(ExecutionOutcome.Failed,
        (await session.WaitForSettlementAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(2, client.Requests.Length);
    Assert.Equal(0, client.SummaryCalls);
    Assert.Equal(2, StoredExecutions.ReadRequests(database).Count);
    if (partialOutput) {
      TranscriptHistoryPage history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
      TranscriptEntryDescriptor partial = Assert.Single(history.Entries,
          entry => entry.Entry.ExecutionId == receipt.ExecutionId && entry.Entry.Kind == TranscriptEntryKind.Text);
      Assert.Contains("partial output", (await session.Transcript.ReadContentAsync(partial.Text!.From)).Text);
    }
  }

  private static async Task SubmitAsync(PrimarySession session, string text, LlmSelection selection) {
    UserMessageReceipt receipt = await AcceptAsync(session, text, selection);
    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForSettlementAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
  }

  private static async Task<UserMessageReceipt> AcceptAsync(PrimarySession session, string text, LlmSelection selection) {
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, text, selection));
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    return receipt;
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> Fail(ProviderFailure failure, bool partial,
      [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) {
    if (partial) yield return Text("partial output");
    await Task.Yield();
    token.ThrowIfCancellationRequested();
    throw new ProviderOperationException(failure);
  }

  private sealed class TestChatClient(
      Func<int, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> stream,
      bool holdSummary = false) : IChatClient {
    private readonly ConcurrentQueue<IReadOnlyList<ChatMessage>> requests = new();
    private readonly TaskCompletionSource? summaryEntered = holdSummary ? Signal() : null;
    private readonly TaskCompletionSource? summaryRelease = holdSummary ? Signal() : null;
    private int streamCalls, summaryCalls;
    internal IReadOnlyList<ChatMessage>[] Requests => requests.ToArray();
    internal int SummaryCalls => Volatile.Read(ref summaryCalls);
    internal Task SummaryEntered => summaryEntered?.Task ?? Task.CompletedTask;
    internal void ReleaseSummary() => summaryRelease?.TrySetResult();

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) {
      requests.Enqueue(messages.ToArray());
      return stream(Interlocked.Increment(ref streamCalls), cancellationToken);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref summaryCalls);
      if (summaryEntered is not null) {
        summaryEntered.TrySetResult();
        await summaryRelease!.Task.WaitAsync(cancellationToken);
      }
      return new(new ChatMessage(ChatRole.Assistant, Summary)) { FinishReason = ChatFinishReason.Stop };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
  }
}
