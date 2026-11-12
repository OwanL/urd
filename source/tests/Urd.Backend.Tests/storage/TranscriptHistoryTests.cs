using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Urd.Backend.Tests.Storage;

public sealed class TranscriptHistoryTests(ITestOutputHelper output) {
  [Fact]
  public void KeysetModesReturnOldestFirstPagesAndRefillAtHistoryBoundaries() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = Guid.NewGuid(), otherSession = Guid.NewGuid(), emptySession = Guid.NewGuid();
    store.Sessions.CreatePrimary(session);
    store.Sessions.CreatePrimary(otherSession);
    store.Sessions.CreatePrimary(emptySession);
    database.Execute($"UPDATE sessions SET history_revision = 1 WHERE session_id IN ('{session:D}', '{otherSession:D}');");

    SeedUsers(database, session, 0, 3);
    TranscriptEntryRef[] first = SeedUsers(database, session, 1, 65);
    SeedUsers(database, otherSession, 1, 4);
    TranscriptEntryRef[] last = SeedUsers(database, session, 1, 65);
    TranscriptEntryRef[] entries = [.. first, .. last];

    TranscriptHistoryPage latest = store.History.Read(session, new(TranscriptHistoryMode.Latest, 1));
    Assert.Equal(entries[^60..], latest.Entries.Select(entry => entry.Entry));
    Assert.True(latest.HasBefore);
    Assert.False(latest.HasAfter);

    TranscriptHistoryPage before = store.History.Read(session,
        new(TranscriptHistoryMode.Before, 1, entries[69]));
    Assert.Equal(entries[9..69], before.Entries.Select(entry => entry.Entry));
    Assert.True(before.HasBefore);
    Assert.True(before.HasAfter);

    TranscriptHistoryPage after = store.History.Read(session,
        new(TranscriptHistoryMode.After, 1, entries[69]));
    Assert.Equal(entries[70..], after.Entries.Select(entry => entry.Entry));
    Assert.True(after.HasBefore);
    Assert.False(after.HasAfter);

    TranscriptHistoryPage aroundMiddle = store.History.Read(session,
        new(TranscriptHistoryMode.Around, 1, entries[69]));
    Assert.Equal(entries[40..100], aroundMiddle.Entries.Select(entry => entry.Entry));
    Assert.True(aroundMiddle.HasBefore);
    Assert.True(aroundMiddle.HasAfter);

    TranscriptHistoryPage aroundStart = store.History.Read(session,
        new(TranscriptHistoryMode.Around, 1, entries[0]));
    Assert.Equal(entries[..60], aroundStart.Entries.Select(entry => entry.Entry));
    Assert.False(aroundStart.HasBefore);
    Assert.True(aroundStart.HasAfter);

    TranscriptHistoryPage aroundEnd = store.History.Read(session,
        new(TranscriptHistoryMode.Around, 1, entries[^1]));
    Assert.Equal(entries[^60..], aroundEnd.Entries.Select(entry => entry.Entry));
    Assert.True(aroundEnd.HasBefore);
    Assert.False(aroundEnd.HasAfter);

    TranscriptHistoryPage beforeStart = store.History.Read(session,
        new(TranscriptHistoryMode.Before, 1, entries[0]));
    Assert.Empty(beforeStart.Entries);
    Assert.False(beforeStart.HasBefore);
    Assert.True(beforeStart.HasAfter);

    TranscriptHistoryPage afterEnd = store.History.Read(session,
        new(TranscriptHistoryMode.After, 1, entries[^1]));
    Assert.Empty(afterEnd.Entries);
    Assert.True(afterEnd.HasBefore);
    Assert.False(afterEnd.HasAfter);

