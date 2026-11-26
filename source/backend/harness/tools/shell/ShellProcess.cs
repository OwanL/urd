using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Urd.Core.Tools;

// One shell and its nested job; a successful handoff leaves only the job chat-owned.
[SupportedOSPlatform("windows")]
internal sealed class ShellProcess : IAsyncDisposable {
  private readonly object stateLock = new();
  private readonly SafeFileHandle job;
  private readonly Task<int?> completion;
  private Task? cancellation;
  private bool keepBackgroundProcesses;
  private bool reclaimed;

  internal ShellProcess(SafeFileHandle job, SafeFileHandle process, SafeFileHandle output,
      Action<ReadOnlyMemory<byte>> receiveOutput) {
    this.job = job;
    completion = Task.Run(() => ReadUntilShellExitAsync(process, output, receiveOutput));
    // Faults remain visible through Wait/Cancel/Dispose even if nobody is currently waiting.
    _ = completion.ContinueWith(task => _ = task.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
  }

  public Task<int?> WaitForExitAsync() => completion;

  public Task CancelAsync() {
    lock (stateLock) {
      if (keepBackgroundProcesses)
        return completion;
      return cancellation ??= CancelCoreAsync();
    }
  }

  public void KeepBackgroundProcesses() {
    lock (stateLock) {
      if (!completion.IsCompletedSuccessfully || cancellation is not null)
        throw new InvalidOperationException("Only a completed, uncancelled shell can leave background processes.");
      keepBackgroundProcesses = true;
      TryCloseEmptyJob();
    }
  }

  public async ValueTask DisposeAsync() {
    await CancelAsync().ConfigureAwait(false);
    TryCloseEmptyJob();
  }

  // The chat retains live nested jobs, and drops empty historical calls on the next launch.
  internal bool TryCloseEmptyJob() {
    lock (stateLock) {
      if (reclaimed)
        return completion.IsCompletedSuccessfully && (cancellation is null || cancellation.IsCompletedSuccessfully);
      if (!completion.IsCompleted || cancellation is { IsCompleted: false } || WindowsShell.ActiveProcesses(job) != 0)
        return false;
      job.Dispose();
      reclaimed = true;
      return completion.IsCompletedSuccessfully && (cancellation is null || cancellation.IsCompletedSuccessfully);
    }
  }

  internal Task CloseWithChatAsync() {
    lock (stateLock) {
      keepBackgroundProcesses = false;
      // A previous call cancellation or chat shutdown may not have emptied the job.
      if (cancellation is null || cancellation.IsFaulted)
        cancellation = CancelCoreAsync();
      return cancellation;
    }
  }

  private async Task CancelCoreAsync() {
    await Task.Yield(); // Publish the task before cleanup can finish or another operation can reclaim it.
    var failures = new List<Exception>();
    try {
      bool empty;
      lock (stateLock) {
        empty = reclaimed;
        if (!empty)
          WindowsShell.Terminate(job);
      }
      if (!empty)
        await WindowsShell.WaitForEmptyAsync(job).ConfigureAwait(false);
    } catch (Exception failure) {
      failures.Add(failure);
    }
    bool jobEmpty = failures.Count == 0;
    try {
      await completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    } catch (Exception failure) {
      failures.Add(failure);
    }
    if (jobEmpty && completion.IsCompleted) {
      lock (stateLock) {
        job.Dispose();
        reclaimed = true;
      }
    }
    if (failures.Count != 0)
      throw new AggregateException("Shell cancellation or output cleanup failed.", failures);
  }

  internal static void DrainAvailableOutput(Stream stream, int available, byte[] buffer, Action<ReadOnlyMemory<byte>> receiveOutput) {
    while (available > 0) {
      int read = stream.Read(buffer, 0, Math.Min(buffer.Length, available));
      if (read == 0)
        return; // The pipe can close after the peek. Only the process handle establishes shell exit.
      receiveOutput(buffer.AsMemory(0, read));
      available -= read;
    }
  }

  private async Task<int?> ReadUntilShellExitAsync(SafeFileHandle process, SafeFileHandle output,
      Action<ReadOnlyMemory<byte>> receiveOutput) {
    using (process)
    using (output) {
      try {
        using var stream = new FileStream(output, FileAccess.Read, 1, isAsync: false);
        byte[] buffer = new byte[4096];
        int? exitCode = null;
        do {
          exitCode = WindowsShell.ExitCode(process);
          // Drain only this snapshot. A child continually writing cannot postpone shell completion.
          DrainAvailableOutput(stream, WindowsShell.AvailableOutput(output), buffer, receiveOutput);
          if (exitCode is null)
            await Task.Delay(10).ConfigureAwait(false);
        } while (exitCode is null);
        return exitCode;
      } catch (Exception failure) {
        try {
          WindowsShell.Terminate(job);
          await WindowsShell.WaitForEmptyAsync(job).ConfigureAwait(false);
        } catch (Exception cleanup) {
          throw new AggregateException("Shell output failed and process cleanup also failed.", failure, cleanup);
        }
        throw;
      }
    }
  }
}
