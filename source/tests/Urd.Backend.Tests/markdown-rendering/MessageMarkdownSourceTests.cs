using System.Text;
using Urd.Backend.Tests.Support;
using Urd.Core.MarkdownRendering;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.MarkdownRendering;

public sealed class MessageMarkdownSourceTests {
  [Fact]
  public void CapturesExactSourceAndMapsUnicodeAcrossEmptySegments() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    const string userText = "user\r\n🙂\0é";
    TestExecution execution = StartExecution(store, userText);
    string[] parts = ["a", "", "🙂", "", "é\r\n\0", ""];
    for (int index = 0; index < parts.Length; index++)
      Append(store, execution, index, parts[index], final: index == parts.Length - 1);

    MessageMarkdownSource user = ReadSource(store, execution, execution.User);
    MessageMarkdownSource assistant = ReadSource(store, execution, execution.Assistant);

    Assert.Equal(userText, user.Source);
    Assert.Equal(Encoding.UTF8.GetByteCount(userText), user.Utf8Bytes);
    Assert.True(user.IsClosed);
    Assert.False(user.FinalOutputCommitted);
    Assert.Equal(0, user.HistoryRevision);
    Assert.Equal(execution.User, user.Message);
    Assert.Equal(string.Concat(parts), assistant.Source);
    Assert.Equal(Encoding.UTF8.GetByteCount(assistant.Source), assistant.Utf8Bytes);
    Assert.True(assistant.IsClosed);
    Assert.True(assistant.FinalOutputCommitted);
    Assert.Equal(execution.Assistant, assistant.Message);
    Assert.Equal(parts.Select((_, index) => new MarkdownSourceSegment(
        Encoding.UTF8.GetByteCount(string.Concat(parts.Take(index))), index)), assistant.Segments);
    Assert.Equal(new(execution.Assistant, 0, 0), assistant.ContentCursor(0));
    Assert.Equal(new(execution.Assistant, 2, 0), assistant.ContentCursor(1));
    Assert.Equal(new(execution.Assistant, 4, 0), assistant.ContentCursor(5));
    Assert.Equal(assistant.AvailableThrough, assistant.ContentCursor(assistant.Utf8Bytes));
    Assert.Equal((new MessageContentCursor(execution.Assistant, 2, 0),
        new MessageContentCursor(execution.Assistant, 4, 0)), assistant.ContentRange(new(1, 5)));
  }

  [Fact]
  public void ReadsLargeUserSourceAcrossScalarAlignedStorageReads() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    string text = new string('x', 16383) + "🙂" + new string('y', 17000);
    TestExecution execution = StartExecution(store, text);
    MessageMarkdownSource source = ReadSource(store, execution, execution.User);

    Assert.True(source.Utf8Bytes > 16 * 1024);
    Assert.Equal(text, source.Source);
    Assert.Equal(Encoding.UTF8.GetByteCount(text), source.Utf8Bytes);
    Assert.Equal(new(execution.User, 0, 16383), source.ContentCursor(16383));
    Assert.Equal(new(execution.User, 0, 16387), source.ContentCursor(16387));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void OpenCaptureKeepsSnapshotDuringAppendAndClosure(bool final) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "");
    Append(store, execution, 1, "before🙂");
    Append(store, execution, 2, "é\0");
    TranscriptTextFacts snapshot = Facts(store, execution, execution.Assistant);
    Append(store, execution, 3, "later");
    MessageContentExtent extent = store.Content.CaptureSourceExtent(execution.Session, execution.Assistant, snapshot);
    Assert.NotEmpty(store.Content.ReadSourceSegment(extent, extent.From).Prefix);
    if (final)
      Append(store, execution, 4, "last", final: true);
    else
      Assert.True(store.LlmResponses.ObserveTool(execution.Session, execution.Execution, 0, execution.Request, "call", "tool"));
    MessageMarkdownSource source = MessageMarkdownSourceReader.Read(store.Content, extent);
    store.Content.ValidateSourceExtent(extent);

    Assert.Equal("before🙂é\0", source.Source);
    Assert.Equal(new(execution.Assistant, 1, 0), source.From);
    Assert.Equal(new(execution.Assistant, 3, 0), source.AvailableThrough);
    Assert.Equal(2, source.Segments.Count);
    Assert.False(source.IsClosed);
    Assert.False(source.FinalOutputCommitted);
    Assert.True(Facts(store, execution, execution.Assistant).IsClosed);
    Assert.Equal(extent, store.Content.CaptureSourceExtent(execution.Session, execution.Assistant, snapshot));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void CapturesClosedPartialAndIgnoresLaterSameRequestOutput(bool failed) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "");
    Append(store, execution, 1, "partial🙂");
    if (failed) {
      Assert.True(store.LlmResponses.CompleteRequest(execution.Session, execution.Execution, 0,
          new(execution.Request, ExecutionOutcome.Failed, "provider failed")));
      Assert.NotNull(store.Executions.TrySettle(execution.Session, execution.Execution, ExecutionOutcome.Failed));
    } else {
      Assert.True(store.LlmResponses.ObserveTool(execution.Session, execution.Execution, 0, execution.Request, "call", "tool"));
    }
    MessageContentExtent extent = Capture(store, execution, execution.Assistant);
    if (!failed) Append(store, execution, 2, "after", output: Guid.NewGuid());
    MessageMarkdownSource source = MessageMarkdownSourceReader.Read(store.Content, extent);
    store.Content.ValidateSourceExtent(extent);

    Assert.Equal(new(execution.Assistant, 1, 0), source.From);
    Assert.Equal(new(execution.Assistant, 2, 0), source.AvailableThrough);
    Assert.Equal("partial🙂", source.Source);
    Assert.True(source.IsClosed);
    Assert.False(source.FinalOutputCommitted);
    Assert.Equal(new MarkdownSourceSegment(0, 1), Assert.Single(source.Segments));
  }

  [Fact]
  public async Task SessionEntryPointUsesRequestedOpenSnapshotWithoutStartingLlm() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "read🙂");
    var key = new MessageMarkdownKey(execution.Session, execution.Assistant, 0, 1, false, false, 8);
    Append(store, execution, 1, "later", final: true);
    using var client = new ScriptedChatClient(_ => throw new InvalidOperationException("Markdown reads must not start execution."));
    var session = await new SessionManager(store, _ => client).GetPrimaryAsync(execution.Session);
    MessageMarkdownSource source = await session.Transcript.ReadMarkdownSourceAsync(key);

    Assert.Equal("read🙂", source.Source);
    Assert.True(key.Matches(source));
    Assert.False(source.IsClosed);
    Assert.False(source.FinalOutputCommitted);
    Assert.Equal(0, client.Calls);
    await Assert.ThrowsAsync<ArgumentException>(() => session.Transcript.ReadMarkdownSourceAsync(key with { SessionId = Guid.NewGuid() }));
  }

  [Fact]
  public void RejectsUncommittedBoundsWrongIdentityAndImpossibleCompletionFacts() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "open");
    TranscriptTextFacts facts = Facts(store, execution, execution.Assistant);
    Guid other = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Assert.Throws<KeyNotFoundException>(() => store.Content.CaptureSourceExtent(other, execution.Assistant, facts));
    Assert.Throws<ArgumentException>(() => store.Content.CaptureSourceExtent(execution.Session, execution.User, facts));
    Assert.Throws<InvalidOperationException>(() => store.Content.CaptureSourceExtent(execution.Session, execution.Assistant,
        facts with { AvailableThrough = facts.AvailableThrough with { SegmentIndex = 2 } }));
    Assert.Throws<InvalidOperationException>(() => store.Content.CaptureSourceExtent(execution.Session, execution.Assistant,
        facts with { IsClosed = true }));
    Assert.Throws<ArgumentException>(() => store.Content.CaptureSourceExtent(execution.Session, execution.Assistant,
        facts with { FinalOutputCommitted = true }));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void CaptureAndValidationFenceStaleRevisionAndDeletedIdentity(bool revision) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    TestExecution execution = StartExecution(store);
    Append(store, execution, 0, "identity");
    TranscriptTextFacts facts = Facts(store, execution, execution.Assistant);
    MessageContentExtent extent = Capture(store, execution, execution.Assistant);
    if (revision) database.ForceHistoryRevision(1);
    else database.Execute($"DELETE FROM execution_messages WHERE session_id = '{execution.Session:D}' "
        + $"AND run_id = '{execution.Execution:D}' AND role = 1 AND message_id = '{execution.Assistant.MessageId:D}';");
    Type error = revision ? typeof(HistoryRevisionConflictException) : typeof(KeyNotFoundException);
    Assert.Throws(error, () => store.Content.ValidateSourceExtent(extent));
    Assert.Throws(error, () => store.Content.CaptureSourceExtent(execution.Session, execution.Assistant, facts));
  }

  static TestExecution StartExecution(UrdStore store, string text = "input") {
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, text, TestLlmSelection.Default);
    Assert.True(store.UserMessages.Admit(session, 0, submission, true, execution).NewlyAccepted);
    LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    Assert.True(store.Executions.StartRequest(session, execution, 0, request.RequestId));
    return new(session, execution, request.RequestId,
        new(execution, submission.SubmissionId, MessageRole.User, 0), new(execution, Guid.NewGuid(), MessageRole.Assistant, 0));
  }

  static TranscriptTextFacts Facts(UrdStore store, TestExecution execution, MessageRef message) =>
      store.History.Read(execution.Session, new(TranscriptHistoryMode.Latest, 0)).Entries
          .Single(entry => entry.Text?.From.Message == message).Text!;

  static MessageContentExtent Capture(UrdStore store, TestExecution execution, MessageRef message) =>
      store.Content.CaptureSourceExtent(execution.Session, message, Facts(store, execution, message));

  static MessageMarkdownSource ReadSource(UrdStore store, TestExecution execution, MessageRef message) =>
      MessageMarkdownSourceReader.Read(store.Content, Capture(store, execution, message));

  static void Append(UrdStore store, TestExecution execution, int index, string text, bool final = false, Guid? output = null) =>
      Assert.True(store.LlmResponses.Append(execution.Session, execution.Execution, 0,
          new(execution.Request, output ?? execution.Assistant.MessageId, index, text, final)));

  sealed record TestExecution(Guid Session, Guid Execution, Guid Request, MessageRef User, MessageRef Assistant);
}
