using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Tools;

public sealed class FileMutationQueueTests : IDisposable {
  private readonly string directory = Directory.CreateTempSubdirectory("urd-file-queue-").FullName;
  private readonly FileMutationQueue files = new();

  [Fact]
  public async Task MutationsEnterInCallOrderAndReadWaitsForEarlierMutations() {
    string path = Path.Combine(directory, "ordered.txt");
    var entered = Signal();
    var release = Signal();
    var first = files.RunMutationAsync(path, async cancellation => {
      entered.SetResult();
      await release.Task.WaitAsync(TestTimeout, cancellation);
      await File.WriteAllTextAsync(path, "first", cancellation);
      return 1;
    }, CancellationToken.None);
    await entered.Task.WaitAsync(TestTimeout);
    var second = files.RunMutationAsync(Path.Combine(directory, ".", "ordered.txt"), async cancellation => {
      Assert.Equal("first", await File.ReadAllTextAsync(path, cancellation));
      await File.WriteAllTextAsync(path, "second", cancellation);
      return 2;
    }, CancellationToken.None);
    var read = files.ReadAfterMutationsAsync(path, cancellation => File.ReadAllTextAsync(path, cancellation), CancellationToken.None);
    try {
      Assert.False(second.IsCompleted);
      Assert.False(read.IsCompleted);
    } finally {
      release.TrySetResult();
    }
    int[] results = await Task.WhenAll(first, second).WaitAsync(TestTimeout);
    Assert.Equal([1, 2], results);
    Assert.Equal("second", await read.WaitAsync(TestTimeout));
  }

  [Fact]
  public async Task FailedMutationDoesNotPoisonFollowingWork() {
    string path = Path.Combine(directory, "failed.txt");
    await Assert.ThrowsAsync<IOException>(() => files.RunMutationAsync<int>(path,
      _ => throw new IOException("fixture failure"), CancellationToken.None));
    Assert.Equal(7, await files.RunMutationAsync(path, _ => Task.FromResult(7), CancellationToken.None).WaitAsync(TestTimeout));
  }

  public void Dispose() => Directory.Delete(directory, recursive: true);
}
