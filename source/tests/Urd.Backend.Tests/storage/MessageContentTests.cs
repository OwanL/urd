using System.Text;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

// Bounded scalar-safe content paging: cursors, empty segments and completion facts.
public sealed class MessageContentTests {
  [Fact]
  public void FullSubmissionAndMultiPageOutputPreserveUnicodeNulAndScalarBoundaries() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    string submission = string.Concat(Enumerable.Repeat("🙂\0é", 2000));
    TestExecution execution = StartExecution(store, submission);
    string firstSegment = new('a', 16383);
    string secondSegment = "🙂\0é" + new string('z', 16376);
    Append(store, execution, 0, firstSegment);
    Append(store, execution, 1, secondSegment, final: true);

    MessageContentPage user = store.Content.Read(execution.Session, new(execution.User, 0, 0));
    Assert.Equal(submission, user.Text);
    Assert.True(user.Text.Length > 256);
    Assert.True(user.IsClosed);
    Assert.False(user.FinalOutputCommitted);
    Assert.False(user.HasMore);
    Assert.Equal(new(execution.User, 1, 0), user.Next);

    MessageContentPage page = store.Content.Read(execution.Session, new(execution.Assistant, 0, 0));
    Assert.Equal(firstSegment, page.Text); // The next four-byte scalar cannot fit the remaining byte.
    Assert.InRange(Encoding.UTF8.GetByteCount(page.Text), 1, 16384);
    Assert.Equal(new(execution.Assistant, 1, 0), page.Next);
    Assert.Equal(new(execution.Assistant, 2, 0), page.AvailableThrough);
    Assert.True(page.HasMore);
    Assert.True(page.IsClosed);
    Assert.True(page.FinalOutputCommitted);
    Assert.Null(store.Executions.ReadSettlement(execution.Session, execution.Execution)); // Final bytes are not settlement.

