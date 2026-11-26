using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Urd.Core.Usage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class SessionClosureStorageTests {
  [Fact]
  public void ClosureDeletesOwnedHistoryAndKeepsOnlyUsageAndSharedFilesAfterReopen() {
    using var database = new TemporaryDatabase();
    Guid primary = Guid.NewGuid(), child = Guid.NewGuid(), nested = Guid.NewGuid(), unrelated = Guid.NewGuid();
    var first = new DateTimeOffset(2026, 11, 4, 15, 30, 0, TimeSpan.Zero);
    DateTimeOffset later = first.AddDays(1);
    string toolDirectory = database.Path + ".tool-images", markdownDirectory = database.Path + ".markdown-images";
    var shared = new DataContent(new byte[] { 1, 2, 3 }, "image/png");
    var unique = new DataContent(new byte[] { 4, 5, 6 }, "image/png");
    Guid unrelatedRequest;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(primary);
      store.Sessions.CreateSubagent(child, primary, "spawn");
      store.Sessions.CreateSubagent(nested, child, "nest");
      store.Sessions.CreatePrimary(unrelated);
      var rootHistory = SaveHistory(store, primary, [shared, unique]);
      SaveHistory(store, child, []);
      SaveHistory(store, nested, []);
      unrelatedRequest = SaveHistory(store, unrelated, [shared]).Request;
      SeedExtraHistory(database, primary, rootHistory.Run, rootHistory.Request, unrelatedRequest, shared, unique);
      RecordUsage(store, primary, first, 0.10m);
      RecordUsage(store, child, later, null, "private failed attempt");
      RecordUsage(store, nested, later, 0.20m);
      RecordUsage(store, unrelated, later, 0.40m);
      RecordUsage(store, null, later, 0.80m);
      Assert.False(store.DeletePrimarySession(child));
      Assert.True(store.DeletePrimarySession(primary));
      Assert.False(store.DeletePrimarySession(primary));
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    Assert.Null(reopened.Sessions.Read(primary));
    Assert.Null(reopened.Sessions.Read(child));
    Assert.Null(reopened.Sessions.Read(nested));
    Assert.Equal(unrelated, Assert.Single(reopened.Sessions.ListPrimaryIds(null).SessionIds));
    Assert.NotNull(reopened.Tools.ReadOutput(unrelated, unrelatedRequest, "tool"));
    Assert.Single(Directory.GetFiles(toolDirectory));
    Assert.Single(Directory.GetFiles(markdownDirectory));
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;")); // raw-sql: closure must leave all foreign keys valid
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_output;")); // raw-sql: only the unrelated session's output remains
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM input_decisions;")); // raw-sql: only the unrelated session's input decision remains
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM auxiliary_tools;")); // raw-sql: closure must cascade into auxiliary tool history
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM session_compactions;")); // raw-sql: closure must delete compaction history despite its reference cycle
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_tool_completions;")); // raw-sql: closure must remove durable tool completion history
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM execution_provider_context;")); // raw-sql: closure must remove provider context
    Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM closed_session_usage;")); // raw-sql: the tree has one compact accounting row
    Assert.Equal(33L, database.Scalar("SELECT input_tokens FROM closed_session_usage;")); // raw-sql: compact accounting preserves tree token totals
    Assert.Equal(1L, database.Scalar("SELECT errors FROM closed_session_usage;")); // raw-sql: compact accounting preserves tree error totals
    Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM usage_requests;")); // raw-sql: only unrelated and unattributed request details remain
    UsageSummary compact = reopened.ReadUsage(primary);
    Assert.Equal(new UsageTotals(0.30m, 3, 1, 1, 0), compact.Totals);
    Assert.Empty(compact.Breakdown);
    Assert.Empty(compact.History);
    Assert.Empty(compact.Recent);
    Assert.Equal(0, reopened.ReadUsage(child).Totals.Requests);
    UsageSummary global = reopened.ReadUsage(null, first, later);
    Assert.Equal(new UsageTotals(1.50m, 5, 1, 1, 0), global.Totals);
    Assert.Equal(global.Totals, Assert.Single(global.Breakdown).Totals);
    Assert.Equal(0.10m, global.History[0].Totals.CostUsd);
    Assert.Equal(1.40m, global.History[1].Totals.CostUsd);
    Assert.Equal(2, global.Recent.Count);
    Assert.Equal(1.40m, reopened.ReadUsage(null, later, later).Totals.CostUsd);

    Assert.True(reopened.DeletePrimarySession(unrelated));
    Assert.Equal(global.Totals, reopened.ReadUsage(null, first, later).Totals);
    Assert.Single(reopened.ReadUsage(null, first, later).Recent); // unattributed
    Assert.Empty(Directory.GetFiles(toolDirectory));
    Assert.Empty(Directory.GetFiles(markdownDirectory));
  }

  [Fact]
  public void ClosedSessionRangeTotalsReadUnavailableWhenOnlyPartOfTheLifetimeIsSelected() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid primary = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var first = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    DateTimeOffset last = first.AddDays(2);
    RecordUsage(store, primary, first, 1m);
    RecordUsage(store, primary, last, 2m);
    Assert.True(store.DeletePrimarySession(primary));

    Assert.Equal(3m, store.ReadUsage(primary).Totals.CostUsd);
    Assert.True(store.ReadUsage(primary).TotalsComplete);
    Assert.Equal(3m, store.ReadUsage(primary, first, last).Totals.CostUsd); // range fully covers the lifetime
    Assert.True(store.ReadUsage(primary, first, last).TotalsComplete);
    Assert.Equal(0m, store.ReadUsage(primary, last.AddDays(1), last.AddDays(2)).Totals.CostUsd);
    Assert.True(store.ReadUsage(primary, last.AddDays(1), last.AddDays(2)).TotalsComplete); // disjoint is a true zero
    Assert.Equal(new UsageTotals(0m, 0, 0, 0, 0), store.ReadUsage(primary, first.AddHours(1), last.AddHours(-1),
        detail: false).Totals);
    UsageSummary partial = store.ReadUsage(primary, first.AddHours(1), last.AddHours(-1));
    Assert.False(partial.TotalsComplete);
  }

  [Fact]
  public void PrimarySessionCostIncludesItsTreeWithoutDoubleCountingRollingTotals() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid primary = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid child = store.Sessions.CreateSubagent(Guid.NewGuid(), primary, "spawn").Id;
    Guid grandchild = store.Sessions.CreateSubagent(Guid.NewGuid(), child, "nest").Id;
    Guid other = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var now = new DateTimeOffset(2026, 11, 12, 15, 30, 0, TimeSpan.Zero);
    RecordUsage(store, primary, now.AddDays(-8), 1m);
    RecordUsage(store, child, now.AddDays(-7), 2m);
    RecordUsage(store, grandchild, now.AddDays(-1), 4m, purpose: "title");
    RecordUsage(store, other, now, 8m);
    RecordUsage(store, null, now, 16m);

    UsageSummary open = store.ReadUsage(primary);
    Assert.Equal(new UsageTotals(7m, 3, 0, 0, 0), open.Totals);
    Assert.Equal(4m, Assert.Single(open.Breakdown, group => group.Purpose == "title").Totals.CostUsd);
    Assert.Equal(7m, open.History.Sum(point => point.Totals.CostUsd));
    Assert.Equal(new Guid?[] { grandchild, child, primary }, open.Recent.Select(request => request.SessionId));
    Assert.Equal(2m, store.ReadUsage(child).Totals.CostUsd);
    Assert.Equal(4m, store.ReadUsage(primary, now.AddDays(-1), now).Totals.CostUsd);
    AssertCosts();
    Assert.True(store.DeletePrimarySession(primary));
    AssertCosts();

    void AssertCosts() {
      Assert.Equal(open.Totals, store.ReadUsage(primary, detail: false).Totals);
      Assert.Equal(new UsageTotals(28m, 3, 0, 0, 0), store.ReadUsage(null, now.AddDays(-1), now).Totals);
      Assert.Equal(new UsageTotals(30m, 4, 0, 0, 0), store.ReadUsage(null, now.AddDays(-7), now, detail: false).Totals);
    }
  }

  [Fact]
  public void UnsettledChildAndFailedCommitCannotEraseHistoryOrAccounting() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid primary = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid child = store.Sessions.CreateSubagent(Guid.NewGuid(), primary, "spawn").Id;
    string images = database.Path + ".tool-images";
    SaveHistory(store, primary, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
    Guid run = Guid.NewGuid();
    Assert.True(store.UserMessages.Submit(child, 0, new(Guid.NewGuid(), 0, "owned input", TestLlmSelection.Default), true, run).NewlyAccepted);
    RecordUsage(store, child, DateTimeOffset.UtcNow, 0.20m);
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    Assert.Throws<OperationCanceledException>(() => store.DeletePrimarySession(primary, canceled.Token));
    Assert.Throws<InvalidOperationException>(() => store.DeletePrimarySession(primary));
    Assert.NotNull(store.Executions.TryFinish(child, run, ExecutionOutcome.Failed));
    database.Execute("""
        CREATE TABLE fail_parent (id INTEGER PRIMARY KEY);
        CREATE TABLE fail_child (parent_id INTEGER REFERENCES fail_parent(id) DEFERRABLE INITIALLY DEFERRED);
        CREATE TRIGGER fail_close AFTER DELETE ON sessions BEGIN INSERT INTO fail_child VALUES (1); END;
        """);
    Assert.Throws<SqliteException>(() => store.DeletePrimarySession(primary));
    Assert.NotNull(store.Sessions.Read(primary));
    Assert.NotNull(store.Sessions.Read(child));
    Assert.Single(store.ReadUsage(child).Recent);
    Assert.Single(Directory.GetFiles(images));
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM closed_session_usage;")); // raw-sql: failed commit must roll back compact session accounting
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM closed_usage_totals;")); // raw-sql: failed commit must roll back global accounting aggregates
    database.Execute("DROP TRIGGER fail_close;");
    Assert.True(store.DeletePrimarySession(primary));
    Assert.Equal(0.20m, store.ReadUsage(primary).Totals.CostUsd);
    Assert.Empty(Directory.GetFiles(images));
  }

  private static void RecordUsage(UrdStore store, Guid? session, DateTimeOffset at, decimal? cost, string? problem = null,
      string purpose = "conversation") =>
      store.Usage.Begin(new(Guid.NewGuid(), session, "provider", "model", purpose, at, at.AddSeconds(1),
          cost, problem is not null, problem, new UsageTokens(11, 22, 0), null, Guid.NewGuid()));

  private static (Guid Run, Guid Request) SaveHistory(UrdStore store, Guid session, DataContent[] images) {
    Guid run = Guid.NewGuid();
    Assert.True(store.UserMessages.Submit(session, 0, new(Guid.NewGuid(), 0, "conversation input", TestLlmSelection.Default), true, run).NewlyAccepted);
    var identity = new ExecutionIdentity(session, run, 0);
    Guid request = store.Executions.CreateNextRequest(identity)!.RequestId;
    Assert.True(store.Executions.StartRequest(identity, request));
    Assert.True(store.LlmResponses.Append(identity, new(request, Guid.NewGuid(), 0, "assistant output", true)));
    var call = new LlmToolCall("tool", "fixture", JsonSerializer.SerializeToElement(new { argument = "secret" }));
    Assert.True(store.LlmResponses.CompleteRequest(identity, new(request, ExecutionOutcome.Completed, ToolCalls: [call])));
    Assert.True(store.Tools.TryStart(identity, request, call.CallId));
    Assert.True(store.Tools.Complete(identity, request, new(call.CallId, ExecutionOutcome.Completed, new ToolOutput("tool output", images))));
    Assert.NotNull(store.Executions.TryFinish(session, run, ExecutionOutcome.Failed));
    return (run, request);
  }

  private static void SeedExtraHistory(TemporaryDatabase database, Guid session, Guid run, Guid request,
      Guid unrelatedRequest, DataContent shared, DataContent unique) {
    // Raw SQL: exercise the compaction cycle, auxiliary cascade and shared image references together.
    string sharedHash = new ToolImageStore(database.Path + ".markdown-images").Save(shared).Hash;
    string uniqueHash = new ToolImageStore(database.Path + ".markdown-images").Save(unique).Hash;
    Guid compaction = Guid.NewGuid(), aux = Guid.NewGuid(), auxRequest = Guid.NewGuid();
    database.Execute($"""
      PRAGMA foreign_keys = ON;
      INSERT INTO session_compactions (compaction_id, session_id, history_revision, run_id, first_kept_request_id,
          first_kept_message_index, first_kept_run_ordinal, first_kept_request_index, reason, tokens_before, tokens_after, created_at, summary)
      VALUES ('{compaction:D}', '{session:D}', 0, '{run:D}', '{request:D}', 0, 1, 0, 0, 50, 10, 'now', 'summary');
      UPDATE execution_requests SET compaction_id = '{compaction:D}' WHERE request_id = '{request:D}';
      INSERT INTO execution_provider_context VALUES ('{request:D}', 'null');
      INSERT INTO execution_tool_completions (completion_id, session_id, history_revision, request_id, call_id, created_at)
      VALUES ('{Guid.NewGuid():D}', '{session:D}', 0, '{request:D}', 'tool', 'now');
      INSERT INTO auxiliary_conversations (conversation_id, session_id, run_id, history_revision, purpose, llm_selection_json, input_json, created_at)
      VALUES ('{aux:D}', '{session:D}', '{run:D}', 0, 'title', 'null', 'null', 'now');
      INSERT INTO auxiliary_requests (request_id, conversation_id, request_index, created_at)
      VALUES ('{auxRequest:D}', '{aux:D}', 0, 'now');
      INSERT INTO auxiliary_tools (conversation_id, request_id, call_index, call_id, name, arguments_json, result_json)
      SELECT '{aux:D}', '{auxRequest:D}', 0, 'aux-tool', 'fixture', 'null', result_json
      FROM execution_tools WHERE request_id = '{request:D}';
      UPDATE execution_tools SET result_json = json_remove(result_json, '$.images[1]') WHERE request_id = '{request:D}';
      INSERT INTO markdown_images
      SELECT ordinal, 'shared', '{sharedHash}', 'image/png', 3 FROM execution_messages
      WHERE role = 1 AND request_id IN ('{request:D}', '{unrelatedRequest:D}');
      INSERT INTO markdown_images
      SELECT ordinal, 'unique', '{uniqueHash}', 'image/png', 3 FROM execution_messages
      WHERE role = 1 AND request_id = '{request:D}';
      """);
  }
}
