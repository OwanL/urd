namespace Urd.Core.Tools;

// Shared across chats: orders Urd mutations, not bash commands or outside writers.
internal sealed class FileMutationQueue {
  private readonly object gate = new();
  private readonly Dictionary<string, TaskCompletionSource> mutations = new(
    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

  public Task<T> ChangeAsync<T>(string path, Func<CancellationToken, Task<T>> change, CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(change);
    string identity = ToolPaths.Resolve(path, Directory.GetCurrentDirectory());
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task preceding;
    lock (gate) {
      preceding = mutations.TryGetValue(identity, out var pending) ? pending.Task : Task.CompletedTask;
      mutations[identity] = completion;
    }
    return ChangeAfterAsync(identity, preceding, completion, change, cancellation);
  }

  public Task<T> ReadAsync<T>(string path, Func<CancellationToken, Task<T>> read, CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(read);
    string identity = ToolPaths.Resolve(path, Directory.GetCurrentDirectory());
    Task preceding;
    lock (gate)
      preceding = mutations.TryGetValue(identity, out var pending) ? pending.Task : Task.CompletedTask;
    return ReadAfterAsync(preceding, read, cancellation);
  }

  private async Task<T> ChangeAfterAsync<T>(string identity, Task preceding, TaskCompletionSource completion,
      Func<CancellationToken, Task<T>> change, CancellationToken cancellation) {
    try {
      await preceding.WaitAsync(cancellation).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      // Cancel the caller promptly, but retain this slot until earlier effects have stopped.
      _ = ReleaseAfterAsync(identity, preceding, completion);
      throw;
    }
    try {
      cancellation.ThrowIfCancellationRequested();
      return await change(cancellation).ConfigureAwait(false);
    } finally {
      Release(identity, completion);
    }
  }

  private static async Task<T> ReadAfterAsync<T>(Task preceding,
      Func<CancellationToken, Task<T>> read, CancellationToken cancellation) {
    await preceding.WaitAsync(cancellation).ConfigureAwait(false);
    cancellation.ThrowIfCancellationRequested();
    return await read(cancellation).ConfigureAwait(false);
  }

  private async Task ReleaseAfterAsync(string identity, Task preceding, TaskCompletionSource completion) {
    await preceding.ConfigureAwait(false);
    Release(identity, completion);
  }

  private void Release(string identity, TaskCompletionSource completion) {
    lock (gate) {
      if (mutations.TryGetValue(identity, out var pending) && ReferenceEquals(pending, completion))
        mutations.Remove(identity);
      completion.SetResult();
    }
  }
}
