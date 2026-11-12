using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class MessageEditingTests {
  [Fact]
  public async Task SaveJoinsToolCleanupFencesInputAndSharesConcurrentRetries() {
    var entered = Signal();
    var cancelled = Signal();
    var release = Signal();
    int effects = 0;
    async Task<string> Step(CancellationToken token) {
      using var registration = token.Register(() => cancelled.TrySetResult());
      Interlocked.Increment(ref effects);
      entered.TrySetResult();
      await release.Task;
      return "old result";
    }
    int calls = 0;
    using var client = new ScriptedChatClient(token => Interlocked.Increment(ref calls) == 1
        ? ToolBatch(token, new FunctionCallContent("call", "step", new Dictionary<string, object?>())) : Complete(token));
    await using var fixture = new ExecutionFixture(client, [AIFunctionFactory.Create(Step, name: "step")]);
    Guid oldRun = await fixture.StartExecutionAsync("old message");
    try {
      await entered.Task.WaitAsync(TestTimeout);
      var history = await fixture.Session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
      var user = Assert.Single(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.User);
      var target = user.Text!.From.Message;
      var selected = TestLlmSelection.Default with { ReasoningEffort = "high" };
      var edit = new MessageEditSubmission(Guid.NewGuid(), target, "replacement", selected);
      await Assert.ThrowsAsync<HistoryRevisionConflictException>(() => fixture.Session.EditUserMessageAsync(
          edit with { Message = target with { HistoryRevision = 1 } }));
      Assert.False(cancelled.Task.IsCompleted);

      Task<MessageEditReceipt> save = fixture.Session.EditUserMessageAsync(edit);
      Task<MessageEditReceipt> retry = fixture.Session.EditUserMessageAsync(edit);
      await cancelled.Task.WaitAsync(TestTimeout);
      Assert.False(save.IsCompleted);
      Assert.False(retry.IsCompleted);
      Assert.Equal(1, client.Calls);
      Assert.True((await fixture.Session.ReadSnapshotAsync()).AdmissionFenced);
      Assert.Equal(MessageRefusalReason.Stopping, (await fixture.Session.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, "competing input", selected))).RefusalReason);
      Assert.Null(await fixture.Session.ReadMessageEditReceiptAsync(edit.OperationId));
      release.TrySetResult();
      MessageEditReceipt receipt = await save.WaitAsync(TestTimeout);
      Assert.Equal(receipt, await retry.WaitAsync(TestTimeout));
      await fixture.Session.WaitForSettlementAsync(receipt.ExecutionId).WaitAsync(TestTimeout);
      Assert.Equal(ExecutionOutcome.Cancelled, (await fixture.Session.WaitForSettlementAsync(oldRun)).Outcome);
      Assert.Equal(selected, (await fixture.Session.ReadSnapshotAsync()).LatestLlmSelection);
      Assert.Equal(new[] { "replacement" }, Users(client.LastRequest));
      Assert.Equal(1, effects);
      Assert.Empty(StoredTools.Read(fixture.Database));
      Assert.Equal(receipt, await fixture.Session.EditUserMessageAsync(edit));
      Assert.Equal(2, client.Calls);
      await Assert.ThrowsAsync<UserMessageConflictException>(() => fixture.Session.EditUserMessageAsync(edit with { Text = "different" }));
      await Assert.ThrowsAsync<HistoryRevisionConflictException>(() => fixture.Session.Transcript.ReadContentAsync(user.Text.From));
    } finally {
      release.TrySetResult();
    }
  }

  [Fact]
  public async Task ShutdownJoinsAnAdmittedEditWithoutStartingItsReplacement() {
    var cleanup = Signal();
    var release = Signal();
    using var client = new ScriptedChatClient(token => new HeldDisposal(Complete(token), cleanup, release.Task));
    await using var fixture = new ExecutionFixture(client);
    await fixture.StartExecutionAsync("original");
    try {
      await cleanup.Task.WaitAsync(TestTimeout);
      var history = await fixture.Session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
      var edit = new MessageEditSubmission(Guid.NewGuid(), history.Entries[0].Text!.From.Message, "replacement", TestLlmSelection.Default);
      Task<MessageEditReceipt> save = fixture.Session.EditUserMessageAsync(edit);
      await fixture.Session.FenceUserMessagesAsync();
      Task shutdown = fixture.Session.StopExecutionAsync();
      Assert.False(save.IsCompleted);
      Assert.False(shutdown.IsCompleted);
      release.TrySetResult();
      await shutdown.WaitAsync(TestTimeout);
      await Assert.ThrowsAsync<UserMessageConflictException>(() => save.WaitAsync(TestTimeout));
      Assert.Equal(0, (await fixture.Session.ReadSnapshotAsync()).HistoryRevision);
      Assert.Null(await fixture.Session.ReadMessageEditReceiptAsync(edit.OperationId));
      Assert.Equal(1, client.Calls);
    } finally {
      release.TrySetResult();
    }
  }

  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  [InlineData(2)]
  public async Task FirstMiddleAndLatestEditsKeepOnlyThePrefixAndSurviveReopen(int index) {
    using var database = new TemporaryDatabase();
    int round = 0;
    using var client = new ScriptedChatClient(token => TextUpdates([$"reply-{Interlocked.Increment(ref round)}"], token));
    MessageEditSubmission edit;
    MessageEditReceipt receipt;
    Guid sessionId;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var manager = new SessionManager(store, _ => client);
      try {
        PrimarySession session = await manager.CreatePrimaryAsync();
        sessionId = session.Id;
        var inputs = new List<UserMessageSubmission>();
        for (int number = 0; number < 3; number++) {
          var input = new UserMessageSubmission(Guid.NewGuid(), 0, $"user-{number}", TestLlmSelection.Default);
          inputs.Add(input);
          var accepted = await session.SubmitUserMessageAsync(input);
          await session.WaitForSettlementAsync(accepted.ExecutionId!.Value).WaitAsync(TestTimeout);
        }
        var page = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
        MessageRef target = page.Entries.Where(entry => entry.Entry.Kind == TranscriptEntryKind.User).ElementAt(index).Text!.From.Message;
        edit = new(Guid.NewGuid(), target, "  edited 雪🙂\0  ", TestLlmSelection.Default);
        receipt = await session.EditUserMessageAsync(edit).WaitAsync(TestTimeout);
        await session.WaitForSettlementAsync(receipt.ExecutionId).WaitAsync(TestTimeout);
        Assert.Equal(1, receipt.HistoryRevision);
        Assert.Equal(inputs.Take(index).Select(input => input.Text).Append(edit.Text), Users(client.LastRequest));
        Assert.Equal(index, client.LastRequest.Count(message => message.Role == ChatRole.Assistant));
        var current = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 1));
        Assert.Equal((index + 1) * 2, current.Entries.Count);
        Assert.Equal(current.Entries.Count, (await session.Transcript.ReadPositionSummaryAsync(1)).EntryCount);
        Guid removedRequest = Assert.Single(StoredExecutions.ReadRequests(database), row => row.ExecutionId == target.ExecutionId).RequestId;
        Assert.Empty(StoredLlmResponses.Read(database, removedRequest));
        foreach (var removed in inputs.Skip(index))
          await Assert.ThrowsAsync<UserMessageConflictException>(() => session.GetUserMessageReceiptAsync(removed.SubmissionId));
        Assert.False((await session.ReadSnapshotAsync()).AdmissionFenced);
      } finally {
        await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      }
    }
    int before = client.Calls;
    using UrdStore reopened = UrdStore.Open(database.Path);
    reopened.RecoverInterruptedExecutions();
    var restored = new SessionManager(reopened, _ => client);
    try {
      PrimarySession session = await restored.GetPrimaryAsync(sessionId);
      Assert.Equal(receipt, await session.ReadMessageEditReceiptAsync(edit.OperationId));
      Assert.Equal(receipt, await session.EditUserMessageAsync(edit));
      Assert.Equal(before, client.Calls);
      var next = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 1, "continue", TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Accepted, next.Acceptance);
      await session.WaitForSettlementAsync(next.ExecutionId!.Value).WaitAsync(TestTimeout);
      Assert.Equal(edit.Text, Users(client.LastRequest)[^2]);
    } finally {
      await restored.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task ReplacementAndItsReceiptRollBackTogetherOnWriteFailure() {
    using var client = new ScriptedChatClient(Complete);
    await using var fixture = new ExecutionFixture(client);
    Guid run = await fixture.StartExecutionAsync("keep me");
    await fixture.Session.WaitForSettlementAsync(run).WaitAsync(TestTimeout);
    var before = await fixture.Session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
    MessageRef target = before.Entries[0].Text!.From.Message;
    fixture.Database.Execute("""
      CREATE TRIGGER reject_edit BEFORE INSERT ON message_edits
      BEGIN SELECT RAISE(ABORT, 'Injected edit receipt failure'); END;
      """);
    var edit = new MessageEditSubmission(Guid.NewGuid(), target, "replacement", TestLlmSelection.Default);
    await Assert.ThrowsAsync<SqliteException>(() => fixture.Session.EditUserMessageAsync(edit));
    var after = await fixture.Session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
    Assert.Equal(before.Entries.Select(entry => entry.Entry), after.Entries.Select(entry => entry.Entry));
    Assert.Equal("keep me", (await fixture.Session.Transcript.ReadContentAsync(before.Entries[0].Text!.From)).Text);
    Assert.Null(await fixture.Session.ReadMessageEditReceiptAsync(edit.OperationId));
    Assert.True((await fixture.Session.ReadSnapshotAsync()).AdmissionFenced);
    Assert.Equal(1, client.Calls);
  }

  private static string[] Users(IEnumerable<ChatMessage> messages) =>
      messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text).ToArray();
}
