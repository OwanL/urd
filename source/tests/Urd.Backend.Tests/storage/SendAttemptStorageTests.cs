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
    UserMessageSubmission first = Input("first"), second = Input("second"), third = Input("third");
    Admit(store, session, run, first);
    Guid request = store.Executions.ClaimNextRequest(session, run, 0)!.RequestId;
    Assert.Empty(History(store, session));
    Assert.Equal(first.SubmissionId, Assert.Single(store.Snapshots.ReadStored(session).PendingInputs).SubmissionId);
    Assert.Null(store.Snapshots.ReadStored(session).FirstUserPreview);
    Assert.True(store.Executions.StartRequest(session, run, 0, request));
    Assert.True(store.Executions.StartRequest(session, run, 0, request));
    Assert.Single(History(store, session));
    Assert.Equal("first", store.Snapshots.ReadStored(session).FirstUserPreview);
    Assert.Empty(store.Snapshots.ReadStored(session).PendingInputs);
    Admit(store, session, run, second);
    Admit(store, session, run, third);
    Assert.Single(History(store, session));
    Assert.True(store.LlmResponses.Append(session, run, 0, new(request, Guid.NewGuid(), 0, "", true)));
    Assert.True(store.LlmResponses.CompleteRequest(session, run, 0, new(request, ExecutionOutcome.Completed)));
    Guid next = store.Executions.ClaimNextRequest(session, run, 0)!.RequestId;
    Assert.Equal(new[] { second.SubmissionId, third.SubmissionId },
        store.Snapshots.ReadStored(session).PendingInputs.Select(input => input.SubmissionId));
    Assert.False(store.Executions.StartRequest(Guid.NewGuid(), run, 0, next));
    Assert.False(store.Executions.StartRequest(session, run, 1, next));
    Assert.True(store.Executions.StartRequest(session, run, 0, next));
    Assert.Equal(new[] { first.SubmissionId, second.SubmissionId, third.SubmissionId },
        History(store, session).Where(entry => entry.Entry.Kind == TranscriptEntryKind.User).Select(entry => entry.Entry.EntryId));
    Assert.Equal(next.ToString("D").ToUpperInvariant(),
        ((string)database.Scalar($"SELECT request_id FROM execution_messages WHERE message_id = '{second.SubmissionId:D}';")!).ToUpperInvariant()); // raw-sql: verify transaction-time history placement
    store.Executions.RequestStop(session, run, true);
    Assert.False(store.Executions.StartRequest(session, run, 0, next));
    Assert.Equal(3, History(store, session).Count(entry => entry.Entry.Kind == TranscriptEntryKind.User));
  }

  [Fact]
  public void MarkerAndEveryAssignedEntryRollBackTogether() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    Admit(store, session, run, Input("one"));
    Admit(store, session, run, Input("two"));
    Guid request = store.Executions.ClaimNextRequest(session, run, 0)!.RequestId;
    database.Execute("""
      CREATE TRIGGER fail_placement BEFORE INSERT ON execution_messages
      WHEN (SELECT count(*) FROM execution_messages) = 1
      BEGIN SELECT RAISE(ABORT, 'second placement rejected'); END;
      """);
    Assert.Throws<SqliteException>(() => store.Executions.StartRequest(session, run, 0, request));
    Assert.Equal(1L, database.Scalar("SELECT attempt FROM execution_requests;")); // raw-sql: verify attempt marker rolls back with placements
    Assert.Empty(History(store, session));
    Assert.Equal(2, store.Snapshots.ReadStored(session).PendingInputs.Count);
    database.Execute("DROP TRIGGER fail_placement;");
    Assert.True(store.Executions.StartRequest(session, run, 0, request));
    Assert.Equal(2, History(store, session).Count);
  }

  [Fact]
  public void ContextExcludesNeverAttemptedButRetainsAttemptedFailuresAndLegacyUnknown() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Finish("never sent", attempt: false, partial: null);
    Finish("attempted failed", attempt: true, partial: "committed partial");
    Guid legacy = Finish("legacy unknown", attempt: false, partial: null);
    database.Execute($"UPDATE execution_requests SET attempt = 0 WHERE run_id = '{legacy:D}';");
    Guid run = Guid.NewGuid();
    Admit(store, session, run, Input("current claim"));
    LlmRequest next = store.Executions.ClaimNextRequest(session, run, 0)!;
    Assert.Equal(new[] { "attempted failed", "committed partial", "legacy unknown", "current claim" },
        next.Messages.Select(message => message.Text));
    Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.User },
        next.Messages.Select(message => message.Role));

    Guid Finish(string text, bool attempt, string? partial) {
      Guid execution = Guid.NewGuid();
      Admit(store, session, execution, Input(text));
      Guid request = store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId;
      if (attempt)
        Assert.True(store.Executions.StartRequest(session, execution, 0, request));
      if (partial is not null)
        Assert.True(store.LlmResponses.Append(session, execution, 0, new(request, Guid.NewGuid(), 0, partial, false)));
      Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, new(request, ExecutionOutcome.Failed, "failure")));
      Assert.NotNull(store.Executions.TrySettle(session, execution, ExecutionOutcome.Failed));
      return execution;
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
        UserMessageSubmission input = Input(text);
        expected.Add(input.SubmissionId);
        Admit(store, session, run, input);
        if (index % 2 == 0) {
          Guid request = store.Executions.ClaimNextRequest(session, run, 0)!.RequestId;
          Assert.True(store.LlmResponses.CompleteRequest(session, run, 0, new(request, ExecutionOutcome.Failed)));
        } else {
          store.Executions.RequestStop(session, run, true);
        }
        Assert.NotNull(store.Executions.TrySettle(session, run, ExecutionOutcome.Cancelled));
      }
      Guid sent = Guid.NewGuid();
      Admit(store, session, sent, Input("later sent run"));
      Guid sentRequest = store.Executions.ClaimNextRequest(session, sent, 0)!.RequestId;
      Assert.True(store.Executions.StartRequest(session, sent, 0, sentRequest));
      Assert.True(store.LlmResponses.Append(session, sent, 0, new(sentRequest, Guid.NewGuid(), 0, "", true)));
      Assert.True(store.LlmResponses.CompleteRequest(session, sent, 0, new(sentRequest, ExecutionOutcome.Completed)));
      Assert.NotNull(store.Executions.TrySettle(session, sent, ExecutionOutcome.Completed));
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
      Assert.True(store.UserMessages.Admit(session, 0, input, true, run, run).NewlyAccepted);

  private static IReadOnlyList<TranscriptEntryDescriptor> History(UrdStore store, Guid session) =>
      store.History.Read(session, new(TranscriptHistoryMode.Latest, 0)).Entries;
}
