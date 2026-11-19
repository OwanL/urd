using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Context;
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
    int summaryCalls = 0;
    var summaryEntered = Signal();
    var summaryRelease = Signal();
    using var client = CreateClient((call, token) => call switch {
      1 => Complete(token),
      2 => ToolBatch(token, new FunctionCallContent("lookup-call", "lookup", new Dictionary<string, object?>())),
      3 or 4 => Complete(token),
      _ => throw new InvalidOperationException("Unexpected model request.")
    }, async cancellation => {
      Interlocked.Increment(ref summaryCalls);
      summaryEntered.TrySetResult();
      await summaryRelease.Task.WaitAsync(cancellation);
      return SummaryResponse();
    });
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)), store, _ => client,
        new ToolCatalog([tool]));
    await SubmitAsync(session, new string('o', 100_000), TestLlmSelection.Default);
    await SubmitAsync(session, "tool turn", TestLlmSelection.Default);
    Assert.Equal(0, summaryCalls); // Unknown windows disable automatic compaction.

    var compacting = Signal();
    using IDisposable subscription = await session.SubscribeAsync(change => {
      if (change.Status == "compacting") compacting.TrySetResult();
    }, watched: true);
    UserMessageReceipt receipt = await AcceptAsync(session, "current turn", TestLlmSelection.Default with { ContextWindow = 8_192 });
    Task<SessionExecutionResult> settlement = session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId));
    try {
      await summaryEntered.Task.WaitAsync(TestTimeout);
      await compacting.Task.WaitAsync(TestTimeout);
      summaryRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed, (await settlement.WaitAsync(TestTimeout)).Outcome);
    } finally {
      summaryRelease.TrySetResult();
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(1, summaryCalls);
    Assert.Equal(1, toolRuns);
    Assert.Equal(5, client.Requests.Count);
    IReadOnlyList<ChatMessage> request = client.Requests[4];
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
  public async Task ThresholdCompactsOlderHistoryWhenCurrentTurnExceedsTheRetentionTarget() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(Complete, _ => Task.FromResult(SummaryResponse()));
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);
    await SubmitAsync(session, new string('o', 3_000), TestLlmSelection.Default);
    string current = new('n', 2_500);
    await SubmitAsync(session, current, TestLlmSelection.Default with { ContextWindow = 1_000 });

    Assert.Equal(3, client.Requests.Count);
    Assert.Contains(Summary, RequestText(client.Requests[^1]));
    Assert.Contains(client.Requests[^1], message => message.Role == ChatRole.User && message.Text == current);
    Assert.DoesNotContain(new string('o', 128), RequestText(client.Requests[^1]), StringComparison.Ordinal);
    Assert.NotNull(store.Compactions.ReadContext(session.Id, 0).PreviousCompaction);
  }

  [Theory]
  [InlineData(false, true, "The summary reached its output limit")]
  [InlineData(false, false, "The summarization request exceeded the model's context window")]
  [InlineData(true, false, "The summarization request exceeded the model's context window")]
  public async Task SummaryFailureReportsItsCauseWithoutSavingOrRetrying(bool afterOverflow, bool outputLimit, string expectedDetail) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = CreateClient((call, token) => call == 1 ? Complete(token)
        : Fail(ProviderFailure.ContextWindowExceeded, false, token), _ => outputLimit
        ? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Summary)) { FinishReason = ChatFinishReason.Length })
        : Task.FromException<ChatResponse>(new ProviderOperationException(ProviderFailure.ContextWindowExceeded)));
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);
    await SubmitAsync(session, new string('o', 5_000), TestLlmSelection.Default);
    LlmSelection selection = TestLlmSelection.Default with { ContextWindow = afterOverflow ? null : 1_000 };
    UserMessageReceipt receipt = await AcceptAsync(session, "current turn", selection);
    Assert.Equal(ExecutionOutcome.Failed,
        (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);

    string error = Assert.IsType<string>((await session.ReadSnapshotAsync()).LastSettlement?.Error);
    Assert.StartsWith("History compaction failed:", error);
    Assert.Contains(expectedDetail, error, StringComparison.Ordinal);
    Assert.Equal(afterOverflow ? 3 : 2, client.Requests.Count);
    Assert.Null(store.Compactions.ReadContext(session.Id, 0).PreviousCompaction);
    Assert.DoesNotContain((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0))).Entries,
        entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
  }

  [Fact]
  public async Task OverflowWithNoSafeCutExplainsWhyItWasNotRetried() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(token => Fail(ProviderFailure.ContextWindowExceeded, false, token));
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);
    UserMessageReceipt receipt = await AcceptAsync(session, "only turn", TestLlmSelection.Default);
    Assert.Equal(ExecutionOutcome.Failed,
        (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);

    Assert.Contains("No older history can be compacted", (await session.ReadSnapshotAsync()).LastSettlement?.Error,
        StringComparison.Ordinal);
    Assert.Single(client.Requests);
  }

  [Fact]
  public async Task CompactionRetainsStoredBackgroundInputAcrossSnapshotsAndDeliversRacingCompletionOnce() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var preferences = new PreferenceManager(store);
    preferences.SetCompactionTriggerTokenCount(int.MaxValue);
    string payload = string.Concat(Enumerable.Repeat("雪🧭é", 8_500));
    var toolEntered = Signal();
    var toolRelease = Signal();
    var summaryEntered = Signal();
    var summaryRelease = Signal();
    var waiting = Signal();
    var completionSaved = Signal();
    var tool = new TestAgentTool("work", async (toolInput, context, cancellation) => {
      Assert.Equal("retained-call", context.CallId);
      Assert.Equal(payload, toolInput.GetProperty("nested").GetProperty("payload").GetString());
      toolEntered.TrySetResult();
      await toolRelease.Task.WaitAsync(cancellation);
      return new ToolOutput("background result", []);
    });
    var toolArguments = new Dictionary<string, object?> {
      ["background"] = true,
      ["nested"] = new Dictionary<string, object?> { ["payload"] = payload }
    };
    int streamCalls = 0;
    int summaryCalls = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref streamCalls) switch {
      2 => ToolBatch(token, new FunctionCallContent("retained-call", "work", toolArguments)),
      _ => Complete(token)
    }, async cancellation => {
      int call = Interlocked.Increment(ref summaryCalls);
      if (call == 1) {
        preferences.SetCompactionTriggerTokenCount(int.MaxValue);
        return SummaryResponse();
      }
      if (call == 2) {
        summaryEntered.TrySetResult();
        await summaryRelease.Task.WaitAsync(cancellation);
        preferences.SetCompactionTriggerTokenCount(int.MaxValue);
      }
      return SummaryResponse();
    });
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)), store, _ => client,
        new ToolCatalog([tool]));
    await SubmitAsync(session, new string('o', 100_000), TestLlmSelection.Default);
    using IDisposable subscription = await session.SubscribeAsync(change => {
      if (change.Status == "waiting") waiting.TrySetResult();
      if (StoredTools.Read(database).Any(call => call.ResultJson is not null)) completionSaved.TrySetResult();
    }, watched: true);
    UserMessageReceipt launch = await AcceptAsync(session, "launch background work", TestLlmSelection.Default);
    Guid executionId = Assert.IsType<Guid>(launch.ExecutionId);
    try {
      await toolEntered.Task.WaitAsync(TestTimeout);
      await waiting.Task.WaitAsync(TestTimeout);
      StoredTool stored = Assert.Single(StoredTools.Read(database));
      Assert.True(stored.Started);
      Assert.Null(stored.ResultJson);
      Assert.True(stored.ArgumentsJson.Length > 32_768);
      using (JsonDocument input = JsonDocument.Parse(stored.ArgumentsJson))
        Assert.Equal(payload, input.RootElement.GetProperty("nested").GetProperty("payload").GetString());
      Assert.Equal(0, summaryCalls);

      waiting = Signal();
      preferences.SetCompactionTriggerTokenCount(1_000);
      UserMessageReceipt firstSteering = await AcceptAsync(session, "compact while work is running", TestLlmSelection.Default);
      Assert.Equal(executionId, firstSteering.ExecutionId);
      await waiting.Task.WaitAsync(TestTimeout);
      long firstRevision = (await session.ReadSnapshotAsync()).HistoryRevision;
      SavedHistoryCompaction firstBoundary = Assert.IsType<SavedHistoryCompaction>(
          store.Compactions.ReadContext(session.Id, firstRevision).PreviousCompaction);
      string firstSummary = Assert.IsType<CompactionSummary>(
          await session.ReadCompactionAsync(firstBoundary.Id, firstRevision)).Summary;
      Assert.StartsWith(Summary, firstSummary);
      AssertBackgroundCallIncluded(firstSummary, stored);
      ModelHistory firstContext = store.Compactions.ReadContext(session.Id, firstRevision);
      Assert.DoesNotContain(firstContext.Messages.SelectMany(entry => entry.Message.Contents)
          .OfType<FunctionCallContent>(), call => call.CallId == stored.CallId);

      waiting = Signal();
      await AcceptAsync(session, new string('f', 100_000), TestLlmSelection.Default);
      await waiting.Task.WaitAsync(TestTimeout);
      Assert.Equal(1, summaryCalls);

      preferences.SetCompactionTriggerTokenCount(1_000);
      UserMessageReceipt secondSteering = await AcceptAsync(session, "compact after the background result", TestLlmSelection.Default);
      Assert.Equal(executionId, secondSteering.ExecutionId);
      await summaryEntered.Task.WaitAsync(TestTimeout);
      Assert.Equal(2, summaryCalls);
      toolRelease.TrySetResult();
      await completionSaved.Task.WaitAsync(TestTimeout);
      Assert.NotNull(Assert.Single(StoredTools.Read(database)).ResultJson);
      summaryRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout)).Outcome);

      long secondRevision = (await session.ReadSnapshotAsync()).HistoryRevision;
      SavedHistoryCompaction secondBoundary = Assert.IsType<SavedHistoryCompaction>(
          store.Compactions.ReadContext(session.Id, secondRevision).PreviousCompaction);
      Assert.NotEqual(firstBoundary.Id, secondBoundary.Id);
      string secondSummary = Assert.IsType<CompactionSummary>(
          await session.ReadCompactionAsync(secondBoundary.Id, secondRevision)).Summary;
      AssertBackgroundCallIncluded(secondSummary, stored);
      Assert.Equal(2, summaryCalls);

      int[] summaryRequestIndices = client.Options.Select((options, index) => (options, index))
          .Where(item => item.options?.Tools is { Count: 0 }).Select(item => item.index).ToArray();
      Assert.Equal(2, summaryRequestIndices.Length);
      string firstModelRequest = client.Requests.Select((request, index) => (request, index))
          .Where(item => item.index > summaryRequestIndices[0] && item.index < summaryRequestIndices[1]
              && client.Options[item.index]?.Tools is { Count: > 0 })
          .Select(item => RequestText(item.request)).First();
      string[] afterSecondCompaction = client.Requests.Skip(summaryRequestIndices[1] + 1).Select(RequestText).ToArray();
      AssertBackgroundCallIncluded(firstModelRequest, stored);
      Assert.Equal(2, afterSecondCompaction.Length);
      Assert.All(afterSecondCompaction, request => AssertBackgroundCallIncluded(request, stored));
      Assert.DoesNotContain("[TOOL-ORIGIN: background tool completion", afterSecondCompaction[0], StringComparison.Ordinal);
      Assert.Contains("[TOOL-ORIGIN: background tool completion", afterSecondCompaction[1], StringComparison.Ordinal);
      Assert.Contains("background result", afterSecondCompaction[1], StringComparison.Ordinal);
      Assert.Single((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, secondRevision))).Entries,
          entry => entry.Entry.Kind == TranscriptEntryKind.ToolCompletion);
    } finally {
      toolRelease.TrySetResult();
      summaryRelease.TrySetResult();
      try {
        await session.StopAcceptingUserMessagesAsync().WaitAsync(TestTimeout);
      } finally {
        await session.StopExecutionAsync().WaitAsync(TestTimeout);
      }
    }
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
    int summaryCalls = 0;
    using var client = CreateClient((call, token) => call switch {
      1 => ToolBatch(token, new FunctionCallContent("lookup-call", "lookup", new Dictionary<string, object?>())),
      2 or 3 => Complete(token),
      _ => throw new InvalidOperationException("Unexpected model request.")
    }, _ => {
      Interlocked.Increment(ref summaryCalls);
      return Task.FromResult(SummaryResponse());
    });
    var manager = new SessionManager(store, _ => client, [tool], Path.GetDirectoryName(database.Path)!);
    try {
      PrimarySession session = await manager.CreatePrimaryAsync();
      UserMessageReceipt receipt = await AcceptAsync(session, new string('o', 100_000), TestLlmSelection.Default);
      await entered.Task.WaitAsync(TestTimeout);
      Assert.Equal(0, summaryCalls);
      preferences.SetCompactionTriggerTokenCount(1_000);
      release.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);

      Assert.Equal(1, summaryCalls);
      Assert.Equal(1, toolRuns);
      Assert.Equal(3, client.Requests.Count);
      IReadOnlyList<ChatMessage> request = client.Requests[2];
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
      Assert.Equal(4, client.Requests.Count);
      Assert.Equal(1, summaryCalls);
      Assert.Equal(1, toolRuns);
      Assert.Contains(client.Requests[3], message => message.Role == ChatRole.User &&
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
    using var client = CreateClient((call, token) => call == 1
        ? ToolBatch(token, new FunctionCallContent("lookup", "lookup", new Dictionary<string, object?>())) : Complete(token),
        _ => Task.FromResult(SummaryResponse()));
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
      await session.WaitForExecutionFinishAsync(initial.ExecutionId!.Value).WaitAsync(TestTimeout);
      var context = store.Compactions.ReadContext(session.Id, 0);
      Assert.Equal(1, context.PreviousCompaction!.FirstRetainedMessageLocation.RequestIndexInExecution);
      Assert.Equal(1, context.PreviousCompaction.FirstRetainedMessageLocation.MessageIndexInRequest); // The first steering input was summarized.
      var history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
      var target = Assert.Single(history.Entries, entry => entry.Entry.EntryId == removed.SubmissionId).Text!.From.Message;
      Assert.Contains(history.Entries.TakeWhile(entry => entry.Entry.EntryId != removed.SubmissionId),
          entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);

      var edited = await session.EditUserMessageAsync(new(Guid.NewGuid(), target, "new steering", TestLlmSelection.Default));
      await session.WaitForExecutionFinishAsync(edited.ExecutionId).WaitAsync(TestTimeout);
      Assert.Null(store.Compactions.ReadContext(session.Id, edited.HistoryRevision).PreviousCompaction);
      Assert.DoesNotContain(client.Requests[^1], message => message.Text?.Contains(Summary, StringComparison.Ordinal) == true);
      Assert.Equal(new[] { "start", "new steering" }, client.Requests[^1]
          .Where(message => message.Role == ChatRole.User).Select(message => message.Text));
      Assert.DoesNotContain((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, edited.HistoryRevision))).Entries,
          entry => entry.Entry.Kind == TranscriptEntryKind.Compaction);
    } finally {
      release.TrySetResult();
      await session.StopAcceptingUserMessagesAsync();
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
    int summaryCalls = 0;
    using var client = CreateClient((call, token) => call switch {
      1 => ToolBatch(token, new FunctionCallContent("overflow-call", "lookup", new Dictionary<string, object?>())),
      2 => Fail(ProviderFailure.ContextWindowExceeded, false, token),
      3 => Complete(token),
      _ => throw new InvalidOperationException("Unexpected model request.")
    }, _ => {
      Interlocked.Increment(ref summaryCalls);
      return Task.FromResult(SummaryResponse());
    });
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)), store, _ => client,
        new ToolCatalog([tool]));
    UserMessageReceipt receipt = await AcceptAsync(session, new string('u', 100_000),
        TestLlmSelection.Default with { ModelId = "gpt-6.2-sol" });

    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(4, client.Requests.Count);
    Assert.Equal(1, summaryCalls);
    Assert.Equal(1, toolRuns);
    Assert.Single(StoredTools.Read(database));
    long revision = (await session.ReadSnapshotAsync()).HistoryRevision;
    Guid compactionId = store.Compactions.ReadContext(session.Id, revision).PreviousCompaction!.Id;
    Assert.Equal(CompactionReason.Overflow, (await session.ReadCompactionAsync(compactionId, revision))?.Reason);
    Assert.Equal(2, StoredExecutions.ReadRequests(database).Count);
    // Overflow retries the stored request without carrying its live estimate forward.
    object? BeforeOrAfterRetry(int index) => client.Options[index]!.AdditionalProperties![RequestContextEstimate.PropertyName];
    Assert.NotSame(Assert.IsType<RequestContextEstimate>(BeforeOrAfterRetry(1)),
        Assert.IsType<RequestContextEstimate>(BeforeOrAfterRetry(3)));
    Assert.Contains(client.Requests[3], message => message.Contents.OfType<TextContent>()
        .Any(content => content.Text.Contains("[HISTORY SUMMARY:")));
    Assert.Contains(client.Requests[3], message => message.Role == ChatRole.Tool &&
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
    int summaryCalls = 0;
    using var client = CreateClient((call, token) => call switch {
      1 => Complete(token),
      2 => Fail(partialOutput ? ProviderFailure.ContextWindowExceeded : ProviderFailure.ProviderUnavailable,
          partialOutput, token),
      _ => throw new InvalidOperationException("A failed request must not be retried.")
    }, _ => {
      Interlocked.Increment(ref summaryCalls);
      return Task.FromResult(SummaryResponse());
    });
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);
    await SubmitAsync(session, new string('o', 100_000), TestLlmSelection.Default);
    UserMessageReceipt receipt = await AcceptAsync(session, "current", TestLlmSelection.Default);

    Assert.Equal(ExecutionOutcome.Failed,
        (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(2, client.Requests.Count);
    Assert.Equal(0, summaryCalls);
    Assert.Equal(2, StoredExecutions.ReadRequests(database).Count);
    if (partialOutput) {
      TranscriptHistoryPage history = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
      TranscriptEntryDescriptor partial = Assert.Single(history.Entries,
          entry => entry.Entry.ExecutionId == receipt.ExecutionId && entry.Entry.Kind == TranscriptEntryKind.Text);
      Assert.Contains("partial output", (await session.Transcript.ReadContentAsync(partial.Text!.From)).Text);
    }
  }

  private static void AssertBackgroundCallIncluded(string text, StoredTool call) {
    string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
    const string header = "[BACKGROUND TOOL CALLS: inputs retained for result association; not new calls.]";
    Assert.Equal(1, normalized.Split(header, StringSplitOptions.None).Length - 1);
    int appendixStart = normalized.IndexOf(header, StringComparison.Ordinal) + header.Length;
    string appendix = normalized[appendixStart..];
    string metadataLine = appendix.Split('\n').First(line => line.StartsWith('{'));
    using JsonDocument metadata = JsonDocument.Parse(metadataLine);
    Assert.Equal(call.RequestId.ToString(), metadata.RootElement.GetProperty("request_id").GetString());
    Assert.Equal(call.CallId, metadata.RootElement.GetProperty("call_id").GetString());
    Assert.Equal(call.Name, metadata.RootElement.GetProperty("name").GetString());
    string arguments = call.ArgumentsJson.Replace("\r\n", "\n", StringComparison.Ordinal);
    Assert.Equal(1, normalized.Split(arguments, StringSplitOptions.None).Length - 1);
  }

  private static string RequestText(IEnumerable<ChatMessage> request) =>
      string.Join("\n", request.SelectMany(message => message.Contents.OfType<TextContent>())
          .Select(content => content.Text));

  private static async Task SubmitAsync(PrimarySession session, string text, LlmSelection selection) {
    UserMessageReceipt receipt = await AcceptAsync(session, text, selection);
    Assert.Equal(ExecutionOutcome.Completed,
        (await session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);
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

  private static ScriptedChatClient CreateClient(
      Func<int, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> stream,
      Func<CancellationToken, Task<ChatResponse>> summarize) {
    int streamCalls = 0;
    return new(token => stream(Interlocked.Increment(ref streamCalls), token), summarize);
  }

  private static ChatResponse SummaryResponse() => new(new ChatMessage(ChatRole.Assistant, Summary)) {
    FinishReason = ChatFinishReason.Stop
  };
}
