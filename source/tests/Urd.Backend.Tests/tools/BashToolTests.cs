using System.Collections;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

// The shell adapter owns cancellation, visible tail/spill output and completed-call handoff.
[SupportedOSPlatform("windows")]
public sealed class BashToolTests {
  private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

  [Theory]
  [InlineData(0)]
  [InlineData(7)]
  public async Task ShortOutputUsesSuppliedEnvironmentAndReturnsTheExitStatus(int exitCode) {
    if (!OperatingSystem.IsWindows())
      return;
    string directory = Directory.CreateTempSubdirectory("urd-bash-tool-").FullName;
    try {
      var bash = new GitBash();
      await using var processes = new ChatProcesses(bash);
      Dictionary<string, string> environment = ShellEnvironment();
      environment["URD_TEST"] = "supplied ☃";
      var tool = new BashTool(processes, bash, environment);
      ToolOutput result = await tool.RunAsync(Arguments($"printf '%s\\n' \"$URD_TEST\"; printf error >&2; pwd; exit {exitCode}"),
          Context(directory), CancellationToken.None).WaitAsync(Deadline);
      Assert.Equal(exitCode != 0, result.IsError);
      Assert.Contains("supplied ☃", result.Text, StringComparison.Ordinal);
      Assert.Contains("error", result.Text, StringComparison.Ordinal);
      Assert.Contains(Path.GetFileName(directory), result.Text, StringComparison.Ordinal);
      JsonElement details = result.Details!.Value;
      Assert.Equal(exitCode, details.GetProperty("exitCode").GetInt32());
      Assert.True(details.GetProperty("durationMs").GetInt64() >= 0);
      Assert.Equal(JsonValueKind.Null, details.GetProperty("fullOutputPath").ValueKind);
      Assert.False(tool.ParametersSchema.GetProperty("properties").TryGetProperty("background", out _));
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task OversizedOutputKeepsTheTailAndACompleteReadableSpill(bool singleLine) {
    if (!OperatingSystem.IsWindows())
      return;
    string directory = Directory.CreateTempSubdirectory("urd-bash-spill-test-").FullName;
    string? spill = null;
    try {
      var bash = new GitBash();
      await using var processes = new ChatProcesses(bash);
      var tool = new BashTool(processes, bash, ShellEnvironment());
      string command = singleLine ? "printf '☃%.0s' {1..40000}"
          : "for ((i=0; i<5000; i++)); do printf '☃-%04d\\n' \"$i\"; done";
      ToolOutput result = await tool.RunAsync(Arguments(command), Context(directory), CancellationToken.None).WaitAsync(Deadline);
      JsonElement details = result.Details!.Value;
      spill = details.GetProperty("fullOutputPath").GetString();
      Assert.False(result.IsError);
      Assert.NotNull(spill);
      Assert.StartsWith(Path.GetTempPath(), spill, StringComparison.OrdinalIgnoreCase);
      Assert.Contains(spill, result.Text, StringComparison.Ordinal);
      JsonElement truncation = details.GetProperty("truncation");
      Assert.True(truncation.GetProperty("truncated").GetBoolean());
      Assert.True(truncation.GetProperty("outputBytes").GetInt32() <= 50 * 1024);
      Assert.True(truncation.GetProperty("outputLines").GetInt32() <= 2000);
      string full = await File.ReadAllTextAsync(spill, new UTF8Encoding(false, true));
      Assert.Equal(singleLine ? string.Concat(Enumerable.Repeat("☃", 40000))
          : string.Concat(Enumerable.Range(0, 5000).Select(index => $"☃-{index:D4}\n")), full);
      Assert.DoesNotContain("\uFFFD", result.Text, StringComparison.Ordinal);
      Assert.Equal(singleLine, truncation.GetProperty("lastLinePartial").GetBoolean());
      if (!singleLine) {
        Assert.StartsWith("☃-3000\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("☃-4999", result.Text, StringComparison.Ordinal);
      }
    } finally {
      if (spill is not null)
        File.Delete(spill);
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task TimeoutAndCallerCancellationCleanTheirCallButKeepAnEarlierCompletedServer() {
    if (!OperatingSystem.IsWindows())
      return;
    string directory = Directory.CreateTempSubdirectory("urd-bash-cancel-test-").FullName;
    try {
      var bash = new GitBash();
      await using var processes = new ChatProcesses(bash);
      var tool = new BashTool(processes, bash, ShellEnvironment());
      ToolOutput earlier = await tool.RunAsync(Arguments("sleep 60 & child=$!; cat /proc/$child/winpid > earlier.pid; exit 3"),
          Context(directory), CancellationToken.None).WaitAsync(Deadline);
      Assert.True(earlier.IsError);
      using Process survivor = Process.GetProcessById(int.Parse((await File.ReadAllTextAsync(Path.Combine(directory, "earlier.pid"))).Trim(),
          System.Globalization.CultureInfo.InvariantCulture));
      _ = survivor.Handle;
      ToolOutput timedOut = await tool.RunAsync(Arguments("printf partial☃; sleep 60", timeout: 1),
          Context(directory), CancellationToken.None).WaitAsync(Deadline);
      Assert.True(timedOut.IsError);
      Assert.True(timedOut.Details!.Value.GetProperty("timedOut").GetBoolean());
      Assert.Contains("partial☃", timedOut.Text, StringComparison.Ordinal);
      Assert.Contains("timed out", timedOut.Text, StringComparison.Ordinal);
      Assert.False(survivor.HasExited);
      using var cancellation = new CancellationTokenSource();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.RunAsync(Arguments("printf ready; sleep 60"),
          Context(directory, _ => cancellation.Cancel()), cancellation.Token).WaitAsync(Deadline));
      Assert.False(survivor.HasExited);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.RunAsync(Arguments("touch must-not-exist"),
          Context(directory), cancellation.Token).WaitAsync(Deadline));
      Assert.False(File.Exists(Path.Combine(directory, "must-not-exist")));
      await processes.KillAllAsync().WaitAsync(Deadline);
      await survivor.WaitForExitAsync().WaitAsync(Deadline);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void DecoderPreservesCharactersSplitAcrossCallbacksAndStripsTheInitialBom() {
    using var output = new BashOutput();
    foreach (byte value in Encoding.UTF8.GetBytes("\uFEFF☃😊\n"))
      output.Append(new byte[] { value });
    output.Finish();
    ToolOutput result = output.Result(0, 0, false, null, "test bash");
    Assert.Equal("☃😊\n", result.Text);
    Assert.False(result.IsError);
  }

  private static JsonElement Arguments(string command, double? timeout = null) => timeout is { } seconds
      ? JsonSerializer.SerializeToElement(new { command, timeout = seconds })
      : JsonSerializer.SerializeToElement(new { command });

  private static ToolContext Context(string directory, Action<ToolOutput>? progress = null) =>
      new(directory, Guid.NewGuid(), "owned-test-call", false, progress ?? (_ => { }));

  private static Dictionary<string, string> ShellEnvironment() => Environment.GetEnvironmentVariables()
      .Cast<DictionaryEntry>()
      .Where(entry => !((string)entry.Key).StartsWith('=') && (string)entry.Key is not "BASH_ENV" and not "ENV")
      .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.OrdinalIgnoreCase);
}
