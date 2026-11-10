using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class TranscriptPositionQueryTests {
  [Fact]
  public void PositionsUseDenseRanksAndWeightsScopedToSessionAndRevision() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = Guid.NewGuid(), otherSession = Guid.NewGuid();
    store.Sessions.CreatePrimary(session);
    store.Sessions.CreatePrimary(otherSession);

    SeedUsers(database, session, 0, 2);
    TranscriptEntryRef[] otherFirst = SeedUsers(database, otherSession, 0, 5);
    SeedUsers(database, session, 0, 3);
    TranscriptEntryRef[] otherRest = SeedUsers(database, otherSession, 0, 7, 11);
    TranscriptEntryRef[] otherEntries = [.. otherFirst, .. otherRest];
    database.Execute($"UPDATE sessions SET history_revision = 1 WHERE session_id = '{session:D}';");
    TranscriptEntryRef[] currentEntries = SeedUsers(database, session, 1, 13, 17);

    TranscriptPositionState summary = store.Positions.Summary(session, 1);
    Assert.Equal(session, summary.SessionId);
    Assert.Equal(1L, summary.HistoryRevision);
    Assert.Equal(2L, summary.EntryCount);
    Assert.Equal(30L, summary.TotalWeight);

    TranscriptPosition[] sessionOne = store.Positions.Prompts(session, 1).Prompts.ToArray();
    TranscriptPosition[] sessionTwo = store.Positions.Prompts(otherSession, 0).Prompts.ToArray();
    Assert.Equal(currentEntries, sessionOne.Select(position => position.Entry));
    Assert.Equal(new long[] { 0, 1 }, sessionOne.Select(position => position.Rank));
    Assert.Equal(new long[] { 0, 13 }, sessionOne.Select(position => position.PrefixWeight));
    Assert.Equal(otherEntries, sessionTwo.Select(position => position.Entry));
    Assert.Equal(new long[] { 0, 1, 2 }, sessionTwo.Select(position => position.Rank));
    Assert.Equal(new long[] { 0, 5, 12 }, sessionTwo.Select(position => position.PrefixWeight));

    Assert.Equal(new long[] { 0, 0, 1, 1, 2, 0, 1 }, ReadOnlyPositions(database)
        .Select(position => position.Rank));
    Assert.All(ReadOnlyPositions(database), position => Assert.NotEqual(position.Rank, position.Ordinal));

    TranscriptPositionWindow located = store.Positions.Locate(session, 1, 0);
    Assert.Equal(currentEntries[0], located.Target!.Entry);
    Assert.Equal(2L, located.State.EntryCount);
    Assert.Equal(30L, located.State.TotalWeight);
  }

  [Fact]
  public void LocateMatchesWeightedSeekOracleAtBoundariesAndClampsToTheTail() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = Guid.NewGuid(), emptySession = Guid.NewGuid();
    store.Sessions.CreatePrimary(session);
    store.Sessions.CreatePrimary(emptySession);
    int[] weights = Enumerable.Range(0, 73).Select(index => 1 + index % 7).ToArray();
    TranscriptEntryRef[] entries = SeedUsers(database, session, 0, weights);
    long totalWeight = weights.Sum(weight => (long)weight);
    long[] offsets = [
      0, weights[0] - 1, weights[0], weights.Take(19).Sum(weight => (long)weight) - 1,
      weights.Take(19).Sum(weight => (long)weight), totalWeight - 1, totalWeight, totalWeight + 10_000
    ];

    foreach (long offset in offsets) {
      long clampedOffset = Math.Min(offset, totalWeight - 1);
      int expectedIndex = 0;
      long end = weights[0];
      while (clampedOffset >= end)
        end += weights[++expectedIndex];

      TranscriptPositionWindow result = store.Positions.Locate(session, 0, offset);
      TranscriptPosition target = result.Target!;
      Assert.Equal(entries[expectedIndex], target.Entry);
      Assert.Equal((long)expectedIndex, target.Rank);
      Assert.InRange(result.Entries.Count, 1, 60);
      Assert.Contains(result.Entries, entry => entry.Position.Entry == entries[expectedIndex]);
      Assert.Equal((long)weights[expectedIndex], target.Weight);
      Assert.Equal(weights.Take(expectedIndex).Sum(weight => (long)weight), target.PrefixWeight);
    }

    TranscriptPositionWindow empty = store.Positions.Locate(emptySession, 0, long.MaxValue);
    Assert.Null(empty.Target);
    Assert.Empty(empty.Entries);
    Assert.False(empty.HasBefore);
    Assert.False(empty.HasAfter);
    Assert.Equal(0L, empty.State.EntryCount);
    Assert.Equal(0L, empty.State.TotalWeight);
  }

  [Fact]
  public void WindowsAndPromptPagesStayBoundedAndUseExclusiveRankCursors() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = Guid.NewGuid();
    store.Sessions.CreatePrimary(session);
    TranscriptEntryRef[] entries = SeedUsers(database, session, 0, Enumerable.Repeat(1, 137).ToArray());

    foreach ((long offset, int firstRank, bool hasBefore, bool hasAfter) in new[] {
      (0L, 0, false, true), (68L, 39, true, true), (136L, 77, true, false)
    }) {
      TranscriptPositionWindow window = store.Positions.Locate(session, 0, offset);
      TranscriptPosition target = window.Target!;
      Assert.Equal((long)firstRank, window.Entries[0].Position.Rank);
      Assert.InRange(window.Entries.Count, 1, 60);
      Assert.Equal(entries[(int)offset], target.Entry);
      Assert.Contains(window.Entries, entry => entry.Position.Entry == target.Entry);
      Assert.Equal(hasBefore, window.HasBefore);
      Assert.Equal(hasAfter, window.HasAfter);
    }

    TranscriptPromptPage first = store.Positions.Prompts(session, 0);
    TranscriptPromptPage second = store.Positions.Prompts(session, 0, first.NextAfterRank);
    TranscriptPromptPage third = store.Positions.Prompts(session, 0, second.NextAfterRank);
    Assert.Equal(entries[..60], first.Prompts.Select(prompt => prompt.Entry));
    Assert.Equal(entries[60..120], second.Prompts.Select(prompt => prompt.Entry));
    Assert.Equal(entries[120..], third.Prompts.Select(prompt => prompt.Entry));
    Assert.Equal(60, first.Prompts.Count);
    Assert.Equal(60, second.Prompts.Count);
    Assert.Equal(17, third.Prompts.Count);
    Assert.Equal(59L, first.NextAfterRank);
    Assert.Equal(119L, second.NextAfterRank);
    Assert.Null(third.NextAfterRank);
    Assert.All(new[] { first, second, third }, page => Assert.InRange(page.Prompts.Count, 0, 60));
    TranscriptPromptPage bounded = store.Positions.Prompts(session, 0, limit: 7);
    Assert.Equal(entries[..7], bounded.Prompts.Select(prompt => prompt.Entry));
    Assert.Equal(6L, bounded.NextAfterRank);
  }

  [Fact]
  public void PromptActivationUsesExactIdentityAndRevisionConflictPrecedesLookup() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = Guid.NewGuid(), otherSession = Guid.NewGuid();
    store.Sessions.CreatePrimary(session);
    store.Sessions.CreatePrimary(otherSession);
    TranscriptEntryRef[] entries = SeedUsers(database, session, 0, Enumerable.Repeat(1, 75).ToArray());
    TranscriptEntryRef foreignEntry = Assert.Single(SeedUsers(database, otherSession, 0, 1));

    TranscriptPositionWindow activated = store.Positions.ActivatePrompt(session, 0, entries[35]);
    TranscriptPosition activatedTarget = activated.Target!;
    Assert.Equal(entries[35], activatedTarget.Entry);
    Assert.Equal(35L, activatedTarget.Rank);
    Assert.Contains(activated.Entries, entry => entry.Descriptor.Entry == entries[35]);

    TranscriptEntryRef missing = entries[35] with { EntryId = Guid.NewGuid() };
    Assert.Throws<KeyNotFoundException>(() => store.Positions.ActivatePrompt(session, 0, missing));
    Assert.Throws<KeyNotFoundException>(() => store.Positions.ActivatePrompt(session, 0, foreignEntry));
    Assert.Throws<ArgumentException>(() => store.Positions.ActivatePrompt(session, 0,
        entries[35] with { Kind = TranscriptEntryKind.Text }));

    database.Execute($"UPDATE sessions SET history_revision = 1 WHERE session_id = '{session:D}';");
    HistoryRevisionConflictException conflict = Assert.Throws<HistoryRevisionConflictException>(() =>
        store.Positions.ActivatePrompt(session, 0, missing));
    Assert.Equal(0L, conflict.ExpectedRevision);
    Assert.Equal(1L, conflict.ActualRevision);
  }

  [Fact]
  public void ExactEntryPositioningSupportsEveryKindAndFencesInvalidIdentity() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = Guid.NewGuid(), otherSession = Guid.NewGuid();
    store.Sessions.CreatePrimary(session);
    store.Sessions.CreatePrimary(otherSession);
    TranscriptEntryRef[] entries = SeedMixedEntries(database, session, 0);
    TranscriptEntryRef foreignEntry = SeedMixedEntries(database, otherSession, 0)[0];

    for (int index = 0; index < entries.Length; index++) {
      TranscriptEntryRef entry = entries[index];
      TranscriptPositionWindow window = store.Positions.LocateEntry(session, 0, entry);
      Assert.Equal(entry, window.Target!.Entry);
      Assert.Equal((long)index, window.Target.Rank);
      Assert.Contains(window.Entries, positioned => positioned.Descriptor.Entry == entry
          && positioned.Position.Entry == entry);
      Assert.Equal((TranscriptEntryKind)index, window.Target.Entry.Kind);
    }

    Assert.Throws<KeyNotFoundException>(() => store.Positions.LocateEntry(session, 0, foreignEntry));
    Assert.Throws<KeyNotFoundException>(() => store.Positions.LocateEntry(session, 0,
        entries[1] with { Kind = TranscriptEntryKind.Tool }));
    Assert.Throws<KeyNotFoundException>(() => store.Positions.LocateEntry(session, 0,
        entries[1] with { EntryId = Guid.NewGuid() }));
    Assert.Throws<ArgumentException>(() => store.Positions.LocateEntry(session, 0,
        entries[1] with { ExecutionId = Guid.Empty }));
    Assert.Throws<ArgumentException>(() => store.Positions.LocateEntry(session, 0,
        entries[1] with { EntryId = Guid.Empty }));
    Assert.Throws<ArgumentException>(() => store.Positions.LocateEntry(session, 0,
        entries[1] with { Kind = (TranscriptEntryKind)99 }));
    Assert.Throws<ArgumentException>(() => store.Positions.LocateEntry(session, 0,
        entries[1] with { HistoryRevision = 1 }));
    Assert.Throws<ArgumentException>(() => store.Positions.ActivatePrompt(session, 0, entries[1]));

    database.Execute($"UPDATE sessions SET history_revision = 1 WHERE session_id = '{session:D}';");
    HistoryRevisionConflictException conflict = Assert.Throws<HistoryRevisionConflictException>(() =>
        store.Positions.LocateEntry(session, 0, entries[2]));
    Assert.Equal(0L, conflict.ExpectedRevision);
    Assert.Equal(1L, conflict.ActualRevision);
  }

  [Fact]
  public void PositionQueriesUseIndexedPlansWithoutScansOffsetsOrBodyReads() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    TranscriptEntryRef entry = Assert.Single(SeedUsers(database, session, 0, 1));
    var queries = new[] {
      (Sql: TranscriptPositionReader.LocateSql, Index: "transcript_positions_weight", Parameters: new (string, object)[] {
        ("$session", session), ("$revision", 0L), ("$offset", 0L)
      }),
      (Sql: TranscriptPositionReader.AnchorSql, Index: "execution_messages", Parameters: new (string, object)[] {
        ("$run", entry.ExecutionId), ("$kind", (int)entry.Kind), ("$entry", entry.EntryId),
        ("$session", session), ("$revision", 0L)
      }),
      (Sql: TranscriptPositionReader.PromptsSql, Index: "transcript_positions_prompts", Parameters: new (string, object)[] {
        ("$session", session), ("$revision", 0L), ("$after", -1L), ("$limit", 61)
      }),
      (Sql: TranscriptPositionReader.WindowSql, Index: "transcript_positions", Parameters: new (string, object)[] {
        ("$session", session), ("$revision", 0L), ("$first", 0L)
      })
    };

    foreach ((string sql, string index, (string Name, object Value)[] parameters) in queries) {
      Assert.DoesNotContain(" OFFSET ", sql.Replace('\n', ' '), StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("COUNT", sql, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("execution_output", sql, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("input_decisions", sql, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("execution_tools", sql, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("text_utf8", sql, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("arguments_json", sql, StringComparison.OrdinalIgnoreCase);

      string[] plan = Explain(database, sql, parameters);
      Assert.DoesNotContain(plan, step => step.Contains("SCAN", StringComparison.OrdinalIgnoreCase));
      Assert.Contains(plan, step => step.Contains("SEARCH p", StringComparison.OrdinalIgnoreCase));
      Assert.Contains(plan, step => step.Contains("SEARCH m", StringComparison.OrdinalIgnoreCase));
      Assert.Contains(plan, step => step.Contains(index, StringComparison.OrdinalIgnoreCase));
    }
  }

  private static TranscriptEntryRef[] SeedUsers(TemporaryDatabase database, Guid session, long revision,
      params int[] weights) {
    string selection = "{}";
    SeededEntry[] seeded = weights.Select(weight => new SeededEntry(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), weight)).ToArray();
    string decisions = string.Join(",\n", seeded.Select(entry =>
        $"('{session:D}', '{entry.InputId:D}', {revision}, X'69', 1, '{entry.RunId:D}', NULL, '{selection}')"));
    string runs = string.Join(",\n", seeded.Select(entry =>
        $"('{session:D}', '{entry.RunId:D}', {revision}, '{entry.InputId:D}', 0)"));
    string requests = string.Join(",\n", seeded.Select(entry => $"('{entry.RequestId:D}', '{entry.RunId:D}')"));
    string messages = string.Join(",\n", seeded.Select(entry =>
        $"('{session:D}', {revision}, '{entry.RunId:D}', 0, '{entry.InputId:D}', '{entry.InputId:D}', "
        + $"'{entry.RequestId:D}', {entry.Weight})"));

    database.Execute($"""
      BEGIN;
      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason,
              llm_selection_json)
      VALUES {decisions};
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
      VALUES {runs};
      INSERT INTO execution_requests (request_id, run_id)
      VALUES {requests};
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, input_id, request_id, layout_weight)
      VALUES {messages};
      COMMIT;
      """);

    return seeded.Select(entry => new TranscriptEntryRef(
        entry.RunId, entry.InputId, TranscriptEntryKind.User, revision)).ToArray();
  }

  private static TranscriptEntryRef[] SeedMixedEntries(TemporaryDatabase database, Guid session, long revision) {
    Guid input = Guid.NewGuid(), run = Guid.NewGuid(), request = Guid.NewGuid();
    Guid text = Guid.NewGuid(), tool = Guid.NewGuid();
    database.Execute($"""
      BEGIN;
      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, llm_selection_json)
      VALUES ('{session:D}', '{input:D}', {revision}, X'69', 1, '{run:D}', '{System.Text.Json.JsonSerializer.Serialize(TestLlmSelection.Default)}');
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id)
      VALUES ('{session:D}', '{run:D}', {revision}, '{input:D}');
      INSERT INTO execution_requests (request_id, run_id, next_chunk_index, response_final)
      VALUES ('{request:D}', '{run:D}', 1, 1);
      INSERT INTO execution_tools (run_id, request_id, call_index, call_id, name, arguments_json)
      VALUES ('{run:D}', '{request:D}', 0, 'call', 'shell', json_object());
      INSERT INTO execution_output (request_id, chunk_index, text_utf8, is_final)
      VALUES ('{request:D}', 0, X'6f6b', 1);
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, input_id, request_id)
      VALUES ('{session:D}', {revision}, '{run:D}', 0, '{input:D}', '{input:D}', '{request:D}');
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, request_id, end_chunk_index)
      VALUES ('{session:D}', {revision}, '{run:D}', 1, '{text:D}', '{request:D}', 1);
      INSERT INTO execution_messages
          (session_id, history_revision, run_id, role, message_id, request_id, call_id, tool_name)
      VALUES ('{session:D}', {revision}, '{run:D}', 2, '{tool:D}', '{request:D}', 'call', 'shell');
      COMMIT;
      """);

    return [
      new(run, input, TranscriptEntryKind.User, revision),
      new(run, text, TranscriptEntryKind.Text, revision),
      new(run, tool, TranscriptEntryKind.Tool, revision)
    ];
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

  private sealed record SeededEntry(Guid RunId, Guid InputId, Guid RequestId, int Weight);

  private static IEnumerable<(long Ordinal, long Rank)> ReadOnlyPositions(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT entry_ordinal, rank FROM transcript_positions ORDER BY entry_ordinal;";
    using SqliteDataReader reader = command.ExecuteReader();
    while (reader.Read())
      yield return (reader.GetInt64(0), reader.GetInt64(1));
  }
}
