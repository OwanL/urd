using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Urd.Core.Tools;

// Runs one finite shell call; a completed shell may leave servers owned by the chat.
[SupportedOSPlatform("windows")]
internal sealed class BashCommand(ChatProcesses processes, GitBash bash, IReadOnlyDictionary<string, string> environment) {
  internal async Task<ToolOutput> RunAsync(JsonElement arguments, ToolContext context, CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    var elapsed = Stopwatch.StartNew();
    var output = new BashOutput();
    Exception? executionFailure = null;
    ToolOutput? result = null;
    try {
      result = await RunCallAsync(arguments, context, output, elapsed, cancellation).ConfigureAwait(false);
    } catch (Exception failure) {
      executionFailure = failure;
    }
    try {
      output.Dispose();
    } catch (Exception cleanup) {
      throw new ToolCleanupException("Bash output cleanup failed.", executionFailure is null
          ? cleanup : new AggregateException(executionFailure, cleanup));
    }
    if (executionFailure is not null)
      ExceptionDispatchInfo.Capture(executionFailure).Throw();
    return result!;
  }

  private async Task<ToolOutput> RunCallAsync(JsonElement arguments, ToolContext context,
      BashOutput output, Stopwatch elapsed, CancellationToken cancellation) {
    try {
      string command = ToolArguments.String(arguments, "command");
      double? timeoutSeconds = TimeoutSeconds(arguments);
      long lastProgressMs = -100;
      void ReceiveOutput(ReadOnlyMemory<byte> bytes) {
        output.Append(bytes);
        long now = elapsed.ElapsedMilliseconds;
        if (now - lastProgressMs < 100 || cancellation.IsCancellationRequested)
          return;
        lastProgressMs = now;
        context.ReportProgress(new ToolOutput(output.Preview(), [], JsonSerializer.SerializeToElement(new {
          durationMs = now, running = true
        })));
      }
      cancellation.ThrowIfCancellationRequested();
      (int? exitCode, bool timedOut) = await RunShellAsync(command, context.WorkingDirectory,
          timeoutSeconds, ReceiveOutput, cancellation).ConfigureAwait(false);
      output.Finish();
      return output.Result(elapsed.ElapsedMilliseconds, exitCode, timedOut, timeoutSeconds, bash.Executable);
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      throw;
    } catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or KeyNotFoundException
        or IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException) {
      return output.Failure(failure, elapsed.ElapsedMilliseconds, bash.Executable);
    }
  }

  private async Task<(int? ExitCode, bool TimedOut)> RunShellAsync(string command, string directory,
      double? timeoutSeconds, Action<ReadOnlyMemory<byte>> receiveOutput, CancellationToken cancellation) {
    ShellProcess shell;
    try {
      shell = processes.StartBash(command, directory, environment, receiveOutput);
    } catch (AggregateException failure) {
      throw new ToolCleanupException("Bash launch cleanup failed.", failure);
    }
    Exception? executionFailure = null;
    (int? ExitCode, bool TimedOut) result = default;
    try {
      result = await WaitForShellAsync(shell, timeoutSeconds, cancellation).ConfigureAwait(false);
    } catch (Exception failure) {
      executionFailure = failure;
    }
    // Explicit cancellation also joins cleanup with an uncancelled wait before it can propagate.
    try {
      await shell.DisposeAsync().ConfigureAwait(false);
    } catch (Exception cleanup) {
      throw new ToolCleanupException("Bash process cleanup failed.", executionFailure is null
          ? cleanup : new AggregateException(executionFailure, cleanup));
    }
    if (executionFailure is not null)
      ExceptionDispatchInfo.Capture(executionFailure).Throw();
    return result;
  }

  private static async Task<(int? ExitCode, bool TimedOut)> WaitForShellAsync(ShellProcess shell,
      double? timeoutSeconds, CancellationToken cancellation) {
    using var timeout = new CancellationTokenSource();
    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
    if (timeoutSeconds is { } seconds)
      timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
    int? exitCode;
    try {
      exitCode = await shell.WaitForExitAsync().WaitAsync(wait.Token).ConfigureAwait(false);
    } catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellation.IsCancellationRequested) {
      await shell.CancelAsync().ConfigureAwait(false);
      cancellation.ThrowIfCancellationRequested();
      return (await shell.WaitForExitAsync().ConfigureAwait(false), true);
    }
    cancellation.ThrowIfCancellationRequested();
    shell.LeaveBackgroundProcesses();
    return (exitCode, false);
  }

  private static double? TimeoutSeconds(JsonElement arguments) {
    if (!arguments.TryGetProperty("timeout", out JsonElement value))
      return null;
    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double seconds)
        || !double.IsFinite(seconds) || seconds <= 0 || seconds * 1000 > int.MaxValue)
      throw new ArgumentException("timeout must be a finite positive number of seconds, at most 2147483.647.");
    return seconds;
  }
}
