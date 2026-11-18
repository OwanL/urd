using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

public sealed class MarkdownImageExecutionTests {
  [Fact]
  public async Task CapturesSplitAssistantImageWhileStreamingWithoutReimportingTargets() {
    const string first = "`![literal](code.png)`\n\n!";
    const string second = "[screen](screen.png) ![missing](later.png)";
    const string tail = "\n\n![repeat](later.png)\n\n![reference][shot]\n\n[shot]: co";
    const string referenceEnd = "de.png";
    var continueImage = Signal();
    var continueReply = Signal();
    var continueReference = Signal();
    var finishReply = Signal();
    await using var fixture = new ExecutionFixture(new ScriptedChatClient(Reply));
    byte[] png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXioAAAAASUVORK5CYII=");
    string directory = Path.GetDirectoryName(fixture.Database.Path)!;
    await File.WriteAllBytesAsync(Path.Combine(directory, "screen.png"), png);
    await File.WriteAllBytesAsync(Path.Combine(directory, "code.png"), png);

    Guid executionId = await fixture.StartExecutionAsync("Show the screenshot");
    try {
      MessageRef message = await WaitForOpenTextAsync(first);
      Assert.Null(await ReadImageAsync(message, "screen.png"));
      continueImage.SetResult();

      Assert.Equal(message, await WaitForOpenTextAsync(first + second));
      var image = Assert.IsType<DataContent>(await ReadImageAsync(message, "screen.png"));
      Assert.Equal("image/png", image.MediaType);
      Assert.Equal(png, image.Data.ToArray());
      Assert.Null(await ReadImageAsync(message, "code.png"));
      Assert.Null(await ReadImageAsync(message, "later.png"));

      File.Delete(Path.Combine(directory, "screen.png"));
      await File.WriteAllBytesAsync(Path.Combine(directory, "later.png"), png);
      continueReply.SetResult();
      Assert.Equal(message, await WaitForOpenTextAsync(first + second + tail));
      Assert.Null(await ReadImageAsync(message, "later.png"));
      Assert.Equal(png, Assert.IsType<DataContent>(await ReadImageAsync(message, "screen.png")).Data.ToArray());
      Assert.Null(await ReadImageAsync(message, "co"));
      continueReference.SetResult();
      Assert.Equal(message, await WaitForOpenTextAsync(first + second + tail + referenceEnd));
      Assert.Equal(png, Assert.IsType<DataContent>(await ReadImageAsync(message, "code.png")).Data.ToArray());
      finishReply.SetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await fixture.Session.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    } finally {
      continueImage.TrySetResult();
      continueReply.TrySetResult();
      continueReference.TrySetResult();
      finishReply.TrySetResult();
    }

    async Task<MessageRef> WaitForOpenTextAsync(string expected) {
      using var deadline = new CancellationTokenSource(TestTimeout);
      while (StoredLlmResponses.ReadText(fixture.Database) != expected)
        await Task.Delay(10, deadline.Token);
      var history = await fixture.Session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0), deadline.Token);
      var entry = Assert.Single(history.Entries, item => item.Entry.Kind == TranscriptEntryKind.Text);
      Assert.False(entry.Text!.IsClosed);
      return new(entry.Entry.ExecutionId, entry.Entry.EntryId, MessageRole.Assistant, entry.Entry.HistoryRevision);
    }

    async Task<DataContent?> ReadImageAsync(MessageRef message, string path) {
      string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
      return await fixture.Session.Transcript.ReadMarkdownImageAsync(message, key).WaitAsync(TestTimeout);
    }

    async IAsyncEnumerable<ChatResponseUpdate> Reply([EnumeratorCancellation] CancellationToken cancellation) {
      yield return Text(first);
      await continueImage.Task.WaitAsync(cancellation);
      yield return Text(second);
      await continueReply.Task.WaitAsync(cancellation);
      yield return Text(tail);
      await continueReference.Task.WaitAsync(cancellation);
      yield return Text(referenceEnd);
      await finishReply.Task.WaitAsync(cancellation);
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }
  }
}
