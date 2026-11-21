using Urd.Backend.Tests.Support;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class SubagentDiscoveryStorageTests {
  [Fact]
  public void LegacyCallFindsOnlyItsUniqueStartedChildAndStopsAfterParentEdit() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid parent = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    const string callId = "legacy-call";
    Guid request = SeedStartedSubagentCall(database, parent, callId);
    Guid child = store.Sessions.CreateSubagent(Guid.NewGuid(), parent, callId).Id;

    Assert.Equal(child, store.Sessions.FindSubagentByCall(parent, request, callId)!.Id);
    Assert.Null(store.Sessions.FindSubagentByCall(parent, Guid.NewGuid(), callId));

    database.Execute($"UPDATE sessions SET history_revision = 1 WHERE session_id = '{parent:D}';"); // raw-sql: simulate an edit that invalidates legacy history evidence.
    Assert.Null(store.Sessions.FindSubagentByCall(parent, request, callId));
  }

  [Fact]
  public void LegacyDiscoveryRejectsRepeatedStartedCallsAndAmbiguousChildren() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);

    Guid repeatedCallParent = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    const string repeatedCallId = "repeated-call";
    Guid firstRequest = SeedStartedSubagentCall(database, repeatedCallParent, repeatedCallId);
    SeedStartedSubagentCall(database, repeatedCallParent, repeatedCallId);
    store.Sessions.CreateSubagent(Guid.NewGuid(), repeatedCallParent, repeatedCallId);
    Assert.Null(store.Sessions.FindSubagentByCall(repeatedCallParent, firstRequest, repeatedCallId));

    Guid ambiguousChildParent = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    const string ambiguousCallId = "ambiguous-child";
    Guid request = SeedStartedSubagentCall(database, ambiguousChildParent, ambiguousCallId);
    store.Sessions.CreateSubagent(Guid.NewGuid(), ambiguousChildParent, ambiguousCallId);
    store.Sessions.CreateSubagent(Guid.NewGuid(), ambiguousChildParent, ambiguousCallId);
    Assert.Null(store.Sessions.FindSubagentByCall(ambiguousChildParent, request, ambiguousCallId));
  }

  [Fact]
  public void ExactRequestLinksWinForDuplicateCallIdsAndSurviveParentEdits() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid parent = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    const string callId = "shared-call";
    Guid firstRequest = SeedStartedSubagentCall(database, parent, callId);
    Guid firstChild = store.Sessions.CreateSubagent(Guid.NewGuid(), parent, callId,
        owningRequestId: firstRequest).Id;
    Guid secondRequest = SeedStartedSubagentCall(database, parent, callId);
    Guid secondChild = store.Sessions.CreateSubagent(Guid.NewGuid(), parent, callId,
        owningRequestId: secondRequest).Id;

    Assert.Equal(firstChild, store.Sessions.FindSubagentByCall(parent, firstRequest, callId)!.Id);
    Assert.Equal(secondChild, store.Sessions.FindSubagentByCall(parent, secondRequest, callId)!.Id);

    database.Execute($"UPDATE sessions SET history_revision = 1 WHERE session_id = '{parent:D}';"); // raw-sql: exact request links remain durable across parent history edits.
    Assert.Equal(firstChild, store.Sessions.FindSubagentByCall(parent, firstRequest, callId)!.Id);
    Assert.Equal(secondChild, store.Sessions.FindSubagentByCall(parent, secondRequest, callId)!.Id);
  }

  private static Guid SeedStartedSubagentCall(TemporaryDatabase database, Guid parent, string callId) {
    Guid execution = Guid.NewGuid();
    Guid request = Guid.NewGuid();
    database.Execute($"""
      INSERT INTO execution_runs (session_id, run_id, history_revision, kind, llm_selection_json, outcome)
      VALUES ('{parent:D}', '{execution:D}', 0, 1, 'null', 0);
      INSERT INTO execution_requests (request_id, run_id, response_final, outcome)
      VALUES ('{request:D}', '{execution:D}', 1, 0);
      INSERT INTO execution_tools (run_id, request_id, call_index, call_id, name, started, outcome,
          started_at, ended_at, arguments_json, result_json)
      VALUES ('{execution:D}', '{request:D}', 0, '{callId}', 'subagent', 1, 0,
          '2026-01-01T00:00:00Z', '2026-01-01T00:00:01Z', 'null', 'null');
      """); // raw-sql: seed only the persisted started-call evidence needed by legacy discovery.
    return request;
  }
}
