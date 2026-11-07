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
  public void FullInputAndMultiPageOutputPreserveUnicodeNulAndScalarBoundaries() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    string input = string.Concat(Enumerable.Repeat("🙂\0é", 2000));
    TestRun run = StartRun(store, input);
    string firstSegment = new('a', 16383);
    string secondSegment = "🙂\0é" + new string('z', 16376);
    Append(store, run, 0, firstSegment);
    Append(store, run, 1, secondSegment, final: true);

    MessageContentPage user = store.Content.Read(run.Session, new(run.User, 0, 0));
    Assert.Equal(input, user.Text);
    Assert.True(user.Text.Length > 256);
    Assert.True(user.IsClosed);
    Assert.False(user.FinalOutputCommitted);
    Assert.False(user.HasMore);
    Assert.Equal(new(run.User, 1, 0), user.Next);

    MessageContentPage page = store.Content.Read(run.Session, new(run.Assistant, 0, 0));
    Assert.Equal(firstSegment, page.Text); // The next four-byte scalar cannot fit the remaining byte.
    Assert.InRange(Encoding.UTF8.GetByteCount(page.Text), 1, 16384);
    Assert.Equal(new(run.Assistant, 1, 0), page.Next);
    Assert.Equal(new(run.Assistant, 2, 0), page.AvailableThrough);
    Assert.True(page.HasMore);
    Assert.True(page.IsClosed);
    Assert.True(page.FinalOutputCommitted);
    Assert.Null(store.Runs.ReadSettlement(run.Session, run.Run)); // Final bytes are not settlement.

    MessageContentPage remainder = store.Content.Read(run.Session, page.Next);
    Assert.Equal(secondSegment, remainder.Text);
    Assert.InRange(Encoding.UTF8.GetByteCount(remainder.Text), 1, 16384);
    Assert.Equal(firstSegment + secondSegment, page.Text + remainder.Text);
    Assert.Equal(remainder.AvailableThrough, remainder.Next);
    Assert.False(remainder.HasMore);
    Assert.Equal(2, StoredOutput.Read(database).Count);
  }

  [Fact]
  public void ExactByteLimitNormalizesToNextSegmentWithoutReadingAnUnboundedBody() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    TestRun run = StartRun(store);
    string block = new('é', 8192);
    Append(store, run, 0, block);
    Append(store, run, 1, "tail", final: true);

    MessageContentPage first = store.Content.Read(run.Session, new(run.Assistant, 0, 0));
    Assert.Equal(16384, Encoding.UTF8.GetByteCount(first.Text));
    Assert.Equal(block, first.Text);
    Assert.Equal(new(run.Assistant, 1, 0), first.Next);
    Assert.True(first.HasMore);
    MessageContentPage next = store.Content.Read(run.Session, first.Next);
    Assert.Equal("tail", next.Text);
    Assert.False(next.HasMore);
  }

  [Theory]
  [InlineData(256)]
  [InlineData(8192)]
  public void ExactTextBudgetsNormalizeAnEmptyFinalSegment(int utf16Units) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    TestRun run = StartRun(store);
    string text = new('é', utf16Units);
    Append(store, run, 0, text);
    Append(store, run, 1, "", final: true);

    MessagePreview preview = Assert.Single(store.Snapshots.Read(run.Session).Messages,
        message => message.Message.Role == MessageRole.Assistant);
    Assert.Equal(text[..Math.Min(256, text.Length)], preview.Preview);
    Assert.Equal(utf16Units > 256, preview.Truncated);
    Assert.True(preview.FinalOutputCommitted);
    MessageContentPage body = store.Content.Read(run.Session, new(run.Assistant, 0, 0));
    Assert.Equal(text, body.Text);
    Assert.False(body.HasMore);
    Assert.Equal(new MessageContentCursor(run.Assistant, 2, 0), body.Next);
    Assert.Equal(body.AvailableThrough, body.Next);
    Assert.True(body.FinalOutputCommitted);
    Assert.Null(store.Runs.ReadSettlement(run.Session, run.Run));
  }

  [Fact]
  public void SegmentBudgetBoundsEvenEmptySegmentsAndContinuationMakesProgress() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    TestRun run = StartRun(store);
    for (int index = 0; index < 16; index++)
      Append(store, run, index, "");

    Append(store, run, 16, "visible", final: true);
    MessageContentPage first = store.Content.Read(run.Session, new(run.Assistant, 0, 0));
    Assert.Equal("", first.Text);
    Assert.Equal(new(run.Assistant, 16, 0), first.Next);
    Assert.True(first.HasMore);
    MessageContentPage next = store.Content.Read(run.Session, first.Next);
    Assert.Equal("visible", next.Text);
    Assert.Equal(new(run.Assistant, 17, 0), next.Next);
    Assert.False(next.HasMore);
  }

  [Fact]
  public void EmptyTailWaitsForAppendAndEmptyFinalAdvancesWithoutClaimingSettlement() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    TestRun run = StartRun(store);
    Append(store, run, 0, "partial\0🙂");
    MessageContentPage initial = store.Content.Read(run.Session, new(run.Assistant, 0, 0));
    Assert.False(initial.HasMore);
    Assert.False(initial.IsClosed);
    Assert.False(initial.FinalOutputCommitted);

    MessageContentPage tail = store.Content.Read(run.Session, initial.Next);
    Assert.Equal("", tail.Text);
    Assert.Equal(tail.From, tail.Next);
    Assert.False(tail.HasMore);
    Assert.False(tail.IsClosed);

    Append(store, run, 1, "", final: true);
    MessageContentPage final = store.Content.Read(run.Session, tail.Next);
    Assert.Equal("", final.Text);
    Assert.Equal(new(run.Assistant, 2, 0), final.Next);
    Assert.Equal(final.AvailableThrough, final.Next);
    Assert.True(final.IsClosed);
    Assert.True(final.FinalOutputCommitted);
    Assert.False(final.HasMore);
    Assert.Null(store.Runs.ReadSettlement(run.Session, run.Run));
    MessageContentPage finalTail = store.Content.Read(run.Session, final.Next);
    Assert.Equal(final.Next, finalTail.Next);
    Assert.Equal("", finalTail.Text);
  }

  [Fact]
  public void ACommittedEmptyMessageIsReadableAndDistinctFromMissingOutput() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    TestRun run = StartRun(store, "");
    Assert.Throws<KeyNotFoundException>(() => store.Content.Read(run.Session, new(run.Assistant, 0, 0)));
    MessageContentPage input = store.Content.Read(run.Session, new(run.User, 0, 0));
    Assert.Equal("", input.Text);
    Assert.Equal(new(run.User, 1, 0), input.Next);
    Assert.True(input.IsClosed);

    Append(store, run, 0, "", final: true);
    MessageContentPage output = store.Content.Read(run.Session, new(run.Assistant, 0, 0));
    Assert.Equal("", output.Text);
    Assert.Equal(new(run.Assistant, 1, 0), output.Next);
    Assert.True(output.FinalOutputCommitted);
    Assert.True(output.IsClosed);
  }

  [Fact]
  public void CursorRequiresScalarBoundaryAndCannotSeekPastSegmentOrTail() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    TestRun run = StartRun(store);
    Append(store, run, 0, "a🙂\0é", final: true);
    MessageContentPage suffix = store.Content.Read(run.Session, new(run.Assistant, 0, 1));
    Assert.Equal("🙂\0é", suffix.Text);
    Assert.Equal(new(run.Assistant, 1, 0), suffix.Next);
    MessageContentPage atEnd = store.Content.Read(run.Session, new(run.Assistant, 0, 8));
    Assert.Equal("", atEnd.Text);
    Assert.Equal(suffix.Next, atEnd.Next);

    Assert.All(new MessageContentCursor[] {
        new(run.Assistant, -1, 0), new(run.Assistant, 0, -1), new(run.Assistant, 0, 2),
        new(run.Assistant, 0, 9), new(run.Assistant, 1, 1), new(run.Assistant, 2, 0),
        new(run.Assistant, 0, 16385)
    }, invalid => Assert.ThrowsAny<ArgumentException>(() => store.Content.Read(run.Session, invalid)));
  }

  [Fact]
  public void ReferencesAreScopedToSessionRunRoleIdentityAndCurrentRevision() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    TestRun run = StartRun(store);
    Append(store, run, 0, "owned");
    Guid otherSession = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Assert.Throws<KeyNotFoundException>(() => store.Content.Read(otherSession, new(run.Assistant, 0, 0)));
    Assert.Throws<KeyNotFoundException>(() => store.Content.Read(Guid.NewGuid(), new(run.Assistant, 0, 0)));
    Assert.All(new MessageRef[] {
        run.Assistant with { RunId = Guid.NewGuid() },
        run.Assistant with { MessageId = Guid.NewGuid() },
        run.Assistant with { Role = MessageRole.User },
        run.User with { Role = MessageRole.Assistant }
    }, missing => Assert.Throws<KeyNotFoundException>(() => store.Content.Read(run.Session, new(missing, 0, 0))));

    Assert.Throws<ArgumentException>(() => store.Content.Read(run.Session,
        new(run.User with { Role = (MessageRole)42 }, 0, 0)));
    Assert.Throws<ArgumentException>(() => store.Content.Read(run.Session,
        new(run.User with { MessageId = Guid.Empty }, 0, 0)));
    database.ForceHistoryRevision(1);
    HistoryRevisionConflictException conflict = Assert.Throws<HistoryRevisionConflictException>(
        () => store.Content.Read(run.Session, new(run.Assistant, 0, 0)));
    Assert.Equal(0, conflict.ExpectedRevision);
    Assert.Equal(1, conflict.ActualRevision);
    Assert.Throws<KeyNotFoundException>(() => store.Content.Read(run.Session,
        new(run.Assistant with { HistoryRevision = 1 }, 0, 0)));
  }

  [Fact]
  public void SteeringAndDistinctResponsesKeepExactReferencesAndCursorsAcrossReopen() {
    using var database = new TemporaryDatabase();
    TestRun first;
    MessageRef steering, secondOutput, undelivered;
    MessageContentCursor oldCursor;
    using (SessionStore store = SessionStore.Open(database.Path)) {
      first = StartRun(store, "origin\0🙂");
      steering = new(first.Run, Guid.NewGuid(), MessageRole.User, 0);
      Assert.True(store.Inputs.Admit(first.Session, 0, new(steering.MessageId, 0, "steering\0é"), true,
          Guid.NewGuid(), activeRunId: first.Run).NewlyAccepted);
      MessageContentPage pending = store.Content.Read(first.Session, new(steering, 0, 0));
      Assert.Equal("steering\0é", pending.Text);
      Assert.True(pending.IsClosed);
      Assert.Equal(new(steering, 1, 0), pending.Next);

      Append(store, first, 0, "first🙂");
      oldCursor = store.Content.Read(first.Session, new(first.Assistant, 0, 0)).Next;
      Append(store, first, 1, "\0tail", final: true);
      Assert.True(store.Output.CompleteRequest(first.Session, first.Run, 0,
          new(first.Request, ExecutionOutcome.Completed)));
      LlmRequest next = store.Runs.ClaimNextRequest(first.Session, first.Run, 0)!;
      Assert.NotEqual(first.Request, next.RequestId);
      secondOutput = new(first.Run, Guid.NewGuid(), MessageRole.Assistant, 0);
      Assert.True(store.Output.Append(first.Session, first.Run, 0,
          new(next.RequestId, secondOutput.MessageId, 0, "second\0🙂", false)));
      undelivered = new(first.Run, Guid.NewGuid(), MessageRole.User, 0);
      Assert.True(store.Inputs.Admit(first.Session, 0, new(undelivered.MessageId, 0, "not delivered"), true,
          Guid.NewGuid(), activeRunId: first.Run).NewlyAccepted);
      Assert.Equal(new[] { first.User, steering, first.Assistant, secondOutput, undelivered },
          store.Snapshots.Read(first.Session).Messages.Select(message => message.Message));
      store.Runs.RequestStop(first.Session, first.Run, ownsRun: true);
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
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
    Assert.Null(reopened.Runs.ReadSettlement(first.Session, first.Run));
  }

  [Fact]
  public void StopClosesPartialBytesWithoutFinalOutputOrTerminalOutcome() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    TestRun run = StartRun(store);
    Append(store, run, 0, "partial");
    store.Runs.RequestStop(run.Session, run.Run, ownsRun: true);
    MessageContentPage stopped = store.Content.Read(run.Session, new(run.Assistant, 0, 0));
    Assert.Equal("partial", stopped.Text);
    Assert.True(stopped.IsClosed);
    Assert.False(stopped.FinalOutputCommitted);
    Assert.Null(store.Runs.ReadSettlement(run.Session, run.Run));
    Assert.Equal(ExecutionOutcome.Cancelled, store.Runs.TrySettle(run.Session, run.Run, ExecutionOutcome.Cancelled)!.Outcome);
    Assert.True(store.Content.Read(run.Session, stopped.Next).IsClosed);
  }

  [Fact]
  public void FailedRequestAndReopenedPartialMessageNeverInventFinalOutput() {
    using var database = new TemporaryDatabase();
    TestRun run;
    using (SessionStore store = SessionStore.Open(database.Path)) {
      run = StartRun(store);
      Append(store, run, 0, "retained partial");
    }

    using SessionStore reopened = SessionStore.Open(database.Path);
    MessageContentPage orphan = reopened.Content.Read(run.Session, new(run.Assistant, 0, 0));
    Assert.Equal("retained partial", orphan.Text);
    Assert.False(orphan.IsClosed);
    Assert.False(orphan.FinalOutputCommitted);
    Assert.True(reopened.Output.CompleteRequest(run.Session, run.Run, 0,
        new(run.Request, ExecutionOutcome.Failed, "failed")));
    MessageContentPage requestClosed = reopened.Content.Read(run.Session, orphan.Next);
    Assert.True(requestClosed.IsClosed);
    Assert.False(requestClosed.FinalOutputCommitted);
    Assert.Null(reopened.Runs.ReadSettlement(run.Session, run.Run));
    Assert.Equal(ExecutionOutcome.Failed, reopened.Runs.TrySettle(run.Session, run.Run, ExecutionOutcome.Completed)!.Outcome);
  }

  [Fact]
  public void MissingOrInvalidCommittedSegmentsFailInsteadOfReturningAFalseTail() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    TestRun run = StartRun(store);
    Append(store, run, 0, "valid");
    database.Execute("UPDATE execution_output SET text_utf8 = X'80';");
    Assert.Throws<InvalidDataException>(() => store.Content.Read(run.Session, new(run.Assistant, 0, 0)));
    database.Execute("DELETE FROM execution_output;");
    Assert.Throws<InvalidDataException>(() => store.Content.Read(run.Session, new(run.Assistant, 0, 0)));
  }

  private static TestRun StartRun(SessionStore store, string input = "input") {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid();
    var command = new InputCommand(Guid.NewGuid(), 0, input);
    Assert.True(store.Inputs.Admit(session, 0, command, true, run).NewlyAccepted);
    LlmRequest request = store.Runs.ClaimNextRequest(session, run, 0)!;
    return new(session, run, request.RequestId,
        new(run, command.InputId, MessageRole.User, 0),
        new(run, Guid.NewGuid(), MessageRole.Assistant, 0));
  }

  private static void Append(SessionStore store, TestRun run, int index, string text, bool final = false)
      => Assert.True(store.Output.Append(run.Session, run.Run, 0,
          new(run.Request, run.Assistant.MessageId, index, text, final)));

  private sealed record TestRun(Guid Session, Guid Run, Guid Request, MessageRef User, MessageRef Assistant);
}
