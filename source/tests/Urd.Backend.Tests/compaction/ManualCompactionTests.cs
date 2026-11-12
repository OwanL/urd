using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Compaction;

public sealed class ManualCompactionTests {
  private static readonly LlmSelection Selection = TestLlmSelection.Default with { ContextWindow = 8_192 };
  private const string Summary = "Goal: retain the latest decision.";

  [Fact]
  public async Task IdleCompactionIsIdempotentAndKeepsReadableHistoryAndLatestModelContextAfterReopen() {
    using var database = new TemporaryDatabase();
    using var client = new CompactionChatClient((_, _) => Task.FromResult(SummaryResponse()));
    Guid sessionId;
    long revision;
    CompactionSummary saved;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      PrimarySession session = CreateSession(store, client);
      sessionId = session.Id;
      await SubmitTurnAsync(session, "ancient-turn: " + new string('x', 100_000));
      await SubmitTurnAsync(session, "latest-turn: keep this decision");
      revision = (await session.ReadSnapshotAsync()).HistoryRevision;
      var submission = new CompactionSubmission(Guid.NewGuid(), revision, Selection);

      CompactionReceipt accepted = Assert.IsType<CompactionReceipt>(await session.CompactAsync(submission));
      CompactionReceipt retry = Assert.IsType<CompactionReceipt>(await session.CompactAsync(submission));
      Assert.Equal(accepted.ExecutionId, retry.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForSettlementAsync(accepted.ExecutionId).WaitAsync(TestTimeout)).Outcome);
      CompactionReceipt settled = Assert.IsType<CompactionReceipt>(await session.ReadCompactionReceiptAsync(submission.OperationId));
      Assert.Equal(accepted.ExecutionId, settled.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed, settled.Outcome);
      CompactionSummary boundary = Assert.IsType<CompactionSummary>(store.Compactions.ReadContext(session.Id, revision).Previous);
      saved = Assert.IsType<CompactionSummary>(await session.ReadCompactionAsync(boundary.Id, revision));
      Assert.Equal(Summary, saved.Summary);
      Assert.Equal(0, Assert.Single(client.SummaryToolCounts));

