using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

#pragma warning disable MEAI001

namespace Urd.Backend.Tests.Execution;

// LlmResponseRunner's optional reasoning capture keeps a second bounded writer beside the
// main output writer; the preview is advisory and never feeds back into any messages.
public sealed class LlmResponseReasoningTests {
  [Fact]
  public async Task CapturesReasoningAndOutputWithoutMixing() {
    Guid requestId = Guid.NewGuid();
    var client = new ScriptedChatClient(token => Script(token));
    var output = new ConcurrentQueue<LlmResponseChunk>();
    var reasoning = new ConcurrentQueue<LlmResponseChunk>();

    LlmResponseRunner.Result response = await LlmResponseRunner.RunAsync(client,
        [new(ChatRole.User, "go")], new ChatOptions(), requestId,
        chunk => CaptureAsync(output, chunk), (_, _) => Task.CompletedTask, CancellationToken.None,
        chunk => CaptureAsync(reasoning, chunk)).WaitAsync(TestTimeout);

    Assert.Null(response.Failure);
    Assert.Equal("answer tail", string.Concat(output.Select(chunk => chunk.Text)));
    Assert.Equal("reasoning more", string.Concat(reasoning.Select(chunk => chunk.Text)));
    Assert.DoesNotContain("opaque-token", output.Concat(reasoning).Select(chunk => chunk.Text));
    Assert.All(output.Concat(reasoning), chunk => Assert.Equal(requestId, chunk.RequestId));
    Assert.Single(output, chunk => chunk.IsFinal);
    Assert.Single(reasoning, chunk => chunk.IsFinal);
    Assert.Equal(Enumerable.Range(0, output.Count).Select(index => (long)index),
        output.Select(chunk => chunk.ChunkIndex));
    Assert.Equal(Enumerable.Range(0, reasoning.Count).Select(index => (long)index),
        reasoning.Select(chunk => chunk.ChunkIndex));
    Assert.Equal("done", response.ProviderResponse is { } context ? LlmProviderContext.Restore(context).Text : null);
  }

  [Fact]
  public async Task IdleProviderFlushesBothWritersBeforeTheNextUpdate() {
    var waiting = Signal();
    var release = Signal();
    var client = new ScriptedChatClient(token => PausedReasoning(waiting, release, token));
    var output = new ConcurrentQueue<LlmResponseChunk>();
    var reasoning = new ConcurrentQueue<LlmResponseChunk>();

    Task<LlmResponseRunner.Result> run = LlmResponseRunner.RunAsync(client,
        [new(ChatRole.User, "go")], new ChatOptions(), Guid.NewGuid(),
        chunk => CaptureAsync(output, chunk), (_, _) => Task.CompletedTask, CancellationToken.None,
        chunk => CaptureAsync(reasoning, chunk));

    try {
      await waiting.Task.WaitAsync(TestTimeout);
      Assert.Equal("kept", await WaitTextAsync(output));
      Assert.Equal("thought", await WaitTextAsync(reasoning));
      Assert.All(output.Concat(reasoning), chunk => Assert.False(chunk.IsFinal));
      Assert.False(run.IsCompleted);
      Assert.Equal(1, client.Calls);
    } finally {
      release.TrySetResult();
    }

    LlmResponseRunner.Result response = await run.WaitAsync(TestTimeout);
    Assert.Null(response.Failure);
    Assert.Single(output, chunk => chunk.IsFinal);
    Assert.Single(reasoning, chunk => chunk.IsFinal);
  }

  [Fact]
  public async Task WithoutReasoningCallbackReasoningStaysIgnored() {
    Guid requestId = Guid.NewGuid();
    var client = new ScriptedChatClient(token => Script(token));
    var output = new ConcurrentQueue<LlmResponseChunk>();

    LlmResponseRunner.Result response = await LlmResponseRunner.RunAsync(client,
        [new(ChatRole.User, "go")], new ChatOptions(), requestId,
        chunk => CaptureAsync(output, chunk), (_, _) => Task.CompletedTask, CancellationToken.None)
        .WaitAsync(TestTimeout);

    Assert.Null(response.Failure);
    Assert.Equal("answer tail", string.Concat(output.Select(chunk => chunk.Text)));
    Assert.Single(output, chunk => chunk.IsFinal);
    Assert.Equal(1, client.Calls);
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> Script(
      [EnumeratorCancellation] CancellationToken token) {
    await Task.CompletedTask;
    token.ThrowIfCancellationRequested();
    yield return new() { Contents = [new TextReasoningContent("reasoning ")] };
    yield return new() { Contents = [new TextContent("answer ")] };
    yield return new() { Contents = [new TextReasoningContent("more") { ProtectedData = "opaque-token" }] };
    yield return new() { Contents = [new TextContent("tail")] };
    yield return Finish();
  }

  // Yields reasoned text, signals waiting, then finishes only after release.
  private static async IAsyncEnumerable<ChatResponseUpdate> PausedReasoning(
      TaskCompletionSource waiting, TaskCompletionSource release, [EnumeratorCancellation] CancellationToken token) {
    yield return new() { Contents = [new TextContent("kept"), new TextReasoningContent("thought")] };
    waiting.TrySetResult();
    await release.Task.WaitAsync(token);
    yield return Finish();
  }

  // Reasoned content completes only with provider context on its final update.
  private static ChatResponseUpdate Finish() {
    ChatResponseUpdate update = new() { FinishReason = ChatFinishReason.Stop };
    LlmProviderContext.SetResponse(update, LlmProviderContext.CaptureChat(
        new LlmProviderScope(Guid.NewGuid(), 0, LlmApi.OpenAIChatCompletions, "fixture-model"),
        new ChatMessage(ChatRole.Assistant, [new TextContent("done")])));
    return update;
  }

  private static Task CaptureAsync(ConcurrentQueue<LlmResponseChunk> chunks, LlmResponseChunk chunk) {
    chunks.Enqueue(chunk);
    return Task.CompletedTask;
  }

  private static async Task<string> WaitTextAsync(ConcurrentQueue<LlmResponseChunk> chunks) {
    using var timeout = new CancellationTokenSource(TestTimeout);
    while (true) {
      string text = string.Concat(chunks.Select(chunk => chunk.Text));
      if (text.Length > 0)
        return text;
      await Task.Delay(20, timeout.Token);
    }
  }
}

#pragma warning restore MEAI001
