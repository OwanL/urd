using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Urd.Core.Tools;

// Owns the chat job and every nested call job, including completed-call survivors.
[SupportedOSPlatform("windows")]
internal sealed class ChatProcesses : IAsyncDisposable {
  private readonly object gate = new();
  private readonly GitBash bash;
  private readonly List<ShellProcess> calls = [];
  private SafeFileHandle? job;
  private Task? shutdown;

  public ChatProcesses(GitBash bash) {
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("Shell process ownership currently requires Windows.");
    ArgumentNullException.ThrowIfNull(bash);
    this.bash = bash;
  }

  public ShellProcess StartBash(string command, string directory,
      IReadOnlyDictionary<string, string> environment, Action<ReadOnlyMemory<byte>> receiveOutput) {
    ArgumentNullException.ThrowIfNull(command);
    ArgumentNullException.ThrowIfNull(environment);
    ArgumentNullException.ThrowIfNull(receiveOutput);
    if (command.Contains('\0'))
      throw new ArgumentException("Shell commands cannot contain NUL.", nameof(command));
    directory = Path.GetFullPath(directory);
    lock (gate) {
      ObjectDisposedException.ThrowIf(shutdown is not null, this);
      calls.RemoveAll(call => call.TryReclaim());
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
    lock (gate)
      return shutdown ??= KillCoreAsync();
  }

  public ValueTask DisposeAsync() => new(KillAllAsync());

  private async Task KillCoreAsync() {
    await Task.Yield(); // Fence admission before starting cleanup outside the chat lock.
    var failures = new List<Exception>();
    try {
      if (job is not null) {
        try {
          WindowsShell.Terminate(job);
          await WindowsShell.WaitForEmptyAsync(job).ConfigureAwait(false);
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
    } finally {
      job?.Dispose();
    }
    if (failures.Count != 0)
      throw new AggregateException("Chat process cleanup failed.", failures);
    calls.Clear();
  }
}
