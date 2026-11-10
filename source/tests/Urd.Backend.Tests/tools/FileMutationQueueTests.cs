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
    var first = files.ChangeAsync(path, async cancellation => {
      entered.SetResult();
      await release.Task.WaitAsync(TestTimeout, cancellation);
      await File.WriteAllTextAsync(path, "first", cancellation);
      return 1;
    }, CancellationToken.None);
    await entered.Task.WaitAsync(TestTimeout);
    var second = files.ChangeAsync(Path.Combine(directory, ".", "ordered.txt"), async cancellation => {
      Assert.Equal("first", await File.ReadAllTextAsync(path, cancellation));
      await File.WriteAllTextAsync(path, "second", cancellation);
      return 2;
    }, CancellationToken.None);
    var read = files.ReadAsync(path, cancellation => File.ReadAllTextAsync(path, cancellation), CancellationToken.None);
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
  public async Task QueuedCancellationDoesNotReleaseEarlierMutationOrBlockIndependentFiles() {
    string path = Path.Combine(directory, "held.txt");
    var entered = Signal();
    var release = Signal();
    using var cancellation = new CancellationTokenSource();
    var first = files.ChangeAsync(path, async token => {
      entered.SetResult();
      await release.Task.WaitAsync(TestTimeout, token);
      return 1;
    }, CancellationToken.None);
    await entered.Task.WaitAsync(TestTimeout);
    int abandonedCalls = 0;
    var abandoned = files.ChangeAsync(path, _ => Task.FromResult(Interlocked.Increment(ref abandonedCalls)), cancellation.Token);
    await cancellation.CancelAsync();
    var later = files.ChangeAsync(path, _ => Task.FromResult(3), CancellationToken.None);
    var read = files.ReadAsync(path, _ => Task.FromResult(4), CancellationToken.None);
    try {
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(TestTimeout));
      Assert.False(later.IsCompleted);
      Assert.False(read.IsCompleted);
      Assert.Equal(5, await files.ChangeAsync(Path.Combine(directory, "other.txt"), _ => Task.FromResult(5),
        CancellationToken.None).WaitAsync(TestTimeout));
    } finally {
      release.TrySetResult();
    }
    int[] results = await Task.WhenAll(first, later, read).WaitAsync(TestTimeout);
    Assert.Equal([1, 3, 4], results);
    Assert.Equal(0, Volatile.Read(ref abandonedCalls));
  }

  [Fact]
  public async Task FailedMutationDoesNotPoisonFollowingWork() {
    string path = Path.Combine(directory, "failed.txt");
    await Assert.ThrowsAsync<IOException>(() => files.ChangeAsync<int>(path,
      _ => throw new IOException("fixture failure"), CancellationToken.None));
    Assert.Equal(7, await files.ChangeAsync(path, _ => Task.FromResult(7), CancellationToken.None).WaitAsync(TestTimeout));
  }

  [Fact]
  public async Task PathAliasesShareTheQueueBeforeAFileExists() {
    string path = Path.Combine(directory, "new.txt");
    var aliases = new List<string> { Path.Combine(directory, ".", "new.txt") };
    if (OperatingSystem.IsWindows()) {
      aliases.Add(path.ToUpperInvariant());
      aliases.Add($"/{char.ToLowerInvariant(path[0])}/{path[3..].Replace('\\', '/')}");
    }
    string link = Path.Combine(directory, "linked");
    try {
      Directory.CreateSymbolicLink(link, directory);
      aliases.Add(Path.Combine(link, "new.txt"));
    } catch (UnauthorizedAccessException) {
      // Windows symbolic links require Developer Mode or a privilege not present on every host.
    } catch (PlatformNotSupportedException) {
      // The lexical aliases above still exercise the queue on filesystems without links.
    } catch (IOException failure) when (OperatingSystem.IsWindows() && (failure.HResult & 0xFFFF) == 1314) {
      // Windows reports ERROR_PRIVILEGE_NOT_HELD as IOException when link creation is unavailable.
    }
    var entered = Signal();
    var release = Signal();
    var write = files.ChangeAsync(path, async cancellation => {
      entered.SetResult();
      await release.Task.WaitAsync(TestTimeout, cancellation);
      await File.WriteAllTextAsync(path, "created", cancellation);
      return true;
    }, CancellationToken.None);
    await entered.Task.WaitAsync(TestTimeout);
    var reads = aliases.Select(alias => files.ReadAsync(alias,
      token => File.ReadAllTextAsync(ToolPaths.Resolve(alias, directory), token), CancellationToken.None)).ToArray();
    try {
      Assert.All(reads, read => Assert.False(read.IsCompleted));
      Assert.Equal(Path.GetFullPath(Path.Combine(directory, "..", "outside.txt")), ToolPaths.Resolve("../outside.txt", directory));
    } finally {
      release.TrySetResult();
    }
    Assert.True(await write.WaitAsync(TestTimeout));
    Assert.All(await Task.WhenAll(reads).WaitAsync(TestTimeout), text => Assert.Equal("created", text));
  }

  public void Dispose() => Directory.Delete(directory, recursive: true);
}