    TranscriptHistoryPage empty = store.History.Read(emptySession, new(TranscriptHistoryMode.Latest, 0));
    Assert.Empty(empty.Entries);
    Assert.False(empty.HasBefore);
    Assert.False(empty.HasAfter);
  }

  [Fact]
  public void MissingAnchorsAreNotFoundAndRevisionConflictPrecedesAnchorLookup() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = Guid.NewGuid(), otherSession = Guid.NewGuid();
    store.Sessions.CreatePrimary(session);
    store.Sessions.CreatePrimary(otherSession);
    TranscriptEntryRef anchor = Assert.Single(SeedUsers(database, session, 0, 1));

    Assert.Throws<KeyNotFoundException>(() => store.History.Read(otherSession,
        new(TranscriptHistoryMode.Before, 0, anchor)));
    var missing = anchor with { EntryId = Guid.NewGuid() };
    Assert.Throws<KeyNotFoundException>(() => store.History.Read(session,
        new(TranscriptHistoryMode.After, 0, missing)));

    database.Execute($"UPDATE sessions SET history_revision = 1 WHERE session_id = '{session:D}';");
    var deletedAnchor = new TranscriptEntryRef(anchor.ExecutionId, Guid.NewGuid(), anchor.Kind, 0);
    HistoryRevisionConflictException conflict = Assert.Throws<HistoryRevisionConflictException>(() =>
        store.History.Read(session, new(TranscriptHistoryMode.Around, 0, deletedAnchor)));
    Assert.Equal(0, conflict.ExpectedRevision);
    Assert.Equal(1, conflict.ActualRevision);
  }

  [Fact]
  public void MixedEntriesStayScopedAndExposeMetadataWithoutDecodingBodies() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = Guid.NewGuid(), otherSession = Guid.NewGuid();
    store.Sessions.CreatePrimary(session);
    store.Sessions.CreatePrimary(otherSession);
    database.Execute($"UPDATE sessions SET history_revision = 3 WHERE session_id IN ('{session:D}', '{otherSession:D}');");

    RichFixture fixture = SeedRichHistory(database, session, otherSession);
    TranscriptHistoryPage page = store.History.Read(session, new(TranscriptHistoryMode.Latest, 3));

    Assert.Equal(new[] { "Compaction", "CreatedAt", "Entry", "Text", "Tool" },
        typeof(TranscriptEntryDescriptor).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    Assert.Equal(new[] { "AvailableThrough", "FinalOutputCommitted", "From", "IsClosed" },
        typeof(TranscriptTextFacts).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    Assert.Equal(new[] {
      "ArgumentsExcerpt", "Background", "BatchedWithNext", "BatchedWithPrevious", "CallId", "EndedAt",
      "Interrupted", "IsClosed", "Name", "Outcome", "RequestId", "Started", "StartedAt"
    }, typeof(TranscriptToolFacts).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));

    Assert.Equal(new[] {
      TranscriptEntryKind.User, TranscriptEntryKind.Text, TranscriptEntryKind.Tool, TranscriptEntryKind.Text,
      TranscriptEntryKind.User, TranscriptEntryKind.Tool, TranscriptEntryKind.User, TranscriptEntryKind.Text
    }, page.Entries.Select(entry => entry.Entry.Kind));
    Assert.Equal(fixture.Entries, page.Entries.Select(entry => entry.Entry));
    Assert.All(page.Entries, entry => Assert.Equal(3, entry.Entry.HistoryRevision));
    Assert.Equal(new[] { fixture.FirstExecution, fixture.ToolOnlyExecution },
        page.Settlements.Select(settlement => settlement.ExecutionId));
    Assert.Equal(2, page.Settlements.Count);
    Assert.All(page.Settlements, settlement => Assert.Equal(ExecutionOutcome.Completed, settlement.Outcome));
    Assert.False(page.HasBefore);
    Assert.False(page.HasAfter);

    TranscriptEntryDescriptor user = page.Entries[0];
    Assert.Equal(new MessageContentCursor(new(fixture.FirstExecution, fixture.UserMessage, MessageRole.User, 3), 0, 0),
        user.Text!.From);
    Assert.Equal(new MessageContentCursor(new(fixture.FirstExecution, fixture.UserMessage, MessageRole.User, 3), 1, 0),
        user.Text.AvailableThrough);
    Assert.True(user.Text.IsClosed);
    Assert.False(user.Text.FinalOutputCommitted);

    TranscriptEntryDescriptor firstText = page.Entries[1];
    AssertTextFacts(firstText, fixture.FirstExecution, fixture.FirstText, 0, 1, closed: true, final: false);
    TranscriptEntryDescriptor firstTool = page.Entries[2];
    Assert.Equal("call-a", firstTool.Tool!.CallId);
    Assert.Equal("first-tool", firstTool.Tool.Name);
    Assert.True(firstTool.Tool.Started);
    Assert.Equal(ExecutionOutcome.Completed, firstTool.Tool.Outcome);
    Assert.False(firstTool.Tool.Interrupted);
    Assert.True(firstTool.Tool.IsClosed);
    Assert.Equal(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), firstTool.Tool.StartedAt);
    Assert.Equal(new DateTimeOffset(2026, 11, 9, 12, 0, 1, TimeSpan.Zero), firstTool.Tool.EndedAt);
    AssertTextFacts(page.Entries[3], fixture.FirstExecution, fixture.SecondText, 2, 3, closed: true, final: true);

    TranscriptToolFacts toolOnly = page.Entries[5].Tool!;
    Assert.Equal(fixture.ToolOnlyRequest, toolOnly.RequestId);
    Assert.Equal("tool-only", toolOnly.Name);
    Assert.False(toolOnly.Started);
    Assert.False(toolOnly.Interrupted);
    Assert.Null(toolOnly.Outcome);
    Assert.True(toolOnly.IsClosed);
    Assert.Null(toolOnly.StartedAt);
    Assert.Null(toolOnly.EndedAt);

    TranscriptTextFacts activeText = page.Entries[7].Text!;
    Assert.Equal(1, activeText.AvailableThrough.SegmentIndex);
    Assert.False(activeText.IsClosed);
    Assert.False(activeText.FinalOutputCommitted);

    // The fixture stores invalid UTF-8 and malformed JSON in all body-bearing tables.
    // Reaching the descriptors above proves this metadata read never decodes those values.
    Assert.Equal(3L, database.Scalar($"SELECT COUNT(*) FROM input_decisions WHERE session_id = '{session:D}' AND original_text_utf8 = X'FF';"));
    Assert.Equal(4L, database.Scalar($"SELECT COUNT(*) FROM execution_output WHERE text_utf8 = X'FF';"));
    Assert.Equal(2L, database.Scalar($"SELECT COUNT(*) FROM execution_tools WHERE arguments_json = 'not-json' AND result_json = 'not-json';"));
  }

  [Fact]
  public void BatchFactsLookBeyondWindowEdgesAndRequireAdjacentAuthorizedToolRows() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    TranscriptEntryRef[] entries = SeedBatchHistory(database, session);

    TranscriptHistoryPage page = store.History.Read(session,
        new(TranscriptHistoryMode.Around, 0, entries[32]));
    Assert.Equal(60, page.Entries.Count);
    TranscriptToolFacts first = page.Entries[0].Tool!;
    Assert.Equal("batch-c", first.CallId);
    Assert.True(first.BatchedWithPrevious);
    Assert.False(first.BatchedWithNext);
    Assert.True(first.Background);
    Assert.Equal("{\"call\":\"batch-c\"}", first.ArgumentsExcerpt);
    Assert.DoesNotContain("argumentsExcerpt", JsonSerializer.Serialize(first), StringComparison.Ordinal);

    TranscriptToolFacts interrupted = page.Entries.Single(entry => entry.Tool?.CallId == "gap-a").Tool!;
    Assert.False(interrupted.BatchedWithPrevious);
    Assert.True(interrupted.BatchedWithNext);
    TranscriptToolFacts gapEnd = page.Entries.Single(entry => entry.Tool?.CallId == "gap-b").Tool!;
    Assert.True(gapEnd.BatchedWithPrevious);
    Assert.False(gapEnd.BatchedWithNext);
    TranscriptToolFacts reservation = page.Entries.Single(entry => entry.Tool?.CallId == "reservation").Tool!;
    Assert.False(reservation.BatchedWithPrevious);
    Assert.False(reservation.BatchedWithNext);
    Assert.Null(reservation.ArgumentsExcerpt);
    TranscriptToolFacts afterReservation = page.Entries.Single(entry => entry.Tool?.CallId == "after-reservation").Tool!;
    Assert.False(afterReservation.BatchedWithPrevious);

    TranscriptToolFacts lastInWindow = page.Entries[^1].Tool!;
    Assert.Equal("window-edge-a", lastInWindow.CallId);
    Assert.True(lastInWindow.BatchedWithNext);

    TranscriptPositionWindow position = store.Positions.LocateEntry(session, 0, entries[32]);
    TranscriptToolFacts positionedFirst = position.Entries[0].Descriptor.Tool!;
    Assert.True(positionedFirst.BatchedWithPrevious);
    Assert.Equal("{\"call\":\"batch-c\"}", positionedFirst.ArgumentsExcerpt);
    Assert.True(position.Entries[^1].Descriptor.Tool!.BatchedWithNext);
  }

  [Fact]
  public void HistoryQueriesUseIndexedKeysetPlansWithoutOffsetCountsOrEntryScans() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    TranscriptEntryRef anchor = Assert.Single(SeedUsers(database, session, 0, 1));
    var queries = new[] {
      (Sql: TranscriptHistoryStorage.WindowSql(ascending: false), Parameters: new (string, object)[] {
        ("$session", session), ("$revision", 0L), ("$boundary", long.MaxValue), ("$limit", 60)
      }),
      (Sql: TranscriptHistoryStorage.WindowSql(ascending: true), Parameters: new (string, object)[] {
        ("$session", session), ("$revision", 0L), ("$boundary", 1L), ("$limit", 60)
      }),
      (Sql: TranscriptHistoryStorage.AnchorSql, Parameters: new (string, object)[] {
        ("$run", anchor.ExecutionId), ("$kind", (int)anchor.Kind), ("$entry", anchor.EntryId),
        ("$session", session), ("$revision", 0L)
      }),
      (Sql: TranscriptHistoryStorage.LookaheadSql, Parameters: new (string, object)[] {
        ("$session", session), ("$revision", 0L), ("$first", 1L), ("$last", 1L)
      })
    };

    foreach ((string sql, (string Name, object Value)[] parameters) in queries) {
      Assert.DoesNotContain("OFFSET", sql, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("COUNT", sql, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("arguments_json", sql, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("result_json", sql, StringComparison.OrdinalIgnoreCase);
      string[] plan = Explain(database, sql, parameters);
      Assert.DoesNotContain(plan, step => step.Contains("SCAN execution_messages", StringComparison.OrdinalIgnoreCase));
      Assert.Contains(plan, step => step.Contains("SEARCH execution_messages", StringComparison.OrdinalIgnoreCase));
      if (sql != TranscriptHistoryStorage.AnchorSql)
        Assert.Contains(plan, step => step.Contains("execution_messages_history_order", StringComparison.OrdinalIgnoreCase));
    }
  }

  [Fact]
  public void LatestWindowLatencyIsMeasuredOnAnOwnedHundredThousandEntryWorkload() {
    using var database = new TemporaryDatabase("urd-transcript-history-workload");
    Guid session = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path))
      store.Sessions.CreatePrimary(session);

    SeedLargeMixedHistory(database, session);
    double coldMilliseconds, warmMedianMilliseconds;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      long started = Stopwatch.GetTimestamp();
      TranscriptHistoryPage cold = store.History.Read(session, new(TranscriptHistoryMode.Latest, 0));
      coldMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
      Assert.Equal(60, cold.Entries.Count);

      long positionStarted = Stopwatch.GetTimestamp();
      TranscriptPositionWindow distant = store.Positions.Locate(session, 0, 50_000L * 256);
      double positionMilliseconds = Stopwatch.GetElapsedTime(positionStarted).TotalMilliseconds;
      Assert.Equal(100_000L, distant.State.EntryCount);
      Assert.Equal(50_000L, distant.Target!.Rank);
      Assert.Equal(60, distant.Entries.Count);
      TranscriptPromptPage prompts = store.Positions.Prompts(session, 0, distant.Target.Rank);
      Assert.Equal(60, prompts.Prompts.Count);
      TranscriptPositionWindow exact = store.Positions.ActivatePrompt(session, 0, prompts.Prompts[^1].Entry);
      Assert.Equal(prompts.Prompts[^1], exact.Target);
      output.WriteLine($"Weighted distant lookup (100,000 synthetic fixed-weight entries, first position read): {positionMilliseconds:F3} ms; marker page and exact activation verified.");

      double[] warmSamples = new double[9];
      for (int sample = 0; sample < warmSamples.Length; sample++) {
        started = Stopwatch.GetTimestamp();
        TranscriptHistoryPage warm = store.History.Read(session, new(TranscriptHistoryMode.Latest, 0));
        warmSamples[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.Equal(60, warm.Entries.Count);
      }
      Array.Sort(warmSamples);
      warmMedianMilliseconds = warmSamples[warmSamples.Length / 2];
    }

    Assert.Equal(100_000L, database.Scalar($"SELECT COUNT(*) FROM execution_messages WHERE session_id = '{session:D}';"));
    long payloadBytes = (long)database.Scalar($"""
      SELECT (SELECT SUM(length(original_text_utf8)) FROM input_decisions WHERE session_id = '{session:D}')
          + (SELECT SUM(length(o.text_utf8)) FROM execution_output o
              JOIN execution_requests q ON q.request_id = o.request_id
              JOIN execution_runs r ON r.run_id = q.run_id WHERE r.session_id = '{session:D}');
      """)!;
    Assert.Equal(249_975_000L, payloadBytes);
    output.WriteLine($"Latest history latency (100,000 entries, {payloadBytes:N0} body bytes): first cold SQLite page-cache read {coldMilliseconds:F3} ms; warm median {warmMedianMilliseconds:F3} ms (OS file cache not flushed).");
  }

  private static void AssertTextFacts(TranscriptEntryDescriptor descriptor, Guid execution, Guid entry,
      long from, long through, bool closed, bool final) {
    TranscriptTextFacts facts = descriptor.Text!;
    var message = new MessageRef(execution, entry, MessageRole.Assistant, descriptor.Entry.HistoryRevision);
    Assert.Equal(new MessageContentCursor(message, from, 0), facts.From);
    Assert.Equal(new MessageContentCursor(message, through, 0), facts.AvailableThrough);
    Assert.Equal(closed, facts.IsClosed);
    Assert.Equal(final, facts.FinalOutputCommitted);
  }

  private static TranscriptEntryRef[] SeedUsers(TemporaryDatabase database, Guid session, long revision, int count) {
    string inputPrefix = Prefix(), runPrefix = Prefix(), requestPrefix = Prefix();
    string selection = JsonSerializer.Serialize(TestLlmSelection.Default);
    database.Execute($"""
      BEGIN;
      WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x + 1 FROM n WHERE x < {count})
      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason,
              llm_selection_json)
      SELECT '{session:D}', '{inputPrefix}' || printf('%012d', x), {revision}, CAST('input ' || x AS BLOB),
          1, '{runPrefix}' || printf('%012d', x), NULL, '{selection}' FROM n;
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
      SELECT session_id, '{runPrefix}' || substr(input_id, 25), {revision}, input_id, 0
      FROM input_decisions WHERE session_id = '{session:D}' AND input_id LIKE '{inputPrefix}%'
      ORDER BY input_id;
      INSERT INTO execution_requests (request_id, run_id)
      SELECT '{requestPrefix}' || substr(input_id, 25), run_id FROM execution_runs
      WHERE session_id = '{session:D}' AND input_id LIKE '{inputPrefix}%';
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id, request_id)
      SELECT r.session_id, r.history_revision, r.run_id, 0, r.input_id, r.input_id, q.request_id
      FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
      WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{inputPrefix}%'
      ORDER BY r.ordinal;
      COMMIT;
      """);

    return Enumerable.Range(1, count).Select(index => new TranscriptEntryRef(
        Guid.Parse(Id(runPrefix, index)), Guid.Parse(Id(inputPrefix, index)), TranscriptEntryKind.User, revision))
        .ToArray();
  }

  private static RichFixture SeedRichHistory(TemporaryDatabase database, Guid session, Guid otherSession) {
    const long revision = 3;
    string selection = JsonSerializer.Serialize(TestLlmSelection.Default);
    Guid firstExecution = Guid.NewGuid(), firstInput = Guid.NewGuid(), firstRequest = Guid.NewGuid();
    Guid firstOutput = Guid.NewGuid(), userMessage = firstInput, firstText = Guid.NewGuid(), secondText = Guid.NewGuid();
    Guid toolOnlyExecution = Guid.NewGuid(), toolOnlyInput = Guid.NewGuid(), toolOnlyRequest = Guid.NewGuid();
    Guid toolOnlyText = Guid.NewGuid(), activeExecution = Guid.NewGuid(), activeInput = Guid.NewGuid();
    Guid activeRequest = Guid.NewGuid(), activeOutput = Guid.NewGuid(), activeText = Guid.NewGuid();

    database.Execute($"""
      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason,
              llm_selection_json)
      VALUES ('{session:D}', '{firstInput:D}', {revision}, X'FF', 1, '{firstExecution:D}', NULL, '{selection}');
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
      VALUES ('{session:D}', '{firstExecution:D}', {revision}, '{firstInput:D}', 0);
      INSERT INTO execution_requests
          (request_id, run_id, response_id, next_chunk_index, response_final, outcome)
      VALUES ('{firstRequest:D}', '{firstExecution:D}', '{firstOutput:D}', 3, 1, 0);
      INSERT INTO execution_output (request_id, chunk_index, text_utf8, is_final)
      VALUES ('{firstRequest:D}', 0, X'FF', 0), ('{firstRequest:D}', 1, X'FF', 0), ('{firstRequest:D}', 2, X'FF', 1);
      INSERT INTO execution_tools
          (run_id, request_id, call_index, call_id, name, arguments_json, started, outcome, result_json, started_at, ended_at)
      VALUES ('{firstExecution:D}', '{firstRequest:D}', 0, 'call-a', 'first-tool', 'not-json', 1, 0, 'not-json',
          '2026-11-09T12:00:00+00:00', '2026-11-09T12:00:01+00:00');
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, input_id, request_id)
      VALUES ('{session:D}', {revision}, '{firstExecution:D}', 0, '{userMessage:D}', '{firstInput:D}', '{firstRequest:D}');
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, request_id, first_chunk_index, end_chunk_index, created_at)
      VALUES ('{session:D}', {revision}, '{firstExecution:D}', 1, '{firstText:D}', '{firstRequest:D}', 0, 1,
          '2026-11-09T11:59:59+00:00');
      """);

    SeedUsers(database, otherSession, revision, 1);

    database.Execute($"""
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, request_id, call_id, tool_name, created_at)
      VALUES ('{session:D}', {revision}, '{firstExecution:D}', 2, '{Guid.NewGuid():D}', '{firstRequest:D}',
          'call-a', 'first-tool', '2026-11-09T12:00:00+00:00');
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, request_id, first_chunk_index, end_chunk_index, created_at)
      VALUES ('{session:D}', {revision}, '{firstExecution:D}', 1, '{secondText:D}', '{firstRequest:D}', 2, 3,
          '2026-11-09T12:00:02+00:00');

      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason,
              llm_selection_json)
      VALUES ('{session:D}', '{toolOnlyInput:D}', {revision}, X'FF', 1, '{toolOnlyExecution:D}', NULL, '{selection}');
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
      VALUES ('{session:D}', '{toolOnlyExecution:D}', {revision}, '{toolOnlyInput:D}', 0);
      INSERT INTO execution_requests (request_id, run_id, next_chunk_index, response_final, outcome)
      VALUES ('{toolOnlyRequest:D}', '{toolOnlyExecution:D}', 0, 0, 0);
      INSERT INTO execution_tools (run_id, request_id, call_index, call_id, name, arguments_json, result_json)
      VALUES ('{toolOnlyExecution:D}', '{toolOnlyRequest:D}', 0, 'tool-only-call', 'tool-only', 'not-json', 'not-json');
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id, request_id)
      VALUES ('{session:D}', {revision}, '{toolOnlyExecution:D}', 0, '{toolOnlyInput:D}', '{toolOnlyInput:D}', '{toolOnlyRequest:D}');
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, request_id, call_id, tool_name)
      VALUES ('{session:D}', {revision}, '{toolOnlyExecution:D}', 2, '{toolOnlyText:D}', '{toolOnlyRequest:D}',
          'tool-only-call', 'tool-only');

      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason,
              llm_selection_json)
      VALUES ('{session:D}', '{activeInput:D}', {revision}, X'FF', 1, '{activeExecution:D}', NULL, '{selection}');
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id)
      VALUES ('{session:D}', '{activeExecution:D}', {revision}, '{activeInput:D}');
      INSERT INTO execution_requests (request_id, run_id, response_id, next_chunk_index, response_final)
      VALUES ('{activeRequest:D}', '{activeExecution:D}', '{activeOutput:D}', 1, 0);
      INSERT INTO execution_output (request_id, chunk_index, text_utf8, is_final)
      VALUES ('{activeRequest:D}', 0, X'FF', 0);
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id, request_id)
      VALUES ('{session:D}', {revision}, '{activeExecution:D}', 0, '{activeInput:D}', '{activeInput:D}', '{activeRequest:D}');
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, request_id, first_chunk_index)
      VALUES ('{session:D}', {revision}, '{activeExecution:D}', 1, '{activeText:D}', '{activeRequest:D}', 0);
      """);

    SeedUsers(database, session, revision - 1, 1);
    TranscriptEntryRef[] entries = [
      new(firstExecution, userMessage, TranscriptEntryKind.User, revision),
      new(firstExecution, firstText, TranscriptEntryKind.Text, revision),
      new(firstExecution, Guid.Parse((string)database.Scalar($"SELECT message_id FROM execution_messages WHERE session_id = '{session:D}' AND run_id = '{firstExecution:D}' AND role = 2;")!), TranscriptEntryKind.Tool, revision),
      new(firstExecution, secondText, TranscriptEntryKind.Text, revision),
      new(toolOnlyExecution, toolOnlyInput, TranscriptEntryKind.User, revision),
      new(toolOnlyExecution, toolOnlyText, TranscriptEntryKind.Tool, revision),
      new(activeExecution, activeInput, TranscriptEntryKind.User, revision),
      new(activeExecution, activeText, TranscriptEntryKind.Text, revision)
    ];
    return new(entries, firstExecution, userMessage, firstText, secondText,
        toolOnlyExecution, toolOnlyRequest);
  }

  private static TranscriptEntryRef[] SeedBatchHistory(TemporaryDatabase database, Guid session) {
    Guid run = Guid.NewGuid(), request = Guid.NewGuid();
    (bool IsTool, string? CallId)[] kinds = Enumerable.Range(0, 64).Select(index => index switch {
      1 => (true, "batch-a"),
      2 => (true, "batch-b"),
      3 => (true, "batch-c"),
      5 => (true, "gap-a"),
      6 => (true, "gap-b"),
      7 => (true, "reservation"),
      8 => (true, "after-reservation"),
      62 => (true, "window-edge-a"),
      63 => (true, "window-edge-b"),
      _ => (false, (string?)null)
    }).ToArray();
    Guid[] messageIds = Enumerable.Range(0, kinds.Length).Select(_ => Guid.NewGuid()).ToArray();
    string messages = string.Join(",\n", kinds.Select((entry, index) => entry.IsTool
        ? $"('{session:D}', 0, '{run:D}', 2, '{messageIds[index]:D}', '{request:D}', '{entry.CallId}', 'test-tool', 0, NULL)"
        : $"('{session:D}', 0, '{run:D}', 1, '{messageIds[index]:D}', '{request:D}', NULL, NULL, 0, 0)"));
    string authorizedTools = string.Join(",\n", kinds.Select((entry, index) => (entry, index))
        .Where(item => item.entry.IsTool && item.entry.CallId != "reservation")
        .Select(item => $"('{run:D}', '{request:D}', {item.index}, '{item.entry.CallId}', 'test-tool', '{{}}', "
            + $"'{{\"call\":\"{item.entry.CallId}\"}}', {(item.entry.CallId == "batch-c" ? 1 : 0)}, "
            + $"{(item.entry.CallId == "batch-c" ? 1 : 0)})"));
    database.Execute($"""
      INSERT INTO execution_runs (session_id, run_id, history_revision, compaction_selection_json, outcome)
      VALUES ('{session:D}', '{run:D}', 0, 'x', 0);
      INSERT INTO execution_requests (request_id, run_id, response_final, outcome)
      VALUES ('{request:D}', '{run:D}', 1, 0);
      INSERT INTO execution_tools
          (run_id, request_id, call_index, call_id, name, arguments_json, arguments_excerpt, started, background)
      VALUES {authorizedTools};
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, request_id, call_id, tool_name,
              first_chunk_index, end_chunk_index)
      VALUES {messages};
      """);

    return kinds.Select((entry, index) => new TranscriptEntryRef(run, messageIds[index],
        entry.IsTool ? TranscriptEntryKind.Tool : TranscriptEntryKind.Text, 0)).ToArray();
  }

  private static void SeedLargeMixedHistory(TemporaryDatabase database, Guid session) {
    const int runs = 45_000;
    const int inputBytes = 2_777;
    const int outputBytes = 2_778;
    string inputPrefix = Prefix(), runPrefix = Prefix(), requestPrefix = Prefix();
    string outputPrefix = Prefix(), toolEntryPrefix = Prefix();
    string selection = JsonSerializer.Serialize(TestLlmSelection.Default);
    database.Execute($"""
      BEGIN;
      WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x + 1 FROM n WHERE x < {runs})
      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason,
              llm_selection_json)
      SELECT '{session:D}', '{inputPrefix}' || printf('%012d', x), 0, zeroblob({inputBytes}), 1,
          '{runPrefix}' || printf('%012d', x), NULL, '{selection}' FROM n;
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
      SELECT session_id, run_id, expected_history_revision, input_id, 0 FROM input_decisions
      WHERE session_id = '{session:D}' AND input_id LIKE '{inputPrefix}%' ORDER BY input_id;
      INSERT INTO execution_requests (request_id, run_id, request_index, response_id, next_chunk_index, response_final, outcome)
      SELECT '{requestPrefix}' || substr(input_id, 25), run_id, 0,
          '{outputPrefix}' || substr(input_id, 25), 1, 1, 0
      FROM execution_runs WHERE session_id = '{session:D}' AND input_id LIKE '{inputPrefix}%';
      INSERT INTO execution_output (request_id, chunk_index, text_utf8, is_final)
      SELECT request_id, 0, zeroblob({outputBytes}), 1 FROM execution_requests
      WHERE request_id LIKE '{requestPrefix}%';
      INSERT INTO execution_inputs (session_id, input_id, run_id, request_id, outcome)
      SELECT r.session_id, r.input_id, r.run_id, q.request_id, 0
      FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
      WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{inputPrefix}%';
      INSERT INTO execution_tools
          (run_id, request_id, call_index, call_id, name, arguments_json, started, outcome, result_json, started_at, ended_at)
      SELECT r.run_id, q.request_id, 0, 'call-' || substr(r.input_id, 25), 'bulk-tool', 'bulk-args', 1, 0, 'bulk-result',
          '2026-11-09T12:00:00+00:00', '2026-11-09T12:00:01+00:00'
      FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
      WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{inputPrefix}%'
          AND CAST(substr(r.input_id, 25) AS INTEGER) BETWEEN 1 AND 40000
          AND CAST(substr(r.input_id, 25) AS INTEGER) % 4 = 0;
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, input_id, request_id,
              first_chunk_index, end_chunk_index, call_id, tool_name)
      SELECT session_id, 0, run_id, role, message_id, input_id, request_id,
          first_chunk_index, end_chunk_index, call_id, tool_name
      FROM (
          SELECT r.ordinal AS run_order, 0 AS entry_order, r.session_id, r.run_id, 0 AS role,
              r.input_id AS message_id, r.input_id, q.request_id, 0 AS first_chunk_index,
              NULL AS end_chunk_index, NULL AS call_id, NULL AS tool_name
          FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
          WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{inputPrefix}%'
          UNION ALL
          SELECT r.ordinal, 1, r.session_id, r.run_id, 1, q.response_id, NULL, q.request_id, 0, 1, NULL, NULL
          FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
          WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{inputPrefix}%'
          UNION ALL
          SELECT r.ordinal, 2, r.session_id, r.run_id, 2, '{toolEntryPrefix}' || substr(r.input_id, 25),
              NULL, q.request_id, 0, NULL, t.call_id, t.name
          FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
          JOIN execution_tools t ON t.request_id = q.request_id
          WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{inputPrefix}%'
      ) ORDER BY run_order, entry_order;
      COMMIT;
      """);
  }

  private static string[] Explain(TemporaryDatabase database, string sql, (string Name, object Value)[] parameters) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "EXPLAIN QUERY PLAN " + sql;
    foreach ((string name, object value) in parameters)
      command.Parameters.AddWithValue(name, value is Guid id ? id.ToString("D") : value);
    using SqliteDataReader reader = command.ExecuteReader();
    var steps = new List<string>();
    while (reader.Read())
      steps.Add(reader.GetString(3));
    return [.. steps];
  }

  private static string Prefix() => Guid.NewGuid().ToString("D")[..24];

  private static string Id(string prefix, int index) => $"{prefix}{index:D12}";

  private sealed record RichFixture(
      TranscriptEntryRef[] Entries, Guid FirstExecution, Guid UserMessage, Guid FirstText, Guid SecondText,
      Guid ToolOnlyExecution, Guid ToolOnlyRequest);
}
