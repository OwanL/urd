using System.Text;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class MessageContentTests {
  [Fact]
  public void PagesPreserveUnicodeAndNeverSplitAScalarAtTheByteLimit() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    string submission = string.Concat(Enumerable.Repeat("🙂\0é", 2000));
    TestExecution execution = StartExecution(store, submission);
    string firstSegment = new('a', 16382);
    string secondSegment = "a🙂\0é🙂";
    Append(store, execution, 0, firstSegment);
    Append(store, execution, 1, secondSegment, final: true);

    MessageContentPage input = store.Content.Read(execution.Identity.SessionId, new(execution.User, 0, 0));
    Assert.Equal(submission, input.Text);
    Assert.True(input.IsClosed);

    MessageContentPage first = store.Content.Read(execution.Identity.SessionId, new(execution.Assistant, 0, 0));
    Assert.Equal(firstSegment + "a", first.Text);
    Assert.InRange(Encoding.UTF8.GetByteCount(first.Text), 1, 16384);
    Assert.Equal(new(execution.Assistant, 1, 1), first.Next);
    Assert.True(first.HasMore);
    Assert.True(first.FinalOutputCommitted);

    MessageContentPage remainder = store.Content.Read(execution.Identity.SessionId, first.Next);
    Assert.Equal(secondSegment[1..], remainder.Text);
    Assert.Equal(firstSegment + secondSegment, first.Text + remainder.Text);
    Assert.Equal(new(execution.Assistant, 2, 0), remainder.Next);
    Assert.False(remainder.HasMore);
  }

  [Fact]
  public void EmptyTailWaitsForAppendThenAdvancesAcrossAnEmptyFinalSegment() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "partial\0🙂");

    MessageContentPage initial = store.Content.Read(execution.Identity.SessionId, new(execution.Assistant, 0, 0));
    Assert.Equal("partial\0🙂", initial.Text);
    Assert.False(initial.HasMore);
    Assert.False(initial.IsClosed);
    MessageContentPage tail = store.Content.Read(execution.Identity.SessionId, initial.Next);
    Assert.Equal(tail.From, tail.Next);
    Assert.False(tail.HasMore);
    Assert.False(tail.IsClosed);

    Append(store, execution, 1, "", final: true);
    MessageContentPage final = store.Content.Read(execution.Identity.SessionId, tail.Next);
    Assert.Equal(new(execution.Assistant, 2, 0), final.Next);
    Assert.True(final.IsClosed);
    Assert.True(final.FinalOutputCommitted);
    Assert.False(final.HasMore);
    Assert.Equal(final.AvailableThrough, final.Next);
    Assert.Equal(final.Next, store.Content.Read(execution.Identity.SessionId, final.Next).Next);
  }

  private static TestExecution StartExecution(UrdStore store, string text = "input") {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var identity = new ExecutionIdentity(session, Guid.NewGuid(), 0);
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, text, TestLlmSelection.Default);
    Assert.True(store.UserMessages.Submit(session, 0, submission, true, identity.ExecutionId).NewlyAccepted);
    LlmRequest request = store.Executions.CreateNextRequest(identity)!;
    Assert.True(store.Executions.StartRequest(identity, request.RequestId));
    return new(identity, request.RequestId,
        new(identity.ExecutionId, submission.SubmissionId, MessageRole.User, 0),
        new(identity.ExecutionId, Guid.NewGuid(), MessageRole.Assistant, 0));
  }

  private static void Append(UrdStore store, TestExecution execution, int index, string text, bool final = false)
      => Assert.True(store.LlmResponses.Append(execution.Identity,
          new(execution.Request, execution.Assistant.MessageId, index, text, final)));

  private sealed record TestExecution(ExecutionIdentity Identity, Guid Request, MessageRef User, MessageRef Assistant);
}
