using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Context;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class SessionListingStorageTests {
  [Fact]
  public void KeysetPagesSurviveInsertionDeletedBoundaryAndReopen() {
    using var database = new TemporaryDatabase();
    Guid[] created = Enumerable.Range(1, 125).Reverse().Select(Id).ToArray();
    SessionIdPage first;
    Guid inserted = Id(200);
    using (UrdStore store = UrdStore.Open(database.Path)) {
      foreach (Guid id in created)
        store.Sessions.CreatePrimary(id);
      first = store.Sessions.ListPrimaryIds(null);
      Assert.Equal(created.Reverse().Take(60), first.SessionIds);
      Assert.Equal("66", first.NextCursor);
      store.Sessions.CreatePrimary(inserted);
      database.Execute($"PRAGMA foreign_keys = ON; DELETE FROM sessions WHERE session_id = '{first.SessionIds[^1]:D}';");
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    SessionIdPage second = reopened.Sessions.ListPrimaryIds(first.NextCursor);
    SessionIdPage third = reopened.Sessions.ListPrimaryIds(second.NextCursor);
    Assert.Equal(created.Reverse(), first.SessionIds.Concat(second.SessionIds).Concat(third.SessionIds));
    Assert.Equal("6", second.NextCursor);
    Assert.Equal(5, third.SessionIds.Count);
    Assert.Null(third.NextCursor);
    Assert.Equal(inserted, reopened.Sessions.ListPrimaryIds(null).SessionIds[0]);
  }

  [Fact]
  public void SessionKindsAndOwnershipRoundTripAcrossReopen() {
    using var database = new TemporaryDatabase();
    Guid primary = Guid.NewGuid(), child = Guid.NewGuid(), nested = Guid.NewGuid();
    string primaryDirectory = Path.Combine(Path.GetTempPath(), "primary");
    string childDirectory = Path.Combine(Path.GetTempPath(), "child");
    using (UrdStore store = UrdStore.Open(database.Path)) {
      AssertSession(store.Sessions.CreatePrimary(primary, primaryDirectory), primary, SessionKind.Primary, null, null, primaryDirectory);
      Assert.Throws<ArgumentException>(() => store.Sessions.CreateSubagent(child, child, "call-1"));
      AssertSession(store.Sessions.CreateSubagent(child, primary, "call-1", childDirectory), child, SessionKind.Subagent, primary, "call-1", childDirectory);
      AssertSession(store.Sessions.CreateSubagent(nested, child, "call-2"), nested, SessionKind.Subagent, child, "call-2", null);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    AssertSession(Assert.IsType<SessionRecord>(reopened.Sessions.Read(primary)), primary, SessionKind.Primary, null, null, primaryDirectory);
    AssertSession(Assert.IsType<SessionRecord>(reopened.Sessions.Read(child)), child, SessionKind.Subagent, primary, "call-1", childDirectory);
    AssertSession(Assert.IsType<SessionRecord>(reopened.Sessions.Read(nested)), nested, SessionKind.Subagent, child, "call-2", null);
  }

  [Theory]
  [InlineData(60)]
  [InlineData(65)]
  public void PrimaryPagesExcludeInterleavedSubagents(int count) {
    using var database = new TemporaryDatabase();
    Guid[] primaryIds = Enumerable.Range(1, count).Select(Id).ToArray();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      foreach (Guid primary in primaryIds) {
        store.Sessions.CreatePrimary(primary);
        store.Sessions.CreateSubagent(Guid.NewGuid(), primary, "spawn");
      }
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    SessionIdPage first = reopened.Sessions.ListPrimaryIds(null);
    Assert.Equal(primaryIds.Reverse().Take(60), first.SessionIds);
    if (count == 60) {
      Assert.Null(first.NextCursor);
      return;
    }

    Assert.NotNull(first.NextCursor);
    SessionIdPage second = reopened.Sessions.ListPrimaryIds(first.NextCursor);
    Assert.Equal(primaryIds.Reverse().Skip(60), second.SessionIds);
    Assert.Null(second.NextCursor);
  }

  [Fact]
  public void FirstPageBootstrapsLatestHumanRunOrContinuationAcrossPrimarySessions() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid older = Guid.NewGuid(), newer = Guid.NewGuid(), child = Guid.NewGuid();
    store.Sessions.CreatePrimary(older);
    store.Sessions.CreatePrimary(newer);
    store.Sessions.CreateSubagent(child, older, "spawn");
    for (int i = 0; i < 60; i++)
      store.Sessions.CreatePrimary(Guid.NewGuid());

    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    JsonElement Page(string? cursor) => JsonSerializer.SerializeToElement(store.Sessions.ListPrimaryIds(cursor), options);
    Assert.False(Page(null).TryGetProperty("latestLlmSelection", out _));

    var human = TestLlmSelection.Default with { ModelId = "human" };
    var continuation = human with { ModelId = "continuation" };
    var ignored = human with { ModelId = "ignored" };
    void Run(Guid session, int kind, int provenance, string selection, bool humanFollowup = false) {
      Guid run = Guid.NewGuid(), input = Guid.NewGuid();
      string json = LlmSelectionJson.Write(human with { ModelId = selection })!.Replace("'", "''");
      string inputId = kind == 0 ? $"'{input:D}'" : "NULL";
      string sender = provenance == 0 ? "NULL" : $"'{session:D}'";
      string continuationFields = kind == 2 ? $"0, '{Guid.NewGuid():D}'" : "NULL, NULL";
      string Decision(Guid id) => $"""
          INSERT INTO input_decisions (session_id, input_id, expected_history_revision,
              original_text_utf8, decision, run_id, llm_selection_json)
          VALUES ('{session:D}', '{id:D}', 0, X'61', 1, '{run:D}', '{json}');
          """;
      string Input(Guid id, int source, string from) => $"""
          INSERT INTO execution_inputs (session_id, input_id, run_id, provenance, sender_session_id)
          VALUES ('{session:D}', '{id:D}', '{run:D}', {source}, {from});
          """;
      Guid followup = Guid.NewGuid();
      database.Execute($"""
          {(kind == 0 ? Decision(input) : "")}
          INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, kind, llm_selection_json,
              continuation_expected_history_revision, continuation_expected_last_execution_id, outcome)
          VALUES ('{session:D}', '{run:D}', 0, {inputId}, {kind}, {(kind == 0 ? "NULL" : $"'{json}'")},
              {continuationFields}, 0);
          {(kind == 0 ? Input(input, provenance, sender) : "")}
          {(humanFollowup ? Decision(followup) + Input(followup, 0, "NULL") : "")}
          """);
    }
    void AssertSelection(string expected) => Assert.Equal(expected,
        Page(null).GetProperty("latestLlmSelection").GetProperty("modelId").GetString());

    Run(newer, 0, 0, human.ModelId);
    AssertSelection(human.ModelId);
    Run(older, 2, 0, continuation.ModelId);
    AssertSelection(continuation.ModelId);
    Run(child, 0, 0, ignored.ModelId);
    Run(newer, 0, 1, ignored.ModelId, humanFollowup: true);
    Run(newer, 0, 2, ignored.ModelId);
    Run(newer, 0, 3, ignored.ModelId);
    Run(newer, 1, 0, ignored.ModelId);
    AssertSelection(continuation.ModelId);
    string cursor = Page(null).GetProperty("nextCursor").GetString()!;
    Assert.False(Page(cursor).TryGetProperty("latestLlmSelection", out _));
    Run(older, 0, 0, human.ModelId);
    AssertSelection(human.ModelId);
  }

  private static void AssertSession(SessionRecord record, Guid id, SessionKind kind, Guid? parent, string? call, string? directory) {
    Assert.Equal(id, record.Id);
    Assert.Equal(0, record.HistoryRevision);
    Assert.Equal(kind, record.Kind);
    Assert.Equal(parent, record.ParentSessionId);
    Assert.Equal(call, record.OwningCallId);
    Assert.Equal(directory, record.WorkingDirectory);
  }

  private static Guid Id(int value) => Guid.Parse($"{value:x8}-0000-0000-0000-000000000000");
}
