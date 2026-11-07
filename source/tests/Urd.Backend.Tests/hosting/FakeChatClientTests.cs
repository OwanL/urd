using System.Text;
using Microsoft.Extensions.AI;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

// The composed local client returns deterministic terminal text and honors run cancellation.
public sealed class FakeChatClientTests {
  [Fact]
  public async Task RepeatedRequestsProduceDeterministicTextAndPositiveCompletion() {
    using var client = new FakeChatClient();
    for (int request = 0; request < 2; request++) {
      var text = new StringBuilder();
      ChatFinishReason? finish = null;
      await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([
        new ChatMessage(ChatRole.User, $"input {request}")
      ])) {
        text.Append(update.Text);
        finish = update.FinishReason ?? finish;
      }

      Assert.Equal("Hello from Urd.", text.ToString());
      Assert.Equal(ChatFinishReason.Stop, finish);
    }
  }

  [Fact]
  public async Task CancellationStopsAnAlreadyStartedResponse() {
    using var client = new FakeChatClient();
    using var cancellation = new CancellationTokenSource();
    await using IAsyncEnumerator<ChatResponseUpdate> response = client.GetStreamingResponseAsync([],
        cancellationToken: cancellation.Token).GetAsyncEnumerator();
    Assert.True(await response.MoveNextAsync());
    await cancellation.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response.MoveNextAsync().AsTask());
  }
}
