using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

public sealed class LlmResponseWriterTests {
  private const int MaxChunkBytes = 16 * 1024;
  private static readonly UTF8Encoding StrictUtf8 = new(false, true);

  [Fact]
  public async Task CoalescesTinyUpdatesAndSplitsOversizedTextAtUtf8ScalarBoundaries() {
    string text = new string('a', MaxChunkBytes - 1) + "😀" + new string('b', 40_000) + "\0終";
    var client = new ScriptedChatClient(token => TextResponse(text, token));
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    var output = StoredLlmResponses.Read(fixture.Database);
    Assert.NotEmpty(output);
    Assert.Equal(text, string.Concat(output.Select(chunk => StrictUtf8.GetString(chunk.Bytes))));
    Assert.Equal(Enumerable.Range(0, output.Count).Select(index => (long)index),
        output.Select(chunk => chunk.ChunkIndex));
    Assert.Single(output, chunk => chunk.Final);
    Assert.True(output[^1].Final);
    Assert.All(output, chunk => Assert.InRange(chunk.Bytes.Length, 0, MaxChunkBytes));
    Assert.True(output.Count < 100, "Tiny source updates must not each cause a durable append.");
    Assert.Equal(1, client.Calls);
    Assert.Equal(0, client.DisposeCalls);
    Assert.Equal("go", client.LastRequest[^1].Text);
  }

  [Fact]
  public async Task FlushesDeadlineWhileTheNextSourceReadIsPaused() {
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
    } finally {
      release.TrySetResult();
    }

    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal("visible", StoredLlmResponses.ReadText(fixture.Database));
    Assert.Single(StoredLlmResponses.Read(fixture.Database), row => row.Final);
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> TextResponse(string text,
      [EnumeratorCancellation] CancellationToken token) {
    await Task.CompletedTask;
    for (int i = 0; i < 1_000; i++) {
      token.ThrowIfCancellationRequested();
      yield return Text(text[i].ToString());
    }

    // Split the emoji's surrogate pair across source updates at the chunk boundary.
    yield return Text(text[1_000..MaxChunkBytes]);
    yield return Text(text[MaxChunkBytes..]);
    yield return new() { FinishReason = ChatFinishReason.Stop };
  }
}
