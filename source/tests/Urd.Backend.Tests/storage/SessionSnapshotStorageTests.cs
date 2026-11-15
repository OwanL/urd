using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class SessionSnapshotStorageTests {
  [Fact]
  public void LargeHistoriesReadOnlyTheIndexedFirstUserPreview() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid other = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    SeedHistory(database, session, 0, 3000);
    database.Execute($"UPDATE sessions SET history_revision = 1 WHERE session_id = '{session:D}';");
    SeedHistory(database, session, 1, 3000);
    SeedHistory(database, other, 0, 3000);

    // Omitted bodies are invalid UTF-8; selecting all history before trimming would fail.
    database.Execute($"""
      UPDATE input_decisions SET original_text_utf8 = X'FF'
      WHERE session_id = '{session:D}' AND input_id <> (
          SELECT message_id FROM execution_messages WHERE session_id = '{session:D}'
              AND history_revision = 1 AND role = 0 ORDER BY ordinal LIMIT 1);
      UPDATE execution_output SET text_utf8 = X'FF' WHERE request_id IN (
          SELECT q.request_id FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
          WHERE r.session_id = '{session:D}');
      """);

    SessionSnapshot snapshot = store.Snapshots.ReadPersisted(session);
    Assert.Equal("input 1", snapshot.FirstUserPreview);
    Assert.Empty(snapshot.PendingInputs);
    Assert.NotNull(snapshot.LastSettlement);

    string[] plan = database.ExplainQueryPlan(SnapshotReader.FirstUserSql, ("$session", session), ("$revision", 1L));
    Assert.Contains(plan, step => step.Contains("execution_messages_history_order", StringComparison.Ordinal));
    Assert.DoesNotContain(plan, step => step.Contains("SCAN execution_messages", StringComparison.Ordinal));
    string[] pending = database.ExplainQueryPlan(SnapshotReader.PendingInputsSql,
        ("$session", session), ("$limit", 8), ("$bytes", 256 * 3 + 3));
    Assert.Contains(pending, step => step.Contains("nonterminal_execution_inputs", StringComparison.Ordinal));
    Assert.DoesNotContain(pending, step => step.Contains("SCAN execution_inputs", StringComparison.Ordinal));
    string[] settlement = database.ExplainQueryPlan(SnapshotReader.SnapshotSettlementSql,
        ("$session", session), ("$revision", 1L));
    Assert.Contains(settlement, step => step.Contains("USING INDEX execution_runs_terminal_order", StringComparison.Ordinal));
    Assert.DoesNotContain(settlement, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    string[] active = database.ExplainQueryPlan(SnapshotReader.SnapshotUnsettledSql,
        ("$session", session), ("$revision", 1L));
    Assert.Contains(active, step => step.Contains("USING INDEX one_active_execution", StringComparison.Ordinal));
    Assert.DoesNotContain(active, step => step.Contains("SCAN", StringComparison.Ordinal));
  }

  [Fact]
  public void SnapshotContainsOnlyBoundedIdentityAndPreviewUntilAttemptStarts() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    string text = string.Concat(Enumerable.Repeat("pending 🙂", 4000));
    Guid input = Guid.NewGuid(), run = Guid.NewGuid();
    var identity = new ExecutionIdentity(session, run, 0);
    store.UserMessages.Submit(session, 0,
        new(input, 0, text, TestLlmSelection.Default), true, run);
    Assert.Equal(0L, database.Scalar($"SELECT COUNT(*) FROM execution_messages WHERE session_id = '{session:D}' AND message_id = '{input:D}';")); // raw-sql: admission does not place a user entry before the request starts

    SessionSnapshot snapshot = store.Snapshots.ReadPersisted(session);
    Assert.Null(snapshot.FirstUserPreview);
    PendingInputPreview pending = Assert.Single(snapshot.PendingInputs);
    Assert.Equal(input, pending.SubmissionId);
    Assert.Equal(text[..256], pending.Preview);
    Guid request = store.Executions.CreateNextRequest(identity)!.RequestId;
    Assert.Equal(pending, Assert.Single(store.Snapshots.ReadPersisted(session).PendingInputs));
    Assert.Equal(text, store.UserMessages.ReadReceipt(session, input)!.Submission.Text);
    Assert.True(store.Executions.StartRequest(identity, request));
    Assert.Empty(store.Snapshots.ReadPersisted(session).PendingInputs);
  }

  [Fact]
  public void FailedSettlementCarriesBoundedErrorsThroughSnapshotsAndReopen() {
    using var database = new TemporaryDatabase();
    Guid session;
    SessionExecutionResult failedRequestSettlement;
    SessionExecutionResult failedRunSettlement;
    string longError = new('x', 2048);
    using (UrdStore store = UrdStore.Open(database.Path)) {
      session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      Guid failedRequestExecution = Admit(store, session);
      var failedRequestIdentity = new ExecutionIdentity(session, failedRequestExecution, 0);
      Guid request = store.Executions.CreateNextRequest(failedRequestIdentity)!.RequestId;
      Assert.True(store.LlmResponses.CompleteRequest(failedRequestIdentity,
          new(request, ExecutionOutcome.Failed, longError)));
      store.Compactions.RecordError(session, failedRequestExecution, "run-level fallback");
      failedRequestSettlement = store.Executions.TryFinish(session, failedRequestExecution, ExecutionOutcome.Failed)!;
      Assert.Equal(longError[..1024], failedRequestSettlement.Error);
      Assert.Equal(1024, failedRequestSettlement.Error!.Length);
      Assert.Equal(failedRequestSettlement, store.Executions.ReadSettlement(session, failedRequestExecution));
      Assert.Equal(failedRequestSettlement, store.Snapshots.ReadPersisted(session).LastSettlement);

      Guid failedRunExecution = Admit(store, session);
      store.Compactions.RecordError(session, failedRunExecution, "saved run error");
      failedRunSettlement = store.Executions.TryFinish(session, failedRunExecution, ExecutionOutcome.Failed)!;
      Assert.Equal("saved run error", failedRunSettlement.Error);
      Assert.Equal(failedRunSettlement, store.Snapshots.ReadPersisted(session).LastSettlement);
    }

    using (UrdStore reopened = UrdStore.Open(database.Path)) {
      Assert.Equal(failedRequestSettlement, reopened.Executions.ReadSettlement(session,
          failedRequestSettlement.ExecutionId));
      Assert.Equal(failedRunSettlement, reopened.Snapshots.ReadPersisted(session).LastSettlement);
      Assert.Equal(failedRunSettlement, reopened.Executions.ReadSettlement(session,
          failedRunSettlement.ExecutionId));

      Guid cancelledExecution = Admit(reopened, session);
      reopened.Compactions.RecordError(session, cancelledExecution, "must not be exposed");
      SessionExecutionResult cancelled = reopened.Executions.TryFinish(session, cancelledExecution,
          ExecutionOutcome.Cancelled)!;
      Assert.Null(cancelled.Error);
      Assert.Null(reopened.Snapshots.ReadPersisted(session).LastSettlement!.Error);
    }
  }

  [Fact]
  public void ContextUsageUsesReportedInputAndOutputCounts() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    RecordAttempt(store, session, new UsageDetails {
      InputTokenCount = 14, OutputTokenCount = 4, TotalTokenCount = 999
    });

    Assert.Equal(new SessionContextUsage(18, IsEstimate: false), store.Snapshots.ReadPersisted(session).ContextUsage);

    string[] plan = database.ExplainQueryPlan(SnapshotReader.ContextUsageSql, ("$session", session), ("$revision", 0L));
    Assert.Contains(plan, step => step.Contains("execution_runs_history_order", StringComparison.Ordinal));
    Assert.Contains(plan, step => step.Contains("execution_requests", StringComparison.Ordinal)
        && step.Contains("INDEX", StringComparison.Ordinal));
    Assert.DoesNotContain(plan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
  }

  [Fact]
  public void MissingLatestAttemptUsageDoesNotFallBackToEarlierUsage() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    RecordAttempt(store, session, new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 });
    RecordAttempt(store, session, usage: null);

    Assert.Null(store.Snapshots.ReadPersisted(session).ContextUsage);
  }

  [Theory]
  [InlineData(-1, 3)]
  [InlineData(3, -1)]
  public void NegativeProviderUsageCountsRemainUnknown(int input, int output) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    RecordAttempt(store, session, new UsageDetails { InputTokenCount = input, OutputTokenCount = output });

    Assert.Null(store.Snapshots.ReadPersisted(session).ContextUsage);
  }

  [Fact]
  public void ContextUsageFromPriorHistoryRevisionIsUnknown() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    RecordAttempt(store, session, new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 });
    database.ForceHistoryRevision(1);

    Assert.Null(store.Snapshots.ReadPersisted(session).ContextUsage);
  }

  [Fact]
  public void NewCompactionProvidesEstimateUntilAMatchingRequestIsAttempted() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    (Guid execution, Guid request) = RecordAttempt(store, session,
        new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 });
    AddCompaction(database, session, execution, request, tokensBefore: 1000, tokensAfter: 400);

    Assert.Equal(new SessionContextUsage(400, IsEstimate: true), store.Snapshots.ReadPersisted(session).ContextUsage);
    RecordAttempt(store, session, new UsageDetails { InputTokenCount = 22, OutputTokenCount = 3 });
    Assert.Equal(new SessionContextUsage(25, IsEstimate: false), store.Snapshots.ReadPersisted(session).ContextUsage);
    RecordAttempt(store, session, usage: null);
    Assert.Null(store.Snapshots.ReadPersisted(session).ContextUsage);
  }

  private static (Guid Execution, Guid Request) RecordAttempt(UrdStore store, Guid session, UsageDetails? usage) {
    Guid execution = Admit(store, session);
    var identity = new ExecutionIdentity(session, execution, 0);
    LlmRequest request = store.Executions.CreateNextRequest(identity)!;
    Assert.True(store.Executions.StartRequest(identity, request.RequestId));
    Assert.True(store.LlmResponses.CompleteRequest(identity,
        new(request.RequestId, ExecutionOutcome.Failed, "request failed", Usage: usage)));
    Assert.Equal(ExecutionOutcome.Failed, store.Executions.TryFinish(session, execution, ExecutionOutcome.Failed)!.Outcome);
    return (execution, request.RequestId);
  }

  private static void AddCompaction(TemporaryDatabase database, Guid session, Guid execution, Guid request,
      long tokensBefore, long tokensAfter) {
    Guid compactionId = Guid.NewGuid();
    long runOrdinal = (long)database.Scalar($"SELECT ordinal FROM execution_runs WHERE run_id = '{execution:D}';")!;
    DateTimeOffset created = DateTimeOffset.UtcNow;
    database.Execute($"""
      INSERT INTO session_compactions (compaction_id, session_id, history_revision, run_id,
          first_kept_request_id, first_kept_message_index, first_kept_run_ordinal, first_kept_request_index,
          reason, tokens_before, tokens_after, created_at, summary)
      VALUES ('{compactionId:D}', '{session:D}', 0, '{execution:D}', '{request:D}', 0, {runOrdinal}, 0,
          {(int)CompactionReason.Manual}, {tokensBefore}, {tokensAfter}, '{created:O}', 'fixture summary');
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, created_at)
      VALUES ('{session:D}', 0, '{execution:D}', 4, '{compactionId:D}', '{created:O}');
      """);
  }

  private static Guid Admit(UrdStore store, Guid session) {
    Guid execution = Guid.NewGuid();
    store.UserMessages.Submit(session, 0,
        new(Guid.NewGuid(), 0, "failure", TestLlmSelection.Default), accepting: true,
        proposedExecutionId: execution);
    return execution;
  }

  // Bulk fixture writes avoid the execution context builder, which deliberately reads full history.
  private static void SeedHistory(TemporaryDatabase database, Guid session, long revision, int count) {
    string submissionPrefix = Guid.NewGuid().ToString("D")[..24];
    string executionPrefix = Guid.NewGuid().ToString("D")[..24];
    string requestPrefix = Guid.NewGuid().ToString("D")[..24];
    string outputPrefix = Guid.NewGuid().ToString("D")[..24];
    string selection = JsonSerializer.Serialize(TestLlmSelection.Default);
    database.Execute($"""
      BEGIN;
      WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x + 1 FROM n WHERE x < {count})
      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason,
              llm_selection_json)
      SELECT '{session:D}', '{submissionPrefix}' || printf('%012d', x), {revision}, CAST('input ' || x AS BLOB),
          1, '{executionPrefix}' || printf('%012d', x), NULL, '{selection}' FROM n;
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
      SELECT session_id, run_id, expected_history_revision, input_id, 0 FROM input_decisions
      WHERE session_id = '{session:D}' AND input_id LIKE '{submissionPrefix}%' ORDER BY input_id;
      INSERT INTO execution_requests (request_id, run_id, response_id, next_chunk_index, response_final, outcome)
      SELECT '{requestPrefix}' || substr(input_id, 25), run_id, '{outputPrefix}' || substr(input_id, 25), 1, 1, 0
      FROM execution_runs WHERE session_id = '{session:D}' AND input_id LIKE '{submissionPrefix}%';
      INSERT INTO execution_output
      SELECT request_id, 0, CAST('output ' || CAST(substr(request_id, 25) AS INTEGER) AS BLOB), 1
      FROM execution_requests WHERE request_id LIKE '{requestPrefix}%';
      INSERT INTO execution_inputs (session_id, input_id, run_id, request_id, outcome)
      SELECT r.session_id, r.input_id, r.run_id, q.request_id, 0
      FROM execution_runs r LEFT JOIN execution_requests q ON q.run_id = r.run_id
      WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{submissionPrefix}%' ORDER BY r.ordinal;
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id, request_id)
      SELECT session_id, history_revision, run_id, role, message_id, input_id, request_id FROM (
          SELECT r.ordinal AS run_ordinal, r.session_id, r.history_revision, r.run_id,
              0 AS role, r.input_id AS message_id, r.input_id, q.request_id
          FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
          WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{submissionPrefix}%'
          UNION ALL
          SELECT r.ordinal, r.session_id, r.history_revision, r.run_id, 1, q.response_id, NULL, q.request_id
          FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
          WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{submissionPrefix}%'
      ) ORDER BY run_ordinal, role;
      COMMIT;
      """);
  }
}
