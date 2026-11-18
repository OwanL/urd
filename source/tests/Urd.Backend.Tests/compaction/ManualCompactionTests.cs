using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
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
    using var client = new ScriptedChatClient(Complete, _ => Task.FromResult(SummaryResponse()));
    Guid sessionId;
    long revision;
    CompactionSummary saved;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      PrimarySession session = CreateSession(store, client);
      sessionId = session.Id;
      await SubmitTurnAsync(session, "ancient-turn: " + new string('x', 100_000));
      await SubmitTurnAsync(session, "latest-turn: keep this decision");
      revision = (await session.ReadSnapshotAsync()).HistoryRevision;
      var submission = new ManualCompactionRequest(Guid.NewGuid(), revision, Selection);

      CompactionReceipt accepted = Assert.IsType<CompactionReceipt>(await session.CompactAsync(submission));
      CompactionReceipt retry = Assert.IsType<CompactionReceipt>(await session.CompactAsync(submission));
      Assert.Equal(accepted.ExecutionId, retry.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(accepted.ExecutionId).WaitAsync(TestTimeout)).Outcome);
      CompactionReceipt settled = Assert.IsType<CompactionReceipt>(await session.ReadCompactionReceiptAsync(submission.OperationId));
      Assert.Equal(accepted.ExecutionId, settled.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed, settled.Outcome);
      SavedHistoryCompaction boundary = Assert.IsType<SavedHistoryCompaction>(store.Compactions.ReadContext(session.Id, revision).PreviousCompaction);
      saved = Assert.IsType<CompactionSummary>(await session.ReadCompactionAsync(boundary.Id, revision));
      Assert.Equal(Summary, saved.Summary);
      Assert.Equal(3, client.Calls);
      Assert.Empty(client.Options[2]!.Tools!);

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
      await session.WaitForExecutionFinishAsync(edited.ExecutionId).WaitAsync(TestTimeout);
      Assert.Equal(saved.Id, reopened.Compactions.ReadContext(sessionId, edited.HistoryRevision).PreviousCompaction!.Id);
      Assert.Equal(saved, await session.ReadCompactionAsync(saved.Id, edited.HistoryRevision));

      history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, edited.HistoryRevision));
      MessageRef ancient = history.Entries.First(entry => entry.Entry.Kind == TranscriptEntryKind.User).Text!.From.Message;
      var reset = await session.EditUserMessageAsync(new(Guid.NewGuid(), ancient, "fresh beginning", TestLlmSelection.Default));
      await session.WaitForExecutionFinishAsync(reset.ExecutionId).WaitAsync(TestTimeout);
      Assert.Null(reopened.Compactions.ReadContext(sessionId, reset.HistoryRevision).PreviousCompaction);
      Assert.Null(await session.ReadCompactionAsync(saved.Id, reset.HistoryRevision));
      Assert.DoesNotContain(client.Requests[^1], message => message.Text?.Contains(Summary, StringComparison.Ordinal) == true);
    }
  }

  [Fact]
  public async Task OversizedLatestToolResultKeepsWholeTurnAndCompactsOlderHistory() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    string toolResult = new('r', 100_000);
    var tool = new TestAgentTool("lookup", (_, _, _) => Task.FromResult(new ToolOutput(toolResult, [])));
    int streamCalls = 0;
    int summaryCalls = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref streamCalls) switch {
      1 => Complete(token),
      2 => ToolBatch(token, new FunctionCallContent("lookup-call", "lookup", new Dictionary<string, object?>())),
      3 => FailContinuation(token),
      _ => throw new InvalidOperationException("Unexpected model request.")
    }, _ => {
      Interlocked.Increment(ref summaryCalls);
      return Task.FromResult(SummaryResponse());
    });
    var manager = new SessionManager(store, _ => client, [tool], Path.GetDirectoryName(database.Path)!);
    try {
      PrimarySession session = await manager.CreatePrimaryAsync();
      await SubmitTurnAsync(session, "older-turn: " + new string('x', 10_000));
      UserMessageReceipt latest = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "latest-turn", Selection));
      Assert.Equal(ExecutionOutcome.Failed,
          (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(latest.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
      long revision = (await session.ReadSnapshotAsync()).HistoryRevision;
      ModelHistory before = store.Compactions.ReadContext(session.Id, revision);
      HistoryMessage latestStart = Assert.Single(before.Messages, message => message.Message.Text == "latest-turn");

      CompactionReceipt receipt = Assert.IsType<CompactionReceipt>(await session.CompactAsync(
          new(Guid.NewGuid(), revision, Selection)));
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(receipt.ExecutionId).WaitAsync(TestTimeout)).Outcome);

      ModelHistory after = store.Compactions.ReadContext(session.Id, revision);
      SavedHistoryCompaction saved = Assert.IsType<SavedHistoryCompaction>(after.PreviousCompaction);
      Assert.Equal(latestStart.Location, saved.FirstRetainedMessageLocation);
      Assert.Equal(Summary, Assert.IsType<CompactionSummary>(await session.ReadCompactionAsync(saved.Id, revision)).Summary);
      Assert.Single((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision))).Entries,
          entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
      Assert.DoesNotContain(after.Messages, message => message.Message.Text?.Contains("older-turn:") == true);
      Assert.Equal(before.Messages.SkipWhile(message => message.Location != latestStart.Location).Select(Position),
          after.Messages.Select(Position));
      Assert.Contains(after.Messages, message => message.Message.Role == ChatRole.Assistant &&
          message.Message.Contents.OfType<FunctionCallContent>().Any(call => call.CallId == "lookup-call"));
      Assert.Contains(after.Messages, message => message.Message.Role == ChatRole.Tool &&
          message.Message.Contents.OfType<FunctionResultContent>().Any(result => result.CallId == "lookup-call" &&
              Equals(result.Result, toolResult)));
      Assert.Equal(1, summaryCalls);
    } finally {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  private static IAsyncEnumerable<ChatResponseUpdate> FailContinuation(CancellationToken token) {
    token.ThrowIfCancellationRequested();
    throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
  }

  [Fact]
  public async Task StopDuringBlockedSummaryLeavesNoBoundaryAndJoinsSummaryCleanup() {
    using var database = new TemporaryDatabase();
    var entered = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    using var client = new ScriptedChatClient(Complete, async cancellation => {
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
    Task<SessionExecutionResult> settlement = session.WaitForExecutionFinishAsync(accepted.ExecutionId);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      await session.RequestStopAsync(accepted.ExecutionId).WaitAsync(TestTimeout);
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.False(settlement.IsCompleted);
      Assert.Null(store.Compactions.ReadContext(session.Id, revision).PreviousCompaction);
      Assert.DoesNotContain((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision))).Entries,
          entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
    } finally {
      cleanupRelease.TrySetResult();
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(ExecutionOutcome.Cancelled, (await settlement).Outcome);
    Assert.Null(store.Compactions.ReadContext(session.Id, revision).PreviousCompaction);
  }

  [Fact]
  public async Task SummaryFailurePreservesPreviousContextAndDoesNotAddMarker() {
    using var database = new TemporaryDatabase();
    int summaryCalls = 0;
    using var client = new ScriptedChatClient(Complete, _ => Interlocked.Increment(ref summaryCalls) == 2
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
        (await session.WaitForExecutionFinishAsync(first.ExecutionId).WaitAsync(TestTimeout)).Outcome);
    await SubmitTurnAsync(session, "new-large-turn: " + new string('y', 100_000));
    await SubmitTurnAsync(session, "second-latest-turn");
    ModelHistory before = store.Compactions.ReadContext(session.Id, revision);
    CompactionReceipt failed = Assert.IsType<CompactionReceipt>(await session.CompactAsync(
        new(Guid.NewGuid(), revision, Selection)));
    Assert.Equal(ExecutionOutcome.Failed,
        (await session.WaitForExecutionFinishAsync(failed.ExecutionId).WaitAsync(TestTimeout)).Outcome);

    ModelHistory after = store.Compactions.ReadContext(session.Id, revision);
    Assert.Equal(before.PreviousCompaction!.Id, after.PreviousCompaction!.Id);
    Assert.Equal(before.Messages.Select(Position), after.Messages.Select(Position));
    Assert.Single((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, revision))).Entries,
        entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
    Assert.Equal(2, summaryCalls);
    Assert.Empty(client.Options[2]!.Tools!);
    Assert.Empty(client.Options[5]!.Tools!);
  }

  private static (Guid Request, int Index, string? Text) Position(HistoryMessage entry) =>
      (entry.Location.RequestId, entry.Location.MessageIndexInRequest, entry.Message.Text);

  private static PrimarySession CreateSession(UrdStore store, ScriptedChatClient client) =>
      new(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);

  private static async Task SubmitTurnAsync(PrimarySession session, string text) {
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
        new(Guid.NewGuid(), 0, text, TestLlmSelection.Default));
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
  }

  private static ChatResponse SummaryResponse() => new(new ChatMessage(ChatRole.Assistant, Summary)) {
    FinishReason = ChatFinishReason.Stop
  };
}
