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
      call.KeepBackgroundProcesses();
      await call.DisposeAsync();
      Assert.False(survivor.HasExited);
      await chat.KillAllAsync().WaitAsync(Deadline);
      await survivor.WaitForExitAsync().WaitAsync(Deadline);
      Assert.Throws<ObjectDisposedException>(() => chat.StartBash(":", directory, environment, _ => { }));
    } finally {
      Directory.Delete(temporary, recursive: true);
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

}
