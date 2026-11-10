using System.Collections;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

// Real Windows ownership checks use only children and temporary files created by these tests.
[SupportedOSPlatform("windows")]
public sealed class ChatProcessesTests {
  private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

  [Fact]
  public async Task ShellExitPreservesItsPipeHoldingChildUntilChatShutdown() {
    if (!OperatingSystem.IsWindows())
      return;
    string temporary = Directory.CreateTempSubdirectory("urd-shell-").FullName;
    string directory = Directory.CreateDirectory(Path.Combine(temporary, "sp ace ☃")).FullName;
    try {
      await using var chat = new ChatProcesses(new GitBash());
      using var output = new MemoryStream();
      Dictionary<string, string> environment = ShellEnvironment();
      environment["URD_TEST"] = "supplied ☃ value";
      await using ShellProcess call = chat.StartBash("""
          if shopt -q login_shell; then exit 90; fi
          printf '%s\n' '☃ "quoted" \tail\' "$URD_TEST"
          pwd
          sleep 60 & child=$!
          cat /proc/$child/winpid > survivor.pid
          exit 3
          """, directory, environment, bytes => output.Write(bytes.Span));
      Assert.Equal(3, await call.WaitForExitAsync().WaitAsync(Deadline));
      string text = Encoding.UTF8.GetString(output.ToArray());
      Assert.Contains("☃ \"quoted\" \\tail\\", text, StringComparison.Ordinal);
      Assert.Contains("supplied ☃ value", text, StringComparison.Ordinal);
      Assert.Contains("sp ace ☃", text, StringComparison.Ordinal);
      using Process survivor = OpenChild(Path.Combine(directory, "survivor.pid"));
      call.LeaveBackgroundProcesses();
      await call.DisposeAsync();
      Assert.False(survivor.HasExited);
      await chat.KillAllAsync().WaitAsync(Deadline);
      await survivor.WaitForExitAsync().WaitAsync(Deadline);
      Assert.Throws<ObjectDisposedException>(() => chat.StartBash(":", directory, environment, _ => { }));
    } finally {
      Directory.Delete(temporary, recursive: true);
    }
  }

  [Fact]
  public async Task CancelDrainsOnlyTheActiveCallWhileAnEarlierSurvivorLives() {
    if (!OperatingSystem.IsWindows())
      return;
    string directory = Directory.CreateTempSubdirectory("urd-shell-cancel-").FullName;
    try {
      await using var chat = new ChatProcesses(new GitBash());
      Dictionary<string, string> environment = ShellEnvironment();
      await using ShellProcess earlier = chat.StartBash(
          "sleep 60 & child=$!; cat /proc/$child/winpid > earlier.pid", directory, environment, _ => { });
      Assert.Equal(0, await earlier.WaitForExitAsync().WaitAsync(Deadline));
      using Process survivor = OpenChild(Path.Combine(directory, "earlier.pid"));
      earlier.LeaveBackgroundProcesses();
      await using ShellProcess active = chat.StartBash(
          "sleep 60 & child=$!; cat /proc/$child/winpid > active.pid; sleep 60", directory, environment, _ => { });
      await WaitForFileAsync(Path.Combine(directory, "active.pid"));
      using Process child = OpenChild(Path.Combine(directory, "active.pid"));
      Assert.Throws<InvalidOperationException>(active.LeaveBackgroundProcesses);
      await Task.WhenAll(active.CancelAsync(), active.CancelAsync()).WaitAsync(Deadline);
      await child.WaitForExitAsync().WaitAsync(Deadline);
      Assert.False(survivor.HasExited);
      await chat.KillAllAsync().WaitAsync(Deadline);
      await survivor.WaitForExitAsync().WaitAsync(Deadline);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task OutputFailureIsReportedAgainAtChatCleanupEvenAfterAnotherLaunch() {
    if (!OperatingSystem.IsWindows())
      return;
    var chat = new ChatProcesses(new GitBash());
    string directory = Directory.CreateTempSubdirectory("urd-shell-output-").FullName;
    var callbackFailure = new InvalidOperationException("owned test callback failure");
    try {
      ShellProcess failed = chat.StartBash("printf ready; sleep 60", directory, ShellEnvironment(), _ => throw callbackFailure);
      Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => failed.WaitForExitAsync().WaitAsync(Deadline));
      Assert.Same(callbackFailure, failure);
      await using ShellProcess next = chat.StartBash(":", directory, ShellEnvironment(), _ => { });
      Assert.Equal(0, await next.WaitForExitAsync().WaitAsync(Deadline));
      next.LeaveBackgroundProcesses();
      AggregateException cleanup = await Assert.ThrowsAsync<AggregateException>(() => chat.KillAllAsync().WaitAsync(Deadline));
      Assert.Contains(callbackFailure, cleanup.Flatten().InnerExceptions);
    } finally {
      try {
        await chat.DisposeAsync().AsTask().WaitAsync(Deadline);
      } catch (AggregateException failure) when (failure.Flatten().InnerExceptions.Contains(callbackFailure)) {
        // The asserted output fault stays sticky; disposal still joins every owned process.
      }
      Directory.Delete(directory, recursive: true);
    }
  }

  private static Dictionary<string, string> ShellEnvironment() => Environment.GetEnvironmentVariables()
      .Cast<DictionaryEntry>()
      .Where(entry => !((string)entry.Key).StartsWith('=') && (string)entry.Key is not "BASH_ENV" and not "ENV")
      .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.OrdinalIgnoreCase);

  private static Process OpenChild(string path) {
    Process child = Process.GetProcessById(int.Parse(File.ReadAllText(path).Trim(), System.Globalization.CultureInfo.InvariantCulture));
    _ = child.Handle; // Keep this process identity, rather than later observing a reused PID.
    return child;
  }

  private static async Task WaitForFileAsync(string path) {
    var elapsed = Stopwatch.StartNew();
    while (!File.Exists(path) || new FileInfo(path).Length == 0) {
      if (elapsed.Elapsed >= Deadline)
        throw new TimeoutException("Owned shell did not publish its child PID.");
      await Task.Delay(20);
    }
  }
}
