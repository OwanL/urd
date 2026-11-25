using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

// The context stamp a request claims must reach the harness context of every tool call the
// request spawns, so baseline consumers can fail closed when captured context leaves the model.
public sealed class ContextStampExecutionTests {
  [Fact]
  public async Task ToolCallsCarryTheClaimStampAcrossTurnsAndRuns() {
    var tool = new StampCaptureTool();
    int calls = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref calls) switch {
      1 => ToolBatch(token, Probe(tool)),
      2 => Complete(token),
      3 => ToolBatch(token, Probe(tool)),
      _ => Complete(token)
    });
    await using var fixture = new ExecutionFixture(client, tools: [tool]);
    Guid first = await fixture.StartExecutionAsync("go");
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(first).WaitAsync(TestTimeout)).Outcome);
    Guid second = await fixture.StartExecutionAsync("again");
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(second).WaitAsync(TestTimeout)).Outcome);

    // The context state claims a freshly prepared history: no compaction, no continuation omissions.
    ContextStamp claim = new(0, null, 0);
    // Context state holds the baseline stable across turns and executions alike, not the
    // execution identity: both runs claim the same stamp for their calls.
    Assert.Equal(Enumerable.Repeat(claim, 2), tool.Stamps);
  }

  [Fact]
  public async Task ContinuationWithOmittedTailAdvancesTheClaimStampGeneration() {
    var tool = new StampCaptureTool();
    int calls = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref calls) switch {
      1 => Complete(token),
      2 => FailedAfterText(token),
      3 => ToolBatch(token, Probe(tool)),
      _ => Complete(token)
    });
    await using var fixture = new ExecutionFixture(client, tools: [tool]);

    Guid first = await fixture.StartExecutionAsync("first question");
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(first).WaitAsync(TestTimeout)).Outcome);
    var failedInput = new UserMessageSubmission(Guid.NewGuid(), 0, "question that fails", TestLlmSelection.Default);
    UserMessageReceipt failedReceipt = await fixture.Session.SubmitUserMessageAsync(failedInput);
    Assert.Equal(ExecutionOutcome.Failed,
        (await fixture.Session.WaitForExecutionFinishAsync(failedReceipt.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
    SessionSnapshot snapshot = await fixture.Session.ReadSnapshotAsync();
    Assert.True(snapshot.CanContinue);

    ContinuationReceipt receipt = Assert.IsType<ContinuationReceipt>(await fixture.Session.ContinueAsync(
        new(Guid.NewGuid(), snapshot.HistoryRevision, snapshot.LastSettlement!.ExecutionId, TestLlmSelection.Default)));
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(receipt.ExecutionId).WaitAsync(TestTimeout)).Outcome);

    // The continuation omitted its failed assistant tail exactly once; its claims agree in stamp.
    Assert.Equal(new ContextStamp(snapshot.HistoryRevision, null, 1), Assert.Single(tool.Stamps));
  }

  [Fact]
  public async Task OmittingContinuationWithoutToolCallsStillCountsForLaterRuns() {
    var tool = new StampCaptureTool();
    int calls = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref calls) switch {
      1 => Complete(token),
      2 => FailedAfterText(token),
      3 => Complete(token),
      4 => ToolBatch(token, Probe(tool)),
      _ => Complete(token)
    });
    await using var fixture = new ExecutionFixture(client, tools: [tool]);

    Guid first = await fixture.StartExecutionAsync("first question");
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(first).WaitAsync(TestTimeout)).Outcome);
    var failedInput = new UserMessageSubmission(Guid.NewGuid(), 0, "question that fails", TestLlmSelection.Default);
    UserMessageReceipt failedReceipt = await fixture.Session.SubmitUserMessageAsync(failedInput);
    Assert.Equal(ExecutionOutcome.Failed,
        (await fixture.Session.WaitForExecutionFinishAsync(failedReceipt.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
    SessionSnapshot snapshot = await fixture.Session.ReadSnapshotAsync();
    Assert.True(snapshot.CanContinue);

    // The omitting continuation spawns no tool calls, so its omission generation is never observed.
    ContinuationReceipt omitted = Assert.IsType<ContinuationReceipt>(await fixture.Session.ContinueAsync(
        new(Guid.NewGuid(), snapshot.HistoryRevision, snapshot.LastSettlement!.ExecutionId, TestLlmSelection.Default)));
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(omitted.ExecutionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Empty(tool.Stamps);

    ContinuationReceipt observed = Assert.IsType<ContinuationReceipt>(await fixture.Session.ContinueAsync(
        new(Guid.NewGuid(), snapshot.HistoryRevision, omitted.ExecutionId, TestLlmSelection.Default)));
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(observed.ExecutionId).WaitAsync(TestTimeout)).Outcome);

    // The second continuation omitted nothing, so the generation count holds at the missed omission.
    Assert.Equal(new ContextStamp(snapshot.HistoryRevision, null, 1), Assert.Single(tool.Stamps));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ToolCallAfterCompactionCarriesEffectiveBoundary(bool overflow) {
    var tool = new StampCaptureTool();
    int calls = 0;
    int summaryCalls = 0;
    var client = new ScriptedChatClient(token => (Interlocked.Increment(ref calls), overflow) switch {
      (2, true) => Overflow(token),
      (2, false) or (3, true) => ToolBatch(token, Probe(tool)),
      _ => Complete(token)
    }, _ => {
      Interlocked.Increment(ref summaryCalls);
      return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Retained history summary.")) {
        FinishReason = ChatFinishReason.Stop
      });
    });
    await using var fixture = new ExecutionFixture(client, tools: [tool]);
    Guid first = await fixture.StartExecutionAsync(new string('o', 100_000));
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(first).WaitAsync(TestTimeout)).Outcome);

    var selection = TestLlmSelection.Default with { ContextWindow = overflow ? null : 8_192 };
    UserMessageReceipt receipt = await fixture.Session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "current turn", selection));
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(Assert.IsType<Guid>(receipt.ExecutionId)).WaitAsync(TestTimeout)).Outcome);

    long revision = (await fixture.Session.ReadSnapshotAsync()).HistoryRevision;
    var boundary = Assert.IsType<SavedHistoryCompaction>(fixture.Store.Compactions.ReadContext(fixture.Session.Id, revision).PreviousCompaction);
    Assert.Equal(overflow ? CompactionReason.Overflow : CompactionReason.Threshold,
        (await fixture.Session.ReadCompactionAsync(boundary.Id, revision))?.Reason);
    Assert.Equal(1, summaryCalls);
    Assert.Contains(client.Requests[overflow ? 3 : 2], message => message.Contents.OfType<TextContent>()
        .Any(content => content.Text.Contains("Retained history summary.", StringComparison.Ordinal)));
    Assert.Equal(new ContextStamp(revision, boundary.Id, 0), Assert.Single(tool.Stamps));
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> Overflow(
      [EnumeratorCancellation] CancellationToken cancellation) {
    await Task.Yield();
    cancellation.ThrowIfCancellationRequested();
    if (!cancellation.IsCancellationRequested)
      throw new ProviderOperationException(ProviderFailure.ContextWindowExceeded);
    yield break;
  }

  private static FunctionCallContent Probe(StampCaptureTool tool) => new(tool.Name, tool.Name, null);

  private static async IAsyncEnumerable<ChatResponseUpdate> FailedAfterText(
      [EnumeratorCancellation] CancellationToken cancellation, string text = "partial failure tail") {
    yield return Text(text);
    await Task.Yield();
    cancellation.ThrowIfCancellationRequested();
    throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
  }

  private sealed class StampCaptureTool : HarnessTool {
    private readonly Lock gate = new();
    private readonly List<ContextStamp> stamps = [];

    internal IReadOnlyList<ContextStamp> Stamps {
      get {
        lock (gate)
          return stamps.ToArray();
      }
    }

    public override string Name => "stamp_probe";
    public override ToolRelevance RelevantWhen => ToolRelevance.Always;
    public override string Description => "Report nothing.";
    public override JsonElement ParametersSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });

    public override Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context, CancellationToken cancellation) {
      lock (gate)
        stamps.Add(Assert.IsType<ContextStamp>(context.BrowserContextStamp));
      return Task.FromResult(new ToolOutput("ok", []));
    }
  }
}