    MessageContentPage remainder = store.Content.Read(execution.Session, page.Next);
    Assert.Equal(secondSegment, remainder.Text);
    Assert.InRange(Encoding.UTF8.GetByteCount(remainder.Text), 1, 16384);
    Assert.Equal(firstSegment + secondSegment, page.Text + remainder.Text);
    Assert.Equal(remainder.AvailableThrough, remainder.Next);
    Assert.False(remainder.HasMore);
    Assert.Equal(2, StoredLlmResponses.Read(database).Count);
  }

  [Fact]
  public void ExactByteLimitNormalizesToNextSegmentWithoutReadingAnUnboundedBody() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    string block = new('é', 8192);
    Append(store, execution, 0, block);
    Append(store, execution, 1, "tail", final: true);

    MessageContentPage first = store.Content.Read(execution.Session, new(execution.Assistant, 0, 0));
    Assert.Equal(16384, Encoding.UTF8.GetByteCount(first.Text));
    Assert.Equal(block, first.Text);
    Assert.Equal(new(execution.Assistant, 1, 0), first.Next);
    Assert.True(first.HasMore);
    MessageContentPage next = store.Content.Read(execution.Session, first.Next);
    Assert.Equal("tail", next.Text);
    Assert.False(next.HasMore);
  }

  [Theory]
  [InlineData(256)]
  [InlineData(8192)]
  public void ExactTextBudgetsNormalizeAnEmptyFinalSegment(int utf16Units) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    string text = new('é', utf16Units);
    Append(store, execution, 0, text);
    Append(store, execution, 1, "", final: true);

    MessagePreview preview = Assert.Single(store.Snapshots.ReadStored(execution.Session).Messages,
        message => message.Message.Role == MessageRole.Assistant);
    Assert.Equal(text[..Math.Min(256, text.Length)], preview.Preview);
    Assert.Equal(utf16Units > 256, preview.Truncated);
    Assert.True(preview.FinalOutputCommitted);
    MessageContentPage body = store.Content.Read(execution.Session, new(execution.Assistant, 0, 0));
    Assert.Equal(text, body.Text);
    Assert.False(body.HasMore);
    Assert.Equal(new MessageContentCursor(execution.Assistant, 2, 0), body.Next);
    Assert.Equal(body.AvailableThrough, body.Next);
    Assert.True(body.FinalOutputCommitted);
    Assert.Null(store.Executions.ReadSettlement(execution.Session, execution.Execution));
  }

  [Fact]
  public void SegmentBudgetBoundsEvenEmptySegmentsAndContinuationMakesProgress() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    for (int index = 0; index < 16; index++)
      Append(store, execution, index, "");

    Append(store, execution, 16, "visible", final: true);
    MessageContentPage first = store.Content.Read(execution.Session, new(execution.Assistant, 0, 0));
    Assert.Equal("", first.Text);
    Assert.Equal(new(execution.Assistant, 16, 0), first.Next);
    Assert.True(first.HasMore);
    MessageContentPage next = store.Content.Read(execution.Session, first.Next);
    Assert.Equal("visible", next.Text);
    Assert.Equal(new(execution.Assistant, 17, 0), next.Next);
    Assert.False(next.HasMore);
  }

  [Fact]
  public void EmptyTailWaitsForAppendAndEmptyFinalAdvancesWithoutClaimingSettlement() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "partial\0🙂");
    MessageContentPage initial = store.Content.Read(execution.Session, new(execution.Assistant, 0, 0));
    Assert.False(initial.HasMore);
    Assert.False(initial.IsClosed);
    Assert.False(initial.FinalOutputCommitted);

    MessageContentPage tail = store.Content.Read(execution.Session, initial.Next);
    Assert.Equal("", tail.Text);
    Assert.Equal(tail.From, tail.Next);
    Assert.False(tail.HasMore);
    Assert.False(tail.IsClosed);

    Append(store, execution, 1, "", final: true);
    MessageContentPage final = store.Content.Read(execution.Session, tail.Next);
    Assert.Equal("", final.Text);
    Assert.Equal(new(execution.Assistant, 2, 0), final.Next);
    Assert.Equal(final.AvailableThrough, final.Next);
    Assert.True(final.IsClosed);
    Assert.True(final.FinalOutputCommitted);
    Assert.False(final.HasMore);
    Assert.Null(store.Executions.ReadSettlement(execution.Session, execution.Execution));
    MessageContentPage finalTail = store.Content.Read(execution.Session, final.Next);
    Assert.Equal(final.Next, finalTail.Next);
    Assert.Equal("", finalTail.Text);
  }

  [Fact]
  public void ACommittedEmptyMessageIsReadableAndDistinctFromMissingOutput() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store, "");
    Assert.Throws<KeyNotFoundException>(() => store.Content.Read(execution.Session, new(execution.Assistant, 0, 0)));
    MessageContentPage submission = store.Content.Read(execution.Session, new(execution.User, 0, 0));
    Assert.Equal("", submission.Text);
    Assert.Equal(new(execution.User, 1, 0), submission.Next);
    Assert.True(submission.IsClosed);

    Append(store, execution, 0, "", final: true);
    MessageContentPage output = store.Content.Read(execution.Session, new(execution.Assistant, 0, 0));
    Assert.Equal("", output.Text);
    Assert.Equal(new(execution.Assistant, 1, 0), output.Next);
    Assert.True(output.FinalOutputCommitted);
    Assert.True(output.IsClosed);
  }

  [Fact]
  public void CursorRequiresScalarBoundaryAndCannotSeekPastSegmentOrTail() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "a🙂\0é", final: true);
    MessageContentPage suffix = store.Content.Read(execution.Session, new(execution.Assistant, 0, 1));
    Assert.Equal("🙂\0é", suffix.Text);
    Assert.Equal(new(execution.Assistant, 1, 0), suffix.Next);
    MessageContentPage atEnd = store.Content.Read(execution.Session, new(execution.Assistant, 0, 8));
    Assert.Equal("", atEnd.Text);
    Assert.Equal(suffix.Next, atEnd.Next);

    Assert.All(new MessageContentCursor[] {
        new(execution.Assistant, -1, 0), new(execution.Assistant, 0, -1), new(execution.Assistant, 0, 2),
        new(execution.Assistant, 0, 9), new(execution.Assistant, 1, 1), new(execution.Assistant, 2, 0),
        new(execution.Assistant, 0, 16385)
    }, invalid => Assert.ThrowsAny<ArgumentException>(() => store.Content.Read(execution.Session, invalid)));
  }

  [Fact]
  public void ReferencesAreScopedToSessionExecutionRoleIdentityAndCurrentRevision() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "owned");
    Guid otherSession = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Assert.Throws<KeyNotFoundException>(() => store.Content.Read(otherSession, new(execution.Assistant, 0, 0)));
    Assert.Throws<KeyNotFoundException>(() => store.Content.Read(Guid.NewGuid(), new(execution.Assistant, 0, 0)));
    Assert.All(new MessageRef[] {
        execution.Assistant with { ExecutionId = Guid.NewGuid() },
        execution.Assistant with { MessageId = Guid.NewGuid() },
        execution.Assistant with { Role = MessageRole.User },
        execution.User with { Role = MessageRole.Assistant }
    }, missing => Assert.Throws<KeyNotFoundException>(() => store.Content.Read(execution.Session, new(missing, 0, 0))));

    Assert.Throws<ArgumentException>(() => store.Content.Read(execution.Session,
        new(execution.User with { Role = (MessageRole)42 }, 0, 0)));
    Assert.Throws<ArgumentException>(() => store.Content.Read(execution.Session,
        new(execution.User with { MessageId = Guid.Empty }, 0, 0)));
    database.ForceHistoryRevision(1);
    HistoryRevisionConflictException conflict = Assert.Throws<HistoryRevisionConflictException>(
        () => store.Content.Read(execution.Session, new(execution.Assistant, 0, 0)));
    Assert.Equal(0, conflict.ExpectedRevision);
    Assert.Equal(1, conflict.ActualRevision);
    Assert.Throws<KeyNotFoundException>(() => store.Content.Read(execution.Session,
        new(execution.Assistant with { HistoryRevision = 1 }, 0, 0)));
  }

  [Fact]
  public void SteeringAndDistinctResponsesKeepExactReferencesAndCursorsAcrossReopen() {
    using var database = new TemporaryDatabase();
    TestExecution first;
    MessageRef steering, secondOutput, undelivered;
    MessageContentCursor oldCursor;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      first = StartExecution(store, "origin\0🙂");
      steering = new(first.Execution, Guid.NewGuid(), MessageRole.User, 0);
      Assert.True(store.UserMessages.Admit(first.Session, 0, new(steering.MessageId, 0, "steering\0é"), true,
          Guid.NewGuid(), activeExecutionId: first.Execution).NewlyAccepted);
      MessageContentPage pending = store.Content.Read(first.Session, new(steering, 0, 0));
      Assert.Equal("steering\0é", pending.Text);
      Assert.True(pending.IsClosed);
      Assert.Equal(new(steering, 1, 0), pending.Next);

      Append(store, first, 0, "first🙂");
      oldCursor = store.Content.Read(first.Session, new(first.Assistant, 0, 0)).Next;
      Append(store, first, 1, "\0tail", final: true);
      Assert.True(store.LlmResponses.CompleteRequest(first.Session, first.Execution, 0,
          new(first.Request, ExecutionOutcome.Completed)));
      LlmRequest next = store.Executions.ClaimNextRequest(first.Session, first.Execution, 0)!;
      Assert.NotEqual(first.Request, next.RequestId);
      secondOutput = new(first.Execution, Guid.NewGuid(), MessageRole.Assistant, 0);
      Assert.True(store.LlmResponses.Append(first.Session, first.Execution, 0,
          new(next.RequestId, secondOutput.MessageId, 0, "second\0🙂", false)));
      undelivered = new(first.Execution, Guid.NewGuid(), MessageRole.User, 0);
      Assert.True(store.UserMessages.Admit(first.Session, 0, new(undelivered.MessageId, 0, "not delivered"), true,
          Guid.NewGuid(), activeExecutionId: first.Execution).NewlyAccepted);
      Assert.Equal(new[] { first.User, steering, first.Assistant, secondOutput, undelivered },
          store.Snapshots.ReadStored(first.Session).Messages.Select(message => message.Message));
      store.Executions.RequestStop(first.Session, first.Execution, ownsExecution: true);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    MessageContentPage retained = reopened.Content.Read(first.Session, oldCursor);
    Assert.Equal("\0tail", retained.Text);
    Assert.Equal(new(first.Assistant, 2, 0), retained.Next);
    Assert.True(retained.FinalOutputCommitted);
    Assert.True(retained.IsClosed);
    MessageContentPage second = reopened.Content.Read(first.Session, new(secondOutput, 0, 0));
    Assert.Equal("second\0🙂", second.Text);
    Assert.Equal(new(secondOutput, 1, 0), second.Next);
    Assert.False(second.FinalOutputCommitted);
    Assert.True(second.IsClosed);
    Assert.Equal("origin\0🙂", reopened.Content.Read(first.Session, new(first.User, 0, 0)).Text);
    Assert.Equal("steering\0é", reopened.Content.Read(first.Session, new(steering, 0, 0)).Text);
    Assert.Equal("not delivered", reopened.Content.Read(first.Session, new(undelivered, 0, 0)).Text);
    Assert.Null(reopened.Executions.ReadSettlement(first.Session, first.Execution));
  }

  [Fact]
  public void StopClosesPartialBytesWithoutFinalOutputOrTerminalOutcome() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "partial");
    store.Executions.RequestStop(execution.Session, execution.Execution, ownsExecution: true);
    MessageContentPage stopped = store.Content.Read(execution.Session, new(execution.Assistant, 0, 0));
    Assert.Equal("partial", stopped.Text);
    Assert.True(stopped.IsClosed);
    Assert.False(stopped.FinalOutputCommitted);
    Assert.Null(store.Executions.ReadSettlement(execution.Session, execution.Execution));
    Assert.Equal(ExecutionOutcome.Cancelled, store.Executions.TrySettle(execution.Session, execution.Execution, ExecutionOutcome.Cancelled)!.Outcome);
    Assert.True(store.Content.Read(execution.Session, stopped.Next).IsClosed);
  }

  [Fact]
  public void FailedRequestAndReopenedPartialMessageNeverInventFinalOutput() {
    using var database = new TemporaryDatabase();
    TestExecution execution;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      execution = StartExecution(store);
      Append(store, execution, 0, "retained partial");
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    MessageContentPage orphan = reopened.Content.Read(execution.Session, new(execution.Assistant, 0, 0));
    Assert.Equal("retained partial", orphan.Text);
    Assert.False(orphan.IsClosed);
    Assert.False(orphan.FinalOutputCommitted);
    Assert.True(reopened.LlmResponses.CompleteRequest(execution.Session, execution.Execution, 0,
        new(execution.Request, ExecutionOutcome.Failed, "failed")));
    MessageContentPage requestClosed = reopened.Content.Read(execution.Session, orphan.Next);
    Assert.True(requestClosed.IsClosed);
    Assert.False(requestClosed.FinalOutputCommitted);
    Assert.Null(reopened.Executions.ReadSettlement(execution.Session, execution.Execution));
    Assert.Equal(ExecutionOutcome.Failed, reopened.Executions.TrySettle(execution.Session, execution.Execution, ExecutionOutcome.Completed)!.Outcome);
  }

  [Fact]
  public void MissingOrInvalidCommittedSegmentsFailInsteadOfReturningAFalseTail() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "valid");
    database.Execute("UPDATE execution_output SET text_utf8 = X'80';");
    Assert.Throws<InvalidDataException>(() => store.Content.Read(execution.Session, new(execution.Assistant, 0, 0)));
    database.Execute("DELETE FROM execution_output;");
    Assert.Throws<InvalidDataException>(() => store.Content.Read(execution.Session, new(execution.Assistant, 0, 0)));
  }

  private static TestExecution StartExecution(UrdStore store, string text = "input") {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, text);
    Assert.True(store.UserMessages.Admit(session, 0, submission, true, execution).NewlyAccepted);
    LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    return new(session, execution, request.RequestId,
        new(execution, submission.SubmissionId, MessageRole.User, 0),
        new(execution, Guid.NewGuid(), MessageRole.Assistant, 0));
  }

  private static void Append(UrdStore store, TestExecution execution, int index, string text, bool final = false)
      => Assert.True(store.LlmResponses.Append(execution.Session, execution.Execution, 0,
          new(execution.Request, execution.Assistant.MessageId, index, text, final)));

  private sealed record TestExecution(Guid Session, Guid Execution, Guid Request, MessageRef User, MessageRef Assistant);
}
