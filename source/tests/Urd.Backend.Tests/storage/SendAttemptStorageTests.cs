using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class SendAttemptStorageTests {
  [Fact]
  public void ClaimsStayPendingAndPromotionIsOrderedAtomicIdempotentAndStopFenced() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    var identity = new ExecutionIdentity(session, run, 0);
    UserMessageSubmission first = Input("first"), second = Input("second"), third = Input("third");
    Admit(store, session, run, first);
    Guid request = store.Executions.CreateNextRequest(identity)!.RequestId;
    Assert.Empty(History(store, session));
    Assert.Equal(first.SubmissionId, Assert.Single(store.Snapshots.ReadPersisted(session).PendingInputs).SubmissionId);
    Assert.Null(store.Snapshots.ReadPersisted(session).FirstUserPreview);
    Assert.True(store.Executions.StartRequest(identity, request));
    Assert.True(store.Executions.StartRequest(identity, request));
    Assert.Single(History(store, session));
    Assert.Equal("first", store.Snapshots.ReadPersisted(session).FirstUserPreview);
    Assert.Empty(store.Snapshots.ReadPersisted(session).PendingInputs);
    Admit(store, session, run, second);
    Admit(store, session, run, third);
    Assert.Single(History(store, session));
    Assert.True(store.LlmResponses.Append(identity, new(request, Guid.NewGuid(), 0, "", true)));
    Assert.True(store.LlmResponses.CompleteRequest(identity, new(request, ExecutionOutcome.Completed)));
    Guid next = store.Executions.CreateNextRequest(identity)!.RequestId;
    Assert.Equal(new[] { second.SubmissionId, third.SubmissionId },
        store.Snapshots.ReadPersisted(session).PendingInputs.Select(input => input.SubmissionId));
    Assert.False(store.Executions.StartRequest(identity with { SessionId = Guid.NewGuid() }, next));
    Assert.False(store.Executions.StartRequest(identity with { HistoryRevision = 1 }, next));
    Assert.True(store.Executions.StartRequest(identity, next));
    Assert.Equal(new[] { first.SubmissionId, second.SubmissionId, third.SubmissionId },
        History(store, session).Where(entry => entry.Entry.Kind == TranscriptEntryKind.User).Select(entry => entry.Entry.EntryId));
    Assert.Equal(next.ToString("D").ToUpperInvariant(),
        ((string)database.Scalar($"SELECT request_id FROM execution_messages WHERE message_id = '{second.SubmissionId:D}';")!).ToUpperInvariant()); // raw-sql: verify transaction-time history placement
    store.Executions.RequestStop(session, run, true);
    Assert.False(store.Executions.StartRequest(identity, next));
    Assert.Equal(3, History(store, session).Count(entry => entry.Entry.Kind == TranscriptEntryKind.User));
  }

  [Fact]
  public void MarkerAndEveryAssignedEntryRollBackTogether() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    var identity = new ExecutionIdentity(session, run, 0);
    Admit(store, session, run, Input("one"));
    Admit(store, session, run, Input("two"));
    Guid request = store.Executions.CreateNextRequest(identity)!.RequestId;
    database.Execute("""
      CREATE TRIGGER fail_placement BEFORE INSERT ON execution_messages
      WHEN (SELECT count(*) FROM execution_messages) = 1
      BEGIN SELECT RAISE(ABORT, 'second placement rejected'); END;
      """);
    Assert.Throws<SqliteException>(() => store.Executions.StartRequest(identity, request));
    Assert.Equal(1L, database.Scalar("SELECT attempt FROM execution_requests;")); // raw-sql: verify attempt marker rolls back with placements
    Assert.Empty(History(store, session));
    Assert.Equal(2, store.Snapshots.ReadPersisted(session).PendingInputs.Count);
    database.Execute("DROP TRIGGER fail_placement;");
    Assert.True(store.Executions.StartRequest(identity, request));
    Assert.Equal(2, History(store, session).Count);
  }

  [Fact]
  public void ContextExcludesUnsentInputsAndRetainsFailedAssistantOutput() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Finish("never sent", startRequest: false, partial: null);
    Finish("attempted failed", startRequest: true, partial: "committed partial");
    Guid run = Guid.NewGuid();
    var identity = new ExecutionIdentity(session, run, 0);
    Admit(store, session, run, Input("current claim"));
    LlmRequest next = store.Executions.CreateNextRequest(identity)!;
    Assert.Equal(new[] { "attempted failed", "committed partial", "current claim" },
        next.Messages.Select(message => message.Text));
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User },
        next.Messages.Select(message => message.Role));

    void Finish(string text, bool startRequest, string? partial) {
      Guid execution = Guid.NewGuid();
      var executionIdentity = new ExecutionIdentity(session, execution, 0);
      Admit(store, session, execution, Input(text));
      Guid request = store.Executions.CreateNextRequest(executionIdentity)!.RequestId;
      if (startRequest)
        Assert.True(store.Executions.StartRequest(executionIdentity, request));
      if (partial is not null)
        Assert.True(store.LlmResponses.Append(executionIdentity, new(request, Guid.NewGuid(), 0, partial, false)));
      Assert.True(store.LlmResponses.CompleteRequest(executionIdentity, new(request, ExecutionOutcome.Failed, "failure")));
      Assert.NotNull(store.Executions.TryFinish(session, execution, ExecutionOutcome.Failed));
    }
  }

  [Fact]
  public void NotSentDiscoveryIsSparseBoundedPagedAndSurvivesLaterRunAndReload() {
    using var database = new TemporaryDatabase();
    Guid session, other;
    var expected = new List<Guid>();
    string text = new('x', 40000);
    using (UrdStore store = UrdStore.Open(database.Path)) {
      session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      other = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      for (int index = 0; index < 65; index++) {
        Guid run = Guid.NewGuid();
        var identity = new ExecutionIdentity(session, run, 0);
        UserMessageSubmission input = Input(text);
        expected.Add(input.SubmissionId);
        Admit(store, session, run, input);
        if (index % 2 == 0) {
          Guid request = store.Executions.CreateNextRequest(identity)!.RequestId;
          Assert.True(store.LlmResponses.CompleteRequest(identity, new(request, ExecutionOutcome.Failed)));
        } else {
          store.Executions.RequestStop(session, run, true);
        }
        Assert.NotNull(store.Executions.TryFinish(session, run, ExecutionOutcome.Cancelled));
      }
      Guid sent = Guid.NewGuid();
      var sentIdentity = new ExecutionIdentity(session, sent, 0);
      Admit(store, session, sent, Input("later sent run"));
      Guid sentRequest = store.Executions.CreateNextRequest(sentIdentity)!.RequestId;
      Assert.True(store.Executions.StartRequest(sentIdentity, sentRequest));
      Assert.True(store.LlmResponses.Append(sentIdentity, new(sentRequest, Guid.NewGuid(), 0, "", true)));
      Assert.True(store.LlmResponses.CompleteRequest(sentIdentity, new(sentRequest, ExecutionOutcome.Completed)));
      Assert.NotNull(store.Executions.TryFinish(session, sent, ExecutionOutcome.Completed));
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    NotSentPage page = reopened.NotSentInputs.Read(session, null);
    Assert.Equal(60, page.Inputs.Count);
    Assert.All(page.Inputs, input => Assert.Equal(text[..256], input.Preview));
    Assert.NotNull(page.NextCursor);
    NotSentPage tail = reopened.NotSentInputs.Read(session, page.NextCursor);
    Assert.Equal(5, tail.Inputs.Count);
    Assert.Null(tail.NextCursor);
    Assert.Equal(expected.AsEnumerable().Reverse(), page.Inputs.Concat(tail.Inputs).Select(input => input.SubmissionId));
    Assert.Equal(text, reopened.UserMessages.ReadReceipt(session, tail.Inputs[0].SubmissionId)!.Submission.Text);
    Assert.Empty(reopened.NotSentInputs.Read(other, page.NextCursor).Inputs);
    foreach (string invalid in new[] { "", "0", "-1", "1.0", "9223372036854775808" })
      Assert.Throws<ArgumentException>(() => reopened.NotSentInputs.Read(session, invalid));

    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand explain = connection.CreateCommand();
    explain.CommandText = "EXPLAIN QUERY PLAN " + NotSentInputReader.PageSql;
    explain.Parameters.AddWithValue("$session", session);
    explain.Parameters.AddWithValue("$cursor", long.MaxValue);
    using SqliteDataReader reader = explain.ExecuteReader();
    var plan = new List<string>();
    while (reader.Read())
      plan.Add(reader.GetString(3));
    Assert.Contains(plan, line => line.Contains("execution_inputs_not_sent_order", StringComparison.Ordinal));
    Assert.DoesNotContain(plan, line => line.Contains("SCAN i", StringComparison.Ordinal) || line.Contains("TEMP B-TREE", StringComparison.Ordinal));
  }

  private static UserMessageSubmission Input(string text) => new(Guid.NewGuid(), 0, text, TestLlmSelection.Default);

  private static void Admit(UrdStore store, Guid session, Guid run, UserMessageSubmission input) =>
      Assert.True(store.UserMessages.Submit(session, 0, input, true, run, run).NewlyAccepted);

  private static IReadOnlyList<TranscriptEntryDescriptor> History(UrdStore store, Guid session) =>
      store.History.Read(session, new(TranscriptHistoryMode.Latest, 0)).Entries;
}