      TranscriptHistoryPage history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision));
      Assert.Single(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
      TranscriptEntryDescriptor original = history.Entries.First(entry => entry.Entry.Kind == TranscriptEntryKind.User);
      Assert.StartsWith("ancient-turn: ", (await session.Transcript.ReadContentAsync(original.Text!.From)).Text);
    }

    using (UrdStore reopened = UrdStore.Open(database.Path)) {
      PrimarySession session = new(Assert.IsType<SessionRecord>(reopened.Sessions.Read(sessionId)), reopened, _ => client);
      await SubmitTurnAsync(session, "after-reopen-turn");
      string modelContext = string.Join("\n", client.Requests[^1].SelectMany(message =>
          message.Contents.OfType<TextContent>().Select(content => content.Text)));
      Assert.Contains("[HISTORY SUMMARY:", modelContext);
      Assert.Contains(Summary, modelContext);
      Assert.Contains("latest-turn: keep this decision", modelContext);
      Assert.Contains("after-reopen-turn", modelContext);
      Assert.DoesNotContain("ancient-turn:", modelContext);
      Assert.Equal(saved.Id, Assert.IsType<CompactionSummary>(await session.ReadCompactionAsync(saved.Id, revision)).Id);

      var history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision));
      MessageRef latest = history.Entries.Last(entry => entry.Entry.Kind == TranscriptEntryKind.User).Text!.From.Message;
      var edited = await session.EditUserMessageAsync(new(Guid.NewGuid(), latest, "revised recent turn", TestLlmSelection.Default));
      await session.WaitForSettlementAsync(edited.ExecutionId).WaitAsync(TestTimeout);
      Assert.Equal(saved.Id, reopened.Compactions.ReadContext(sessionId, edited.HistoryRevision).Previous!.Id);

      history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, edited.HistoryRevision));
      MessageRef ancient = history.Entries.First(entry => entry.Entry.Kind == TranscriptEntryKind.User).Text!.From.Message;
      var reset = await session.EditUserMessageAsync(new(Guid.NewGuid(), ancient, "fresh beginning", TestLlmSelection.Default));
      await session.WaitForSettlementAsync(reset.ExecutionId).WaitAsync(TestTimeout);
      Assert.Null(reopened.Compactions.ReadContext(sessionId, reset.HistoryRevision).Previous);
      Assert.Null(await session.ReadCompactionAsync(saved.Id, reset.HistoryRevision));
      Assert.DoesNotContain(client.Requests[^1], message => message.Text?.Contains(Summary, StringComparison.Ordinal) == true);
    }
  }

  [Fact]
  public async Task StopDuringBlockedSummaryLeavesNoBoundaryAndJoinsSummaryCleanup() {
    using var database = new TemporaryDatabase();
    var entered = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    using var client = new CompactionChatClient(async (_, cancellation) => {
      entered.TrySetResult();
      try {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        return SummaryResponse();
      } finally {
        cleanupEntered.TrySetResult();
        await cleanupRelease.Task;
      }
    });
    using UrdStore store = UrdStore.Open(database.Path);
    PrimarySession session = CreateSession(store, client);
    await SubmitTurnAsync(session, "ancient-turn: " + new string('x', 100_000));
    await SubmitTurnAsync(session, "latest-turn");
    long revision = (await session.ReadSnapshotAsync()).HistoryRevision;
    CompactionReceipt accepted = Assert.IsType<CompactionReceipt>(await session.CompactAsync(
        new(Guid.NewGuid(), revision, Selection)));
    Task<SessionExecutionResult> settlement = session.WaitForSettlementAsync(accepted.ExecutionId);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      await session.RequestStopAsync(accepted.ExecutionId).WaitAsync(TestTimeout);
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.False(settlement.IsCompleted);
      Assert.Null(store.Compactions.ReadContext(session.Id, revision).Previous);
      Assert.DoesNotContain((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision))).Entries,
          entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
    } finally {
      cleanupRelease.TrySetResult();
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(ExecutionOutcome.Cancelled, (await settlement).Outcome);
    Assert.Null(store.Compactions.ReadContext(session.Id, revision).Previous);
  }

  [Fact]
  public async Task SummaryFailurePreservesPreviousContextAndDoesNotAddMarker() {
    using var database = new TemporaryDatabase();
    using var client = new CompactionChatClient((call, _) => call == 2
        ? Task.FromException<ChatResponse>(new InvalidOperationException("scripted summary failure"))
        : Task.FromResult(SummaryResponse()));
    using UrdStore store = UrdStore.Open(database.Path);
    PrimarySession session = CreateSession(store, client);
    await SubmitTurnAsync(session, "ancient-turn: " + new string('x', 100_000));
    await SubmitTurnAsync(session, "first-latest-turn");
    long revision = (await session.ReadSnapshotAsync()).HistoryRevision;
    CompactionReceipt first = Assert.IsType<CompactionReceipt>(await session.CompactAsync(
        new(Guid.NewGuid(), revision, Selection)));
    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForSettlementAsync(first.ExecutionId).WaitAsync(TestTimeout)).Outcome);
    await SubmitTurnAsync(session, "new-large-turn: " + new string('y', 100_000));
    await SubmitTurnAsync(session, "second-latest-turn");
    CompactionContext before = store.Compactions.ReadContext(session.Id, revision);
    CompactionReceipt failed = Assert.IsType<CompactionReceipt>(await session.CompactAsync(
        new(Guid.NewGuid(), revision, Selection)));
    Assert.Equal(ExecutionOutcome.Failed,
        (await session.WaitForSettlementAsync(failed.ExecutionId).WaitAsync(TestTimeout)).Outcome);

    CompactionContext after = store.Compactions.ReadContext(session.Id, revision);
    Assert.Equal(before.Previous!.Id, after.Previous!.Id);
    Assert.Equal(before.Entries.Select(Position), after.Entries.Select(Position));
    Assert.Single((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision))).Entries,
        entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
    Assert.Equal(0, client.SummaryToolCounts[0]);
    Assert.Equal(0, client.SummaryToolCounts[1]);
  }

  private static (Guid Request, int Index, string? Text) Position(ContextEntry entry) =>
      (entry.RequestId, entry.MessageIndex, entry.Message.Text);

  private static PrimarySession CreateSession(UrdStore store, CompactionChatClient client) =>
      new(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);

  private static async Task SubmitTurnAsync(PrimarySession session, string text) {
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
        new(Guid.NewGuid(), 0, text, TestLlmSelection.Default));
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForSettlementAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
  }

  private static ChatResponse SummaryResponse() => new(new ChatMessage(ChatRole.Assistant, Summary)) {
    FinishReason = ChatFinishReason.Stop
  };

  private sealed class CompactionChatClient(Func<int, CancellationToken, Task<ChatResponse>> summarize) : IChatClient {
    private readonly ConcurrentQueue<IReadOnlyList<ChatMessage>> requests = new();
    private readonly ConcurrentQueue<int> summaryToolCounts = new();
    private int summaryCalls;
    internal IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => requests.ToArray();
    internal int[] SummaryToolCounts => summaryToolCounts.ToArray();

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) {
      requests.Enqueue(messages.ToArray());
      return Complete(cancellationToken);
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref summaryCalls);
      summaryToolCounts.Enqueue(options?.Tools?.Count ?? -1);
      return summarize(summaryCalls, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
  }
}
