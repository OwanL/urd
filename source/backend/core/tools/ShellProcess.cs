using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Urd.Core.Tools;

// One shell and its nested job; a successful handoff leaves only the job chat-owned.
[SupportedOSPlatform("windows")]
internal sealed class ShellProcess : IAsyncDisposable {
  private readonly object gate = new();
  private readonly SafeFileHandle job;
  private readonly Task<int?> completion;
  private Task? cancellation;
  private bool leftBackgroundProcesses;
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
    lock (gate) {
      if (leftBackgroundProcesses)
        return completion;
      return cancellation ??= CancelCoreAsync();
    }
  }

  public void LeaveBackgroundProcesses() {
    lock (gate) {
      if (!completion.IsCompletedSuccessfully || cancellation is not null)
        throw new InvalidOperationException("Only a completed, uncancelled shell can leave background processes.");
      leftBackgroundProcesses = true;
      TryReclaim();
    }
  }

  public async ValueTask DisposeAsync() {
    await CancelAsync().ConfigureAwait(false);
    TryReclaim();
  }

  // The chat retains live nested jobs, and drops empty historical calls on the next launch.
  internal bool TryReclaim() {
    lock (gate) {
      if (reclaimed)
        return completion.IsCompletedSuccessfully && (cancellation is null || cancellation.IsCompletedSuccessfully);
      if (!completion.IsCompleted || cancellation is { IsCompleted: false } || WindowsShell.ActiveProcesses(job) != 0)
        return false;
      job.Dispose();
      reclaimed = true;
      return completion.IsCompletedSuccessfully && (cancellation is null || cancellation.IsCompletedSuccessfully);
    }
  }

  internal async Task CloseWithChatAsync() {
    Task cleanup;
    lock (gate) {
      leftBackgroundProcesses = false;
      cleanup = cancellation ??= CancelCoreAsync();
    }
    try {
      await cleanup.ConfigureAwait(false);
    } finally {
      // Kill-on-close remains the fallback if explicit cleanup failed; the failure is preserved.
      lock (gate) {
        if (completion.IsCompleted) {
          job.Dispose();
          reclaimed = true;
        }
      }
    }
  }

  private async Task CancelCoreAsync() {
    await Task.Yield(); // Publish the task before cleanup can finish or another operation can reclaim it.
    var failures = new List<Exception>();
    try {
      bool empty;
      lock (gate) {
        empty = reclaimed;
        if (!empty)
          WindowsShell.Terminate(job);
      }
      if (!empty)
        await WindowsShell.WaitForEmptyAsync(job).ConfigureAwait(false);
    } catch (Exception failure) {
      failures.Add(failure);
    }
    try {
      await completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    } catch (Exception failure) {
      failures.Add(failure);
    }
    if (failures.Count != 0)
      throw new AggregateException("Shell cancellation or output cleanup failed.", failures);
    lock (gate) {
      job.Dispose();
      reclaimed = true;
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
          int available = WindowsShell.AvailableOutput(output);
          while (available > 0) {
#pragma warning disable S6966 // Synchronous inherited pipe; PeekNamedPipe bounds this nonblocking read.
            int read = stream.Read(buffer, 0, Math.Min(buffer.Length, available));
#pragma warning restore S6966
            if (read == 0)
              throw new EndOfStreamException("Shell output closed while buffered bytes were being read.");
            receiveOutput(buffer.AsMemory(0, read).ToArray());
            available -= read;
          }
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
