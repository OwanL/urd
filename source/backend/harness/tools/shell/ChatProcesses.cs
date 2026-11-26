using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Urd.Core.Tools;

// Owns the chat job and every nested call job, including completed-call survivors.
[SupportedOSPlatform("windows")]
internal sealed class ChatProcesses : IAsyncDisposable {
  private readonly object stateLock = new();
  private readonly GitBash bash;
  private readonly Func<SafeFileHandle, Task> terminateJob;
  private readonly List<ShellProcess> calls = [];
  private SafeFileHandle? job;
  private Task? shutdown;

  public ChatProcesses(GitBash bash) : this(bash, TerminateJobAsync) { }

  internal ChatProcesses(GitBash bash, Func<SafeFileHandle, Task> terminateJob) {
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("Shell process ownership currently requires Windows.");
    ArgumentNullException.ThrowIfNull(bash);
    ArgumentNullException.ThrowIfNull(terminateJob);
    this.bash = bash;
    this.terminateJob = terminateJob;
  }

  public ShellProcess StartBash(string command, string directory,
      IReadOnlyDictionary<string, string> environment, Action<ReadOnlyMemory<byte>> receiveOutput) {
    ArgumentNullException.ThrowIfNull(command);
    ArgumentNullException.ThrowIfNull(environment);
    ArgumentNullException.ThrowIfNull(receiveOutput);
    if (command.Contains('\0'))
      throw new ArgumentException("Shell commands cannot contain NUL.", nameof(command));
    directory = Path.GetFullPath(directory);
    lock (stateLock) {
      ObjectDisposedException.ThrowIf(shutdown is not null, this);
      calls.RemoveAll(call => call.TryCloseEmptyJob());
      job ??= WindowsShell.CreateJob();
      SafeFileHandle callJob = WindowsShell.CreateJob();
      try {
        (SafeFileHandle process, SafeFileHandle output) = WindowsShell.Start(
            bash.Executable, command, directory, environment, job, callJob);
        var call = new ShellProcess(callJob, process, output, receiveOutput);
        calls.Add(call);
        return call;
      } catch {
        callJob.Dispose();
        throw;
      }
    }
  }

  public Task KillAllAsync() {
    lock (stateLock) {
      // A failed attempt can be retried, but it never reopens shell admission.
      if (shutdown is null || shutdown.IsFaulted)
        shutdown = KillCoreAsync();
      return shutdown;
    }
  }

  public ValueTask DisposeAsync() => new(KillAllAsync());

  private async Task KillCoreAsync() {
    await Task.Yield(); // Fence admission before starting cleanup outside the chat lock.
    var failures = new List<Exception>();
    if (job is not null) {
      try {
        // Keep a job that was not confirmed empty so shutdown can retry it.
        await terminateJob(job).ConfigureAwait(false);
        job.Dispose();
        job = null;
      } catch (Exception failure) {
        failures.Add(failure);
      }
    }
    Task cleanup = Task.WhenAll(calls.Select(call => call.CloseWithChatAsync()));
    try {
      await cleanup.ConfigureAwait(false);
    } catch (Exception failure) {
      failures.Add(cleanup.Exception ?? failure);
    }
    if (failures.Count != 0)
      throw new AggregateException("Chat process cleanup failed.", failures);
    calls.Clear();
  }

  private static async Task TerminateJobAsync(SafeFileHandle job) {
    WindowsShell.Terminate(job);
    await WindowsShell.WaitForEmptyAsync(job).ConfigureAwait(false);
  }
}
