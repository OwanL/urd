using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Storage;
using Urd.Core.Usage;
using Xunit;

namespace Urd.Backend.Tests.Usage;

public sealed class PromptCacheStorageTests {
  private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

  [Fact]
  public void SumsUnequalRequestsBeyondTheRecentLedgerLimit() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid priorTurn = StartTurn(store, session);
    FinishTurn(store, session, priorTurn);
    Guid lastTurn = StartTurn(store, session);
    FinishTurn(store, session, lastTurn);

    store.Usage.Begin(Request(session, priorTurn, 0, new(100, 1, 20)));
    store.Usage.Begin(Request(session, lastTurn, 1, new(9000, 1, 8000)));
    for (int index = 2; index <= 21; index++)
      store.Usage.Begin(Request(session, lastTurn, index, new(50, 1, 0)));

    Assert.Equal(20, store.Usage.Read(session, null, null).Recent.Count);
    PromptCacheSummary summary = store.Usage.ReadPromptCache(session);
    Assert.Equal(new PromptCacheTotals(10_000, 8_000, 21, 21), summary.LastTurn);
    Assert.Equal(new PromptCacheTotals(10_100, 8_020, 22, 22), summary.Session);
  }

  [Fact]
  public void ExcludesOtherWorkAndUnfinishedRequestsAndCountsUnreportedConversationAttempts() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid otherSession = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid turn = StartTurn(store, session);
    FinishTurn(store, session, turn);

    // A zero hit is a measurement, even when the request cannot be priced.
    store.Usage.Begin(Request(session, turn, 0, new(100, 2, 0)) with { Problem = "Unknown rates." });
    store.Usage.Begin(Request(session, turn, 1, new(900, 2, 800)) with { IsEstimate = true });
    store.Usage.Begin(Request(session, turn, 2, new(900, 2, null)));
    store.Usage.Begin(Request(session, turn, 3, new(null, 2, 800)));
    store.Usage.Begin(Request(session, turn, 4, new(0, 2, 0)));
    store.Usage.Begin(Request(session, turn, 5, new(900, 2, -1)));
    store.Usage.Begin(Request(session, turn, 6, new(900, 2, 901)));
    store.Usage.Begin(Request(session, turn, 7, null));
    store.Usage.Begin(Request(session, turn, 8, new(900, 2, 800)) with { FinishedAt = null });
    foreach (string purpose in new[] { "compaction", "title", "pruning.tools", "background", "model", "provider test" })
      store.Usage.Begin(Request(session, turn, 9, new(900, 2, 800)) with { Purpose = purpose });
    store.Usage.Begin(Request(otherSession, Guid.NewGuid(), 10, new(900, 2, 800)));
    store.Usage.Begin(Request(null, null, 11, new(900, 2, 800)));

    PromptCacheSummary summary = store.Usage.ReadPromptCache(session);
    Assert.Equal(new PromptCacheTotals(100, 0, 1, 8), summary.LastTurn);
    Assert.Equal(summary.LastTurn, summary.Session);
  }

  [Fact]
  public void SelectsTheLatestSuccessfulConversationIncludingContinuation() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid first = StartTurn(store, session);
    FinishTurn(store, session, first);
    Guid continuation = Guid.NewGuid();
    Assert.NotNull(store.Continuations.Start(session, 0,
        new(continuation, 0, first, TestLlmSelection.Default), accepting: true));
    FinishTurn(store, session, continuation);
    Guid failed = StartTurn(store, session);
    Assert.Equal(ExecutionOutcome.Failed, store.Executions.TryFinish(session, failed, ExecutionOutcome.Failed)?.Outcome);
    Guid cancelled = StartTurn(store, session);
    Assert.Equal(ExecutionOutcome.Cancelled,
        store.Executions.TryFinish(session, cancelled, ExecutionOutcome.Cancelled)?.Outcome);
    Guid compaction = Guid.NewGuid();
    Assert.NotNull(store.Compactions.Start(session, new(compaction, 0, TestLlmSelection.Default), accepting: true));
    Assert.Equal(ExecutionOutcome.Completed,
        store.Executions.TryFinish(session, compaction, ExecutionOutcome.Completed)?.Outcome);
    Guid active = StartTurn(store, session);

    store.Usage.Begin(Request(session, continuation, 0, new(200, 2, 80)));
    store.Usage.Begin(Request(session, first, 1, new(100, 2, 20)));
    store.Usage.Begin(Request(session, failed, 2, new(300, 2, 100)));
    store.Usage.Begin(Request(session, cancelled, 3, new(400, 2, 200)));
    store.Usage.Begin(Request(session, compaction, 4, new(900, 2, 800)) with { Purpose = "compaction" });
    store.Usage.Begin(Request(session, active, 5, new(500, 2, 300)));
    store.Usage.Begin(Request(session, active, 6, new(900, 2, 800)) with { FinishedAt = null });

    PromptCacheSummary summary = store.Usage.ReadPromptCache(session);
    Assert.Equal(new PromptCacheTotals(200, 80, 1, 1), summary.LastTurn);
    Assert.Equal(new PromptCacheTotals(1_500, 700, 5, 5), summary.Session);
  }

  [Fact]
  public void DistinguishesNoCompletedTurnFromALatestTurnWithoutMeasurements() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid first = StartTurn(store, session);
    Assert.Equal(new PromptCacheSummary(null, new(0, 0, 0, 0)), store.Usage.ReadPromptCache(session));

    store.Usage.Begin(Request(session, first, 0, new(100, 2, 80)));
    FinishTurn(store, session, first);
    Guid last = StartTurn(store, session);
    store.Usage.Begin(Request(session, last, 1, new(100, 2, null)));
    FinishTurn(store, session, last);

    PromptCacheSummary summary = store.Usage.ReadPromptCache(session);
    Assert.Equal(new PromptCacheTotals(0, 0, 0, 1), summary.LastTurn);
    Assert.Equal(new PromptCacheTotals(100, 80, 1, 2), summary.Session);
  }

  private static Guid StartTurn(UrdStore store, Guid session) {
    Guid execution = Guid.NewGuid();
    var admitted = store.UserMessages.Submit(session, 0,
        new(Guid.NewGuid(), 0, "input", TestLlmSelection.Default), accepting: true, execution);
    Assert.Equal(execution, admitted.Receipt.ExecutionId);
    return execution;
  }

  private static void FinishTurn(UrdStore store, Guid session, Guid execution) {
    var identity = new ExecutionIdentity(session, execution, 0);
    Guid request = Assert.IsType<LlmRequest>(store.Executions.CreateNextRequest(identity)).RequestId;
    Assert.True(store.Executions.StartRequest(identity, request));
    Assert.True(store.LlmResponses.Append(identity, new(request, Guid.NewGuid(), 0, "reply", true)));
    Assert.True(store.LlmResponses.CompleteRequest(identity, new(request, ExecutionOutcome.Completed)));
    Assert.Equal(ExecutionOutcome.Completed,
        store.Executions.TryFinish(session, execution, ExecutionOutcome.Completed)?.Outcome);
  }

  private static UsageRequest Request(Guid? session, Guid? execution, int second, UsageTokens? tokens) {
    DateTimeOffset started = Start.AddSeconds(second);
    return new(Guid.NewGuid(), session, "provider", "model", "conversation", started, started.AddSeconds(1),
        null, false, null, tokens, null, Guid.NewGuid(), execution);
  }
}
