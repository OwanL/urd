using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class ContinuationFlowTests {
  [Fact]
  public async Task ContinueUsesAttemptedContextAndKeepsItsReceiptAcrossEdits() {
    var continuationEntered = Signal();
    var continuationRelease = Signal();
    int call = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref call) switch {
      1 => Complete(token),
      2 => FailedAfterText(token),
      3 => FailedAfterText(token, "later partial failure tail"),
      4 => Paused(continuationEntered, continuationRelease, token),
      _ => Complete(token)
    });
    await using var fixture = new ExecutionFixture(client);

    Guid firstExecution = await fixture.StartExecutionAsync("first question");
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(firstExecution).WaitAsync(TestTimeout)).Outcome);
    SessionSnapshot firstSnapshot = await fixture.Session.ReadSnapshotAsync();
    Assert.True(firstSnapshot.CanContinue);

    using (UrdStore otherStore = UrdStore.Open(fixture.Database.Path)) {
      Guid freshSession = otherStore.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      Assert.Null(otherStore.Continuations.Start(freshSession, 0,
          new(Guid.NewGuid(), 0, firstExecution, TestLlmSelection.Default), accepting: true));
    }

    var failedInput = new UserMessageSubmission(Guid.NewGuid(), 0, "question that fails", TestLlmSelection.Default);
    UserMessageReceipt failedReceipt = await fixture.Session.SubmitUserMessageAsync(failedInput);
    Assert.Equal(ExecutionOutcome.Failed,
        (await fixture.Session.WaitForExecutionFinishAsync(failedReceipt.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);

    var laterFailedInput = new UserMessageSubmission(Guid.NewGuid(), 0, "another question that fails", TestLlmSelection.Default);
    UserMessageReceipt laterFailedReceipt = await fixture.Session.SubmitUserMessageAsync(laterFailedInput);
    Assert.Equal(ExecutionOutcome.Failed,
        (await fixture.Session.WaitForExecutionFinishAsync(laterFailedReceipt.ExecutionId!.Value).WaitAsync(TestTimeout)).Outcome);
    Assert.Contains(client.Requests[2], message =>
        message.Role == ChatRole.Assistant && message.Text.Contains("partial failure tail", StringComparison.Ordinal));
    using (UrdStore beforeContinue = UrdStore.Open(fixture.Database.Path)) {
      IReadOnlyList<ChatMessage> beforeContext = beforeContinue.Compactions.ReadContext(fixture.Session.Id, 0).ReadMessages();
      Assert.Contains(beforeContext, message => message.Text.Contains("partial failure tail", StringComparison.Ordinal));
      Assert.Contains(beforeContext, message => message.Text.Contains("later partial failure tail", StringComparison.Ordinal));
    }

    LlmSelection selection = TestLlmSelection.Default with { ModelId = "continue-model" };
    var staleTail = new ContinuationSubmission(Guid.NewGuid(), 0, firstExecution, selection);
    Assert.Null(await fixture.Session.ContinueAsync(staleTail));
    SessionSnapshot failedSnapshot = await fixture.Session.ReadSnapshotAsync();
    Assert.True(failedSnapshot.CanContinue);
    Assert.Equal(laterFailedReceipt.ExecutionId, failedSnapshot.LastSettlement!.ExecutionId);

    var submission = new ContinuationSubmission(Guid.NewGuid(), failedSnapshot.HistoryRevision,
        failedSnapshot.LastSettlement.ExecutionId, selection);
    ContinuationReceipt receipt = Assert.IsType<ContinuationReceipt>(await fixture.Session.ContinueAsync(submission));
    Assert.Equal(submission.OperationId, receipt.ExecutionId);
    Assert.Equal(1L, fixture.Database.Scalar($"""
      SELECT continuation_omitted_request_id = (
          SELECT request_id FROM execution_requests WHERE run_id = '{laterFailedReceipt.ExecutionId.Value:D}'
          ORDER BY request_index DESC LIMIT 1)
      FROM execution_runs WHERE run_id = '{receipt.ExecutionId:D}';
      """)); // raw-sql: Continue persists the exact failed assistant request identity.
    await continuationEntered.Task.WaitAsync(TestTimeout);
    Assert.Equal(0L, fixture.Database.Scalar( // raw-sql: a Continue run has no synthetic user input
        $"SELECT COUNT(*) FROM execution_inputs WHERE run_id = '{receipt.ExecutionId:D}';"));

    IReadOnlyList<ChatMessage> context = client.Requests[3];
    Assert.Equal(new[] { "first question", "question that fails", laterFailedInput.Text },
        context.Where(message => message.Role == ChatRole.User).Select(message => message.Text));
    Assert.Contains(context, message => message.Role == ChatRole.Assistant && message.Text == "done");
    Assert.Contains(context, message => message.Role == ChatRole.Assistant
        && message.Text.Contains("partial failure tail", StringComparison.Ordinal));
    Assert.DoesNotContain(context, message => message.Role == ChatRole.Assistant
        && message.Text.Contains("later partial failure tail", StringComparison.Ordinal));
    Assert.DoesNotContain(context, message => message.Role == ChatRole.User && message.Text.Contains("continue", StringComparison.OrdinalIgnoreCase));
    Assert.Equal(selection, (await fixture.Session.ReadSnapshotAsync()).LatestLlmSelection);
    Assert.False((await fixture.Session.ReadSnapshotAsync()).CanContinue);

    var steering = new UserMessageSubmission(Guid.NewGuid(), 0, "steer the continuation", selection);
    UserMessageReceipt accepted = await fixture.Session.SubmitUserMessageAsync(steering);
    Assert.Equal(receipt.ExecutionId, accepted.ExecutionId);
    UserMessageReceipt locked = await fixture.Session.SubmitUserMessageAsync(new(
        Guid.NewGuid(), 0, "different model", TestLlmSelection.Default));
    Assert.Equal(MessageRefusalReason.SelectionLocked, locked.RefusalReason);
    continuationRelease.TrySetResult();
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(receipt.ExecutionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(new[] { "first question", "question that fails", laterFailedInput.Text, steering.Text },
        client.Requests[4].Where(message => message.Role == ChatRole.User).Select(message => message.Text));
    Assert.Contains(client.Requests[4], message =>
        message.Role == ChatRole.Assistant && message.Text == "visible");
    Assert.Contains(client.Requests[4], message =>
        message.Text.Contains("partial failure tail", StringComparison.Ordinal));
    Assert.DoesNotContain(client.Requests[4], message =>
        message.Text.Contains("later partial failure tail", StringComparison.Ordinal));
    Assert.Equal("continue-model", client.Options[3]!.ModelId);
    Assert.Equal("continue-model", client.Options[4]!.ModelId);

    Assert.Equal(receipt, await fixture.Session.ContinueAsync(submission));
    Assert.Equal(5, client.Calls);
    await Assert.ThrowsAsync<UserMessageConflictException>(async () => {
      await fixture.Session.ContinueAsync(submission with { ExpectedLastExecutionId = firstExecution });
    });

    using UrdStore reopened = UrdStore.Open(fixture.Database.Path);
    Assert.Equal(receipt, reopened.Continuations.ReadReceipt(fixture.Session.Id, submission.OperationId));
    IReadOnlyList<ChatMessage> reopenedContext = reopened.Compactions.ReadContext(fixture.Session.Id, 0).ReadMessages();
    Assert.Contains(reopenedContext, message => message.Role == ChatRole.Assistant && message.Text == "done");
    Assert.Contains(reopenedContext, message => message.Role == ChatRole.User && message.Text == failedInput.Text);
    Assert.Contains(reopenedContext, message => message.Text.Contains("partial failure tail", StringComparison.Ordinal));
    Assert.DoesNotContain(reopenedContext, message =>
        message.Text.Contains("later partial failure tail", StringComparison.Ordinal));

    TranscriptHistoryPage history = await fixture.Session.Transcript.ReadHistoryAsync(
        new(TranscriptHistoryMode.Latest, 0));
    Assert.Contains(history.Entries, entry => entry.Entry.ExecutionId == failedReceipt.ExecutionId
        && entry.Entry.Kind == TranscriptEntryKind.Text);
    Assert.Contains(history.Entries, entry => entry.Entry.ExecutionId == laterFailedReceipt.ExecutionId
        && entry.Entry.Kind == TranscriptEntryKind.Text);
    MessageRef firstPrompt = history.Entries.First(entry => entry.Entry.Kind == TranscriptEntryKind.User)
        .Text!.From.Message;
    MessageEditReceipt edit = await fixture.Session.EditUserMessageAsync(
        new(Guid.NewGuid(), firstPrompt, "edited first question", TestLlmSelection.Default));
    await fixture.Session.WaitForExecutionFinishAsync(edit.ExecutionId).WaitAsync(TestTimeout);
    Assert.Equal(receipt, reopened.Continuations.ReadReceipt(fixture.Session.Id, submission.OperationId));
    Assert.Equal(receipt, await fixture.Session.ContinueAsync(submission));
    Assert.Equal(6, client.Calls);
  }

  [Fact]
  public void ContinueKeepsTheAssistantCallWhenTheActualTailIsAFailedToolResult() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid execution = Guid.NewGuid();
    var identity = new ExecutionIdentity(session, execution, 0);
    Assert.True(store.UserMessages.Submit(session, 0,
        new(Guid.NewGuid(), 0, "request", TestLlmSelection.Default), true, execution).NewlyAccepted);
    LlmRequest first = store.Executions.CreateNextRequest(identity)!;
    Assert.True(store.Executions.StartRequest(identity, first.RequestId));
    Assert.True(store.LlmResponses.Append(identity, new(first.RequestId, Guid.NewGuid(), 0, "", true)));
    Assert.True(store.LlmResponses.CompleteRequest(identity,
        new(first.RequestId, ExecutionOutcome.Completed, ToolCalls: [new("failed-call", "fake",
            JsonSerializer.SerializeToElement(new { value = "test" }))])));
    Assert.True(store.Tools.TryStart(identity, first.RequestId, "failed-call"));
    Assert.True(store.Tools.Complete(identity, first.RequestId,
        new("failed-call", ExecutionOutcome.Failed, new ToolOutput("tool failed", [], IsError: true))));
    Assert.NotNull(store.Executions.CreateNextRequest(identity));
    store.Executions.RequestStop(session, execution, ownsExecution: true);
    Assert.Equal(ExecutionOutcome.Cancelled,
        store.Executions.TryFinish(session, execution, ExecutionOutcome.Cancelled)!.Outcome);

    Guid continuation = Guid.NewGuid();
    Assert.NotNull(store.Continuations.Start(session, 0,
        new(continuation, 0, execution, TestLlmSelection.Default), accepting: true));
    Assert.Equal(1L, database.Scalar($"SELECT continuation_omitted_request_id IS NULL FROM execution_runs WHERE run_id = '{continuation:D}';")); // raw-sql: a tool-result tail must not record an assistant omission.
    var continuationIdentity = new ExecutionIdentity(session, continuation, 0);
    LlmRequest continued = store.Executions.CreateNextRequest(continuationIdentity)!;
    ChatMessage assistant = Assert.Single(continued.Messages, message => message.Role == ChatRole.Assistant);
    Assert.Contains(assistant.Contents, content => content is FunctionCallContent call && call.CallId == "failed-call");
    ChatMessage result = Assert.Single(continued.Messages, message => message.Role == ChatRole.Tool);
    FunctionResultContent savedResult = Assert.Single(result.Contents.OfType<FunctionResultContent>());
    Assert.Contains("tool failed", Assert.IsType<string>(savedResult.Result), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(ExecutionOutcome.Failed)]
  [InlineData(ExecutionOutcome.Cancelled)]
  [InlineData(ExecutionOutcome.Completed)]
  public void ContinueDistinguishesObservedIncompleteCallsFromAuthorizedUnstartedCalls(ExecutionOutcome responseOutcome) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid execution = Guid.NewGuid(), response = Guid.NewGuid();
    var identity = new ExecutionIdentity(session, execution, 0);
    Assert.True(store.UserMessages.Submit(session, 0,
        new(Guid.NewGuid(), 0, "request", TestLlmSelection.Default), true, execution).NewlyAccepted);
    LlmRequest first = store.Executions.CreateNextRequest(identity)!;
    Assert.True(store.Executions.StartRequest(identity, first.RequestId));
    Assert.True(store.LlmResponses.Append(identity,
        new(first.RequestId, response, 0, "assistant text", false)));
    Assert.True(store.LlmResponses.RecordToolCall(identity, first.RequestId, "observed", "fake"));
    bool authorized = responseOutcome == ExecutionOutcome.Completed;
    if (authorized)
      Assert.True(store.LlmResponses.Append(identity,
          new(first.RequestId, Guid.NewGuid(), 1, "", true)));
    Assert.True(store.LlmResponses.CompleteRequest(identity,
        new(first.RequestId, responseOutcome, ToolCalls: authorized
            ? [new("observed", "fake", JsonSerializer.SerializeToElement(new { }))] : null)));
    if (authorized)
      store.Executions.RequestStop(session, execution, ownsExecution: true);
    Assert.NotNull(store.Executions.TryFinish(session, execution,
        authorized ? ExecutionOutcome.Cancelled : responseOutcome));
    Assert.Equal(authorized ? 1 : 0, StoredTools.Read(database).Count);

    Guid continuation = Guid.NewGuid();
    Assert.NotNull(store.Continuations.Start(session, 0,
        new(continuation, 0, execution, TestLlmSelection.Default), accepting: true));
    var continuationIdentity = new ExecutionIdentity(session, continuation, 0);
    LlmRequest continued = store.Executions.CreateNextRequest(continuationIdentity)!;
    Assert.Equal("request", Assert.Single(continued.Messages, message => message.Role == ChatRole.User).Text);
    if (authorized) {
      ChatMessage assistant = Assert.Single(continued.Messages, message => message.Role == ChatRole.Assistant);
      Assert.Contains(assistant.Contents, content => content is FunctionCallContent call && call.CallId == "observed");
      ChatMessage result = Assert.Single(continued.Messages, message => message.Role == ChatRole.Tool);
      Assert.Contains("Tool was not executed", Assert.IsType<string>(
          Assert.Single(result.Contents.OfType<FunctionResultContent>()).Result), StringComparison.Ordinal);
    } else {
      Assert.Single(continued.Messages);
    }
    TranscriptHistoryPage history = store.History.Read(session, new(TranscriptHistoryMode.Latest, 0));
    Assert.Contains(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.Text);
    Assert.False(Assert.Single(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.Tool).Tool!.Started);
  }

  [Fact]
  public async Task PendingInputRemovalOnlyWinsBeforeClaimAndIsNotReportedNotSent() {
    var firstEntered = Signal();
    var firstRelease = Signal();
    var secondEntered = Signal();
    var secondRelease = Signal();
    int call = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref call) switch {
      1 => Paused(firstEntered, firstRelease, token),
      2 => Paused(secondEntered, secondRelease, token),
      _ => Complete(token)
    });
    await using var fixture = new ExecutionFixture(client);

    Guid executionId = await fixture.StartExecutionAsync("initial");
    await firstEntered.Task.WaitAsync(TestTimeout);
    var removed = new UserMessageSubmission(Guid.NewGuid(), 0, "remove before claim", TestLlmSelection.Default);
    Assert.Equal(MessageAcceptance.Accepted, (await fixture.Session.SubmitUserMessageAsync(removed)).Acceptance);
    PendingInputPreview pending = Assert.Single((await fixture.Session.ReadSnapshotAsync()).PendingInputs);
    Assert.Equal(removed.SubmissionId, pending.SubmissionId);
    Assert.True(pending.CanRemove);
    Assert.True(await fixture.Session.RemovePendingInputAsync(executionId, removed.SubmissionId));
    Assert.Empty((await fixture.Session.ReadSnapshotAsync()).PendingInputs);
    Assert.Empty((await fixture.Session.ReadNotSentInputsAsync()).Inputs);
    Assert.False((await fixture.Session.ReadSnapshotAsync()).StopRequested);
    Assert.Equal(1, client.Calls);

    var claimed = new UserMessageSubmission(Guid.NewGuid(), 0, "already claimed", TestLlmSelection.Default);
    Assert.Equal(MessageAcceptance.Accepted, (await fixture.Session.SubmitUserMessageAsync(claimed)).Acceptance);
    firstRelease.TrySetResult();
    await secondEntered.Task.WaitAsync(TestTimeout);
    Assert.False(await fixture.Session.RemovePendingInputAsync(executionId, claimed.SubmissionId));
    Assert.DoesNotContain((await fixture.Session.ReadSnapshotAsync()).PendingInputs,
        input => input.SubmissionId == claimed.SubmissionId && input.CanRemove);
    secondRelease.TrySetResult();
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Empty((await fixture.Session.ReadNotSentInputsAsync()).Inputs);
    Assert.Equal(new[] { "initial", claimed.Text },
        client.Requests[1].Where(message => message.Role == ChatRole.User).Select(message => message.Text));
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> FailedAfterText(
      [EnumeratorCancellation] CancellationToken cancellation, string text = "partial failure tail") {
    yield return Text(text);
    await Task.Yield();
    cancellation.ThrowIfCancellationRequested();
    throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
  }
}
