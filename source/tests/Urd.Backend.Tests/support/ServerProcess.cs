using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Urd.Server;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Support;

// Runs the actual CLI in an isolated environment; termination targets only this fixture's child process.
internal sealed class ServerProcess : IAsyncDisposable {
  private readonly Process process;
  private readonly Task<string> errors;

  internal ServerProcess(string[] arguments, IReadOnlyDictionary<string, string>? environment = null) {
    string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
    var start = new ProcessStartInfo(dotnet) {
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      WorkingDirectory = AppContext.BaseDirectory
    };
    start.ArgumentList.Add(typeof(Program).Assembly.Location);
    foreach (string argument in arguments)
      start.ArgumentList.Add(argument);
    if (environment is not null)
      foreach ((string key, string value) in environment)
        start.Environment[key] = value;
    process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test server process.");
    errors = process.StandardError.ReadToEndAsync();
  }

  internal StreamReader Output => process.StandardOutput;
  internal Task<string> ReadErrorsAsync() => errors.WaitAsync(TestTimeout);

  internal async Task<Uri> WaitForReadyAsync() {
    string? line = await Output.ReadLineAsync().WaitAsync(TestTimeout);
    if (line is null)
      throw new InvalidOperationException("The server exited before readiness.");
    using JsonDocument message = JsonDocument.Parse(line);
    if (message.RootElement.GetProperty("event").GetString() != "urd-ready")
      throw new InvalidOperationException("The server emitted an unexpected readiness event.");
    return new Uri(message.RootElement.GetProperty("origin").GetString()!);
  }

  internal async Task CrashRootAsync() {
    if (!process.HasExited) {
      try {
        process.Kill();
      } catch (InvalidOperationException) when (process.HasExited) {
        // It exited between the check and Kill; still join the process below.
      }
    }

    await process.WaitForExitAsync().WaitAsync(TestTimeout);
  }

  internal async Task<int> WaitForExitAsync() {
    await process.WaitForExitAsync().WaitAsync(TestTimeout);
    return process.ExitCode;
  }

  internal async Task TerminateAsync() {
    if (!process.HasExited) {
      try {
        process.Kill(entireProcessTree: true);
      } catch (InvalidOperationException) when (process.HasExited) {
        // It exited between the check and Kill; still join the process below.
      }
    }

    await process.WaitForExitAsync().WaitAsync(TestTimeout);
  }

  public async ValueTask DisposeAsync() {
    await TerminateAsync();
    await ReadErrorsAsync();
    process.Dispose();
  }
}
