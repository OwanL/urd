using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Urd.Backend.Tests.Support;

// Reusable scripted LLM responses for ScriptedChatClient.
internal static class ScriptedResponses {
  internal static ChatResponseUpdate Text(string text) => new() {
    Role = ChatRole.Assistant,
    Contents = [new TextContent(text)]
  };

  internal static async IAsyncEnumerable<ChatResponseUpdate> TextUpdates(IEnumerable<string> updates,
      [EnumeratorCancellation] CancellationToken token) {
    await Task.CompletedTask;
    foreach (string update in updates) {
      token.ThrowIfCancellationRequested();
      yield return Text(update);
    }

    yield return new() { FinishReason = ChatFinishReason.Stop };
  }

  // Yields visible text, signals waiting, then finishes only after release.
  internal static IAsyncEnumerable<ChatResponseUpdate> Paused(TaskCompletionSource waiting,
      TaskCompletionSource release, CancellationToken token,
      TaskCompletionSource? cleanupEntered = null, TaskCompletionSource? cleanupRelease = null) {
    if ((cleanupEntered is null) != (cleanupRelease is null))
      throw new ArgumentException("Cleanup barriers must be supplied together.");
    return Pause(waiting, release, cleanupEntered, cleanupRelease, token);
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> Pause(TaskCompletionSource waiting,
      TaskCompletionSource release, TaskCompletionSource? cleanupEntered, TaskCompletionSource? cleanupRelease,
      [EnumeratorCancellation] CancellationToken token) {
    yield return Text("visible");
    waiting.TrySetResult();
    try {
      await release.Task.WaitAsync(token);
    } finally {
      if (cleanupEntered is not null) {
        cleanupEntered.TrySetResult();
        await cleanupRelease!.Task;
      }
    }
    yield return new() { FinishReason = ChatFinishReason.Stop };
  }

  internal static async IAsyncEnumerable<ChatResponseUpdate> ToolBatch(
      [EnumeratorCancellation] CancellationToken token, params FunctionCallContent[] calls) {
    await Task.CompletedTask;
    token.ThrowIfCancellationRequested();
    yield return new() { Contents = [.. calls], FinishReason = ChatFinishReason.ToolCalls };
    yield return new() { Contents = [new UsageContent(), new TextContent("")] };
  }

  internal static IAsyncEnumerable<ChatResponseUpdate> Complete(CancellationToken token) =>
      TextUpdates(["done"], token);
}
