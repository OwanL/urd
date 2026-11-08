using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

// Response buffering through a real session: chunk sizes, flush timing and Unicode boundaries.
public sealed class LlmResponseWriterTests {
  private const int MaxChunkBytes = 16 * 1024;
  private static readonly UTF8Encoding StrictUtf8 = new(false, true);

  [Fact]
  public async Task CoalescesTinyUpdatesAndSplitsOversizedTextAtUtf8ScalarBoundaries() {
    string text = new string('a', MaxChunkBytes - 1) + "😀" + new string('b', 40_000) + "\0終";
    var client = new ScriptedChatClient(token => TextResponse(text, token));
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("go");

    SessionExecutionResult settlement = await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout);
    Assert.Equal(ExecutionOutcome.Completed, settlement.Outcome);
    var output = StoredLlmResponses.Read(fixture.Database);
    AssertValidResponse(output.Select(chunk =>
        (chunk.ChunkIndex, StrictUtf8.GetString(chunk.Bytes), chunk.Final)), text);
    Assert.True(output.Count < 100, "Tiny source updates must not each cause a durable append.");
    Assert.Equal(1, client.Calls);
    Assert.Equal(0, client.DisposeCalls);
    Assert.Equal("go", client.LastRequest[^1].Text);
  }

  [Theory]
  [InlineData(1, "é", false)]
  [InlineData(2, "é", false)]
  [InlineData(1, "終", false)]
  [InlineData(2, "終", false)]
  [InlineData(3, "終", false)]
  [InlineData(1, "😀", false)]
  [InlineData(2, "😀", false)]
  [InlineData(3, "😀", false)]
  [InlineData(4, "😀", false)]
  [InlineData(3, "😀", true)]
  [InlineData(4, "😀", true)]
  public async Task WritesValidUnicodeAndPacksGreedilyAtScalarBoundaries(int bytesLeft, string scalar, bool splitAcrossUpdates) {
    string prefix = new string('a', MaxChunkBytes - bytesLeft);
    // Keep enough text after the tested scalar to fill a second chunk as well.
    string suffix = new string('t', MaxChunkBytes);
    string text = prefix + scalar + suffix;
    string[] updates = splitAcrossUpdates
        ? [prefix, scalar[..1], string.Empty, scalar[1..], suffix]
        : [text];
    var chunks = new List<LlmResponseChunk>();
    var writer = new LlmResponseWriter(Guid.NewGuid(), chunk => {
      chunks.Add(chunk);
      return Task.CompletedTask;
    });

    foreach (string update in updates)
      await writer.AppendTextAsync(update);
    await writer.FinishResponseAsync();

    AssertValidResponse(chunks.Select(chunk => (chunk.ChunkIndex, chunk.Text, chunk.IsFinal)), text);
    int offset = 0;
    foreach (var chunk in chunks.Where(chunk => !chunk.IsFinal)) {
      offset += chunk.Text.Length;
      int nextBytes = offset < text.Length ? Rune.GetRuneAt(text, offset).Utf8SequenceLength : 1;
      Assert.True(StrictUtf8.GetByteCount(chunk.Text) + nextBytes > MaxChunkBytes,
          "A size-flushed chunk must not leave room for the next scalar.");
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task FlushesDeadlineWhileTheSingleNextSourceReadIsPaused(bool stopWhilePaused) {
    var waiting = Signal();
    var release = Signal();
    var client = new ScriptedChatClient(token => Paused(waiting, release, token));
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("go");
    try {
      await waiting.Task.WaitAsync(TestTimeout);
      await StoredLlmResponses.WaitForAnyAsync(fixture.Database);
      Assert.Equal("visible", StoredLlmResponses.ReadText(fixture.Database));
      Assert.All(StoredLlmResponses.Read(fixture.Database), row => Assert.False(row.Final));
      Assert.False(fixture.Session.WaitForSettlementAsync(executionId).IsCompleted);
      Assert.Equal(1, client.Calls);
      if (stopWhilePaused)
        await fixture.Session.RequestStopAsync(executionId).WaitAsync(TestTimeout);
    } finally {
      release.TrySetResult();
    }

    SessionExecutionResult settlement = await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout);
    Assert.Equal(stopWhilePaused ? ExecutionOutcome.Cancelled : ExecutionOutcome.Completed, settlement.Outcome);
    Assert.Equal("visible", StoredLlmResponses.ReadText(fixture.Database));
    Assert.Equal(!stopWhilePaused, StoredLlmResponses.Read(fixture.Database).Any(row => row.Final));
  }

  [Theory]
  [InlineData(0xDC00, null)]
  [InlineData(0xD800, 0x0078)]
  [InlineData(0xD800, 0xD800)]
  [InlineData(0xD800, null)]
  public async Task RejectsMalformedOrIncompleteUnicodeWithoutFinalMarker(int firstCodeUnit, int? secondCodeUnit) {
    // Attribute strings use UTF-8 metadata, so construct lone surrogates at runtime.
    string firstUpdate = ((char)firstCodeUnit).ToString();
    string[] updates = secondCodeUnit is { } codeUnit
        ? [firstUpdate, ((char)codeUnit).ToString()]
        : [firstUpdate];
    var client = new ScriptedChatClient(token => TextUpdates(updates, token));
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Failed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.DoesNotContain(StoredLlmResponses.Read(fixture.Database), row => row.Final);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(16 * 1024)]
  public async Task PersistsEmptyAndExactBoundaryResponsesWithOrderedOutputIdentity(int textLength) {
    string text = new string('x', textLength);
    var client = new ScriptedChatClient(token => ExactResponse(text, token));
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    var output = StoredLlmResponses.Read(fixture.Database);
    AssertValidResponse(output.Select(chunk =>
        (chunk.ChunkIndex, StrictUtf8.GetString(chunk.Bytes), chunk.Final)), text);
    Guid requestId = Assert.Single(StoredExecutions.ReadRequests(fixture.Database)).RequestId;
    Guid responseId = output[0].ResponseId;

    Assert.NotEqual(Guid.Empty, responseId);
    Assert.All(output, row => {
      Assert.Equal(requestId, row.RequestId);
      Assert.Equal(responseId, row.ResponseId);
    });

    if (textLength == 0) {
      Assert.Single(output);
      Assert.Empty(output[0].Bytes);
    } else {
      Assert.Contains(output, row => row.Bytes.Length == MaxChunkBytes && !row.Final);
      Assert.Empty(output[^1].Bytes);
    }
  }

  [Fact]
  public async Task WaitsForChunkWritesBeforeContinuingAndKeepsTheirOrder() {
    var writing = Signal();
    var release = Signal();
    var chunks = new List<LlmResponseChunk>();
    var writer = new LlmResponseWriter(Guid.NewGuid(), async chunk => {
      writing.TrySetResult();
      await release.Task;
      chunks.Add(chunk);
    });
    string text = new string('x', 16 * 1024) + "🙂tail";
    Task append = writer.AppendTextAsync(text);
    try {
      await writing.Task.WaitAsync(TestTimeout);
      Assert.False(append.IsCompleted);
      Assert.Empty(chunks);
    } finally {
      release.TrySetResult();
      await append.WaitAsync(TestTimeout);
    }

    await writer.FinishResponseAsync();
    AssertValidResponse(chunks.Select(chunk => (chunk.ChunkIndex, chunk.Text, chunk.IsFinal)), text);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task SavesValidTextBeforeRejectingInvalidTextAtChunkBoundary(bool overflowBoundary) {
    const int maxChunkBytes = 16 * 1024;
    string validText = overflowBoundary
        ? new string('x', maxChunkBytes - 1) + "é"
        : new string('x', maxChunkBytes);
    string savedText = overflowBoundary ? new string('x', maxChunkBytes - 1) : validText;
    var chunks = new List<LlmResponseChunk>();
    var writer = new LlmResponseWriter(Guid.NewGuid(), chunk => {
      chunks.Add(chunk);
      return Task.CompletedTask;
    });

    await Assert.ThrowsAsync<InvalidDataException>(() => writer.AppendTextAsync(validText + "\udc00"));

    var saved = Assert.Single(chunks);
    Assert.Equal(savedText, saved.Text);
    Assert.Equal(0L, saved.ChunkIndex);
    Assert.False(saved.IsFinal);
  }

  [Fact]
  public async Task KeepsFlushDelayIdentityAcrossAppendsAndEmptyUpdatesUntilFlush() {
    var chunks = new List<LlmResponseChunk>();
    var writer = new LlmResponseWriter(Guid.NewGuid(), chunk => {
      chunks.Add(chunk);
      return Task.CompletedTask;
    });

    await writer.AppendTextAsync("first");
    Task firstFlushDelay = Assert.IsAssignableFrom<Task>(writer.FlushDelay);
    await writer.AppendTextAsync(string.Empty);
    Assert.Same(firstFlushDelay, writer.FlushDelay);
    await writer.AppendTextAsync(" second");
    Assert.Same(firstFlushDelay, writer.FlushDelay);

    await writer.FlushAsync();
    Assert.Null(writer.FlushDelay);
    Assert.Equal("first second", Assert.Single(chunks).Text);

    await writer.AppendTextAsync("after flush");
    Assert.NotNull(writer.FlushDelay);
    Assert.NotSame(firstFlushDelay, writer.FlushDelay);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task FailedChunkWriteStopsBeforeProcessingRemainingText(bool overflowBoundary) {
    var failure = new IOException("Chunk save failed.");
    int attempts = 0;
    var writer = new LlmResponseWriter(Guid.NewGuid(), _ => {
      attempts++;
      return Task.FromException(failure);
    });
    // The invalid tail must not mask the earlier persistence failure or cause another write.
    string prefix = overflowBoundary
        ? new string('x', 16 * 1024 - 1) + "é"
        : new string('x', 16 * 1024);
    string text = prefix + "\ud800x";

    IOException observed = await Assert.ThrowsAsync<IOException>(() => writer.AppendTextAsync(text));
    Assert.Same(failure, observed);
    Assert.Equal(1, attempts);
  }

  private static void AssertValidResponse(
      IEnumerable<(long ChunkIndex, string Text, bool Final)> chunks, string expectedText) {
    var output = chunks.ToArray();
    Assert.NotEmpty(output);
    Assert.Equal(expectedText, string.Concat(output.Select(chunk => chunk.Text)));
    Assert.Equal(Enumerable.Range(0, output.Length).Select(index => (long)index),
        output.Select(chunk => chunk.ChunkIndex));
    Assert.Single(output, chunk => chunk.Final);
    Assert.True(output[^1].Final);
    Assert.All(output, chunk => Assert.InRange(StrictUtf8.GetByteCount(chunk.Text), 0, MaxChunkBytes));
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> TextResponse(string text,
      [EnumeratorCancellation] CancellationToken token) {
    await Task.CompletedTask;
    for (int i = 0; i < 1_000; i++) {
      token.ThrowIfCancellationRequested();
      yield return Text(text[i].ToString());
    }

    // Split a surrogate pair across updates at the chunk boundary, then yield oversized text.
    yield return Text(text[1_000..MaxChunkBytes]);
    yield return Text(text[MaxChunkBytes..]);
    yield return new() { FinishReason = ChatFinishReason.Stop };
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> ExactResponse(string text,
      [EnumeratorCancellation] CancellationToken token) {
    await Task.CompletedTask;
    token.ThrowIfCancellationRequested();
    if (text.Length != 0)
      yield return Text(text);

    yield return new() { FinishReason = ChatFinishReason.Stop };
  }
}
