using System.Text;
using Urd.Backend.Tests.Support;
using Urd.Core.Display;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Display;

public sealed class MessageDisplaySourceTests {
  [Fact]
  public void CapturesExactUserAndAssistantSourceAcrossUnicodeSegments() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    const string userText = "user\r\n🙂\0é";
    TestExecution execution = StartExecution(store, userText);
    string[] parts = ["assistant🙂", "\r\n", "\0", "é", ""];
    for (int index = 0; index < parts.Length; index++)
      Append(store, execution, index, parts[index], final: index == parts.Length - 1);

    MessageDisplaySource user = ReadSource(store, execution.Session, execution.User);
    MessageDisplaySource assistant = ReadSource(store, execution.Session, execution.Assistant);

    Assert.Equal(userText, user.Source);
    Assert.Equal(Encoding.UTF8.GetByteCount(userText), user.Utf8Bytes);
    Assert.True(user.IsClosed);
    Assert.False(user.FinalOutputCommitted);
    Assert.Equal(0, user.HistoryRevision);
    Assert.Equal(execution.User, user.Message);
    Assert.Equal("assistant🙂\r\n\0é", assistant.Source);
    Assert.Equal(Encoding.UTF8.GetByteCount(assistant.Source), assistant.Utf8Bytes);
    Assert.True(assistant.IsClosed);
    Assert.True(assistant.FinalOutputCommitted);
    Assert.Equal(execution.Assistant, assistant.Message);
    Assert.Equal(0, assistant.Segments[0].Utf8Start);
    Assert.Equal(parts.Length, assistant.Segments.Count);
    Assert.Equal(Enumerable.Range(0, parts.Length).Select(index => (long)index),
        assistant.Segments.Select(segment => segment.SegmentIndex));
    Assert.Equal(parts.Select((_, index) => Encoding.UTF8.GetByteCount(string.Concat(parts.Take(index)))),
        assistant.Segments.Select(segment => segment.Utf8Start));
  }

  [Fact]
  public void ReadsLargeUserSourceAcrossScalarAlignedStorageReads() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    string text = new string('x', 16383) + "🙂" + new string('y', 17000);
    TestExecution execution = StartExecution(store, text);

    MessageDisplaySource source = ReadSource(store, execution.Session, execution.User);

    Assert.True(source.Utf8Bytes > 16 * 1024);
    Assert.Equal(text, source.Source);
    Assert.Equal(Encoding.UTF8.GetByteCount(text), source.Utf8Bytes);
    Assert.Equal(new(execution.User, 0, 16383), source.ContentCursor(16383));
    Assert.Equal(new(execution.User, 0, 16387), source.ContentCursor(16387));
  }

  [Fact]
  public void CapturesClosedFailedPartialFromNormalizedNonzeroSegment() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "");
    Append(store, execution, 1, "partial🙂");
    Assert.True(store.LlmResponses.CompleteRequest(execution.Session, execution.Execution, 0,
        new(execution.Request, ExecutionOutcome.Failed, "provider failed")));
    Assert.NotNull(store.Executions.TrySettle(execution.Session, execution.Execution, ExecutionOutcome.Failed));

    MessageContentExtent extent = store.Content.CaptureClosedSourceExtent(execution.Session, execution.Assistant);
    MessageDisplaySource source = MessageDisplaySourceReader.Read(store.Content, extent);

    Assert.Equal(new(execution.Assistant, 1, 0), source.From);
    Assert.Equal(new(execution.Assistant, 2, 0), source.AvailableThrough);
    Assert.Equal("partial🙂", source.Source);
    Assert.True(source.IsClosed);
    Assert.False(source.FinalOutputCommitted);
    Assert.Equal(new DisplaySourceSegment(0, 1), Assert.Single(source.Segments));
  }

  [Fact]
  public void CapturesClosedBeforeToolAndIgnoresLaterSameRequestOutput() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "");
    Append(store, execution, 1, "before🙂");
    Assert.True(store.LlmResponses.ObserveTool(execution.Session, execution.Execution, 0,
        execution.Request, "call", "tool"));

    MessageContentExtent extent = store.Content.CaptureClosedSourceExtent(execution.Session, execution.Assistant);
    Append(store, execution, 2, "after", output: Guid.NewGuid());
    MessageDisplaySource source = MessageDisplaySourceReader.Read(store.Content, extent);
    store.Content.ValidateSourceExtent(extent);

    Assert.Equal(new(execution.Assistant, 1, 0), source.From);
    Assert.Equal(new(execution.Assistant, 2, 0), source.AvailableThrough);
    Assert.Equal("before🙂", source.Source);
    Assert.True(source.IsClosed);
    Assert.False(source.FinalOutputCommitted);
  }

  [Fact]
  public void RejectsWrongSessionStaleRevisionAndOpenAssistantMessages() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution closed = StartExecution(store, "closed input");
    Append(store, closed, 0, "closed", final: true);
    Guid otherSession = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;

    Assert.Throws<KeyNotFoundException>(() => store.Content.CaptureClosedSourceExtent(otherSession, closed.Assistant));
    Assert.Throws<HistoryRevisionConflictException>(() => store.Content.CaptureClosedSourceExtent(
        closed.Session, closed.Assistant with { HistoryRevision = 1 }));

    TestExecution open = StartExecution(store, "open input");
    Append(store, open, 0, "still streaming");
    Assert.Throws<InvalidOperationException>(() => store.Content.CaptureClosedSourceExtent(open.Session, open.Assistant));
  }

  [Fact]
  public void MapsScalarAlignedDisplayRangesAcrossEmptySegments() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    string[] parts = ["a", "", "🙂", "", "z", ""];
    for (int index = 0; index < parts.Length; index++)
      Append(store, execution, index, parts[index], final: index == parts.Length - 1);

    MessageDisplaySource source = ReadSource(store, execution.Session, execution.Assistant);

    Assert.Equal("a🙂z", source.Source);
    Assert.Equal(new[] {
      new DisplaySourceSegment(0, 0),
      new DisplaySourceSegment(1, 1),
      new DisplaySourceSegment(1, 2),
      new DisplaySourceSegment(5, 3),
      new DisplaySourceSegment(5, 4),
      new DisplaySourceSegment(6, 5)
    }, source.Segments);
    Assert.Equal(new(execution.Assistant, 0, 0), source.ContentCursor(0));
    Assert.Equal(new(execution.Assistant, 2, 0), source.ContentCursor(1));
    Assert.Equal(new(execution.Assistant, 4, 0), source.ContentCursor(5));
    Assert.Equal(source.AvailableThrough, source.ContentCursor(6));
    Assert.Equal((new MessageContentCursor(execution.Assistant, 2, 0),
        new MessageContentCursor(execution.Assistant, 4, 0)), source.ContentRange(new(1, 5)));
  }

  [Fact]
  public async Task SessionEntryPointReturnsValidatedDisplaySourceWithoutStartingLlm() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store, "session source");
    Append(store, execution, 0, "read through session", final: true);
    using var client = new ScriptedChatClient(_ =>
        throw new InvalidOperationException("Reading display source must not start an LLM request."));
    var manager = new SessionManager(store, _ => client);
    PrimarySession session = await manager.GetPrimaryAsync(execution.Session);

    MessageDisplaySource source = await session.ReadMessageDisplaySourceAsync(execution.Assistant);

    Assert.Equal(execution.Session, source.SessionId);
    Assert.Equal(execution.Assistant, source.Message);
    Assert.Equal("read through session", source.Source);
    Assert.True(source.IsClosed);
    Assert.True(source.FinalOutputCommitted);
    Assert.Equal(0, client.Calls);
  }

  [Fact]
  public void ValidationRejectsStaleRevisionAndDeletedMessageIdentity() {
    using (var database = new TemporaryDatabase()) {
      using UrdStore store = UrdStore.Open(database.Path);
      TestExecution execution = StartExecution(store);
      Append(store, execution, 0, "revision", final: true);
      MessageContentExtent extent = store.Content.CaptureClosedSourceExtent(execution.Session, execution.Assistant);

      database.ForceHistoryRevision(1);

      Assert.Throws<HistoryRevisionConflictException>(() => store.Content.ValidateSourceExtent(extent));
    }

    using (var database = new TemporaryDatabase()) {
      using UrdStore store = UrdStore.Open(database.Path);
      TestExecution execution = StartExecution(store);
      Append(store, execution, 0, "identity", final: true);
      MessageContentExtent extent = store.Content.CaptureClosedSourceExtent(execution.Session, execution.Assistant);
      database.Execute($"DELETE FROM execution_messages WHERE session_id = '{execution.Session:D}' "
          + $"AND run_id = '{execution.Execution:D}' AND role = 1 AND message_id = '{execution.Assistant.MessageId:D}';");

      Assert.Throws<KeyNotFoundException>(() => store.Content.ValidateSourceExtent(extent));
    }
  }

  private static TestExecution StartExecution(UrdStore store, string text = "input") {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, text, TestLlmSelection.Default);
    Assert.True(store.UserMessages.Admit(session, 0, submission, true, execution).NewlyAccepted);
    LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    Assert.True(store.Executions.StartRequest(session, execution, 0, request.RequestId));
    return new(session, execution, request.RequestId,
        new(execution, submission.SubmissionId, MessageRole.User, 0),
        new(execution, Guid.NewGuid(), MessageRole.Assistant, 0));
  }

  private static MessageDisplaySource ReadSource(UrdStore store, Guid sessionId, MessageRef message) =>
      MessageDisplaySourceReader.Read(store.Content, store.Content.CaptureClosedSourceExtent(sessionId, message));

  private static void Append(UrdStore store, TestExecution execution, int index, string text, bool final = false,
      Guid? output = null) =>
      Assert.True(store.LlmResponses.Append(execution.Session, execution.Execution, 0,
          new(execution.Request, output ?? execution.Assistant.MessageId, index, text, final)));

  private sealed record TestExecution(Guid Session, Guid Execution, Guid Request, MessageRef User, MessageRef Assistant);
}
