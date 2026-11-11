using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Hosting;

public sealed class ToolHostCrashTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task HostTerminationStopsToolChildWithoutReplay(bool crashRoot) {
    if (!OperatingSystem.IsWindows())
      return;

    using var database = new TemporaryDatabase("urd-host-crash");
    string workingDirectory = Directory.CreateTempSubdirectory("urd-host-crash-").FullName;
    Process? child = null;
    try {
      var builder = WebApplication.CreateBuilder();
      builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
      await using WebApplication provider = builder.Build();
      int providerCalls = 0;
      string command = """
          printf x >> effect.marker
          powershell.exe -NoProfile -WindowStyle Hidden -Command '$tmp="child.pid.tmp"; [IO.File]::WriteAllText($tmp, [string]$PID, [Text.Encoding]::ASCII); [IO.File]::Move($tmp, "child.pid"); Start-Sleep -Seconds 3600'
          """;
      string arguments = JsonSerializer.Serialize(new { command });
      provider.MapPost("/v1/chat/completions", async (HttpContext context) => {
        Interlocked.Increment(ref providerCalls);
        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync(ProviderModelStreams.ChatCalls(arguments: arguments, name: "bash"));
      });
      await provider.StartAsync().WaitAsync(TestTimeout);
      string providerAddress = provider.Services.GetRequiredService<IServer>().Features
          .Get<IServerAddressesFeature>()!.Addresses.Single();
      var providerEndpoint = new Uri(new Uri(providerAddress), "/v1/");
      string[] serverArguments = ["--data-root", Path.GetDirectoryName(database.Path)!, "--port", "0",
          "--working-directory", workingDirectory];

      await using var server = new ServerProcess(serverArguments);
      using var client = CreateClient(await server.WaitForReadyAsync());
      using HttpResponseMessage providerResponse = await client.PostAsJsonAsync("/api/providers/custom",
          new CustomProviderSettings("Crash fixture", LlmApi.OpenAIChatCompletions, providerEndpoint,
              AuthenticationMethod.None), json);
      providerResponse.EnsureSuccessStatusCode();
      ProviderConnection connection = (await providerResponse.Content.ReadFromJsonAsync<ProviderConnection>(json))!;

      using HttpResponseMessage sessionResponse = await client.PostAsJsonAsync("/api/sessions", new { }, json);
      sessionResponse.EnsureSuccessStatusCode();
      using JsonDocument sessionBody = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
      Guid sessionId = sessionBody.RootElement.GetProperty("sessionId").GetGuid();
      var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "run the crash fixture",
          new LlmSelection(connection, "fixture-model", null, ModelFamily.Other, false));
      using HttpResponseMessage submitted = await client.PostAsJsonAsync(
          $"/api/sessions/{sessionId}/message-submissions", submission, json);
      submitted.EnsureSuccessStatusCode();
      UserMessageReceipt receipt = (await submitted.Content.ReadFromJsonAsync<UserMessageReceipt>(json))!;
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      Guid executionId = receipt.ExecutionId ?? throw new InvalidOperationException("Submission was not accepted.");

      string pidPath = Path.Combine(workingDirectory, "child.pid");
      await WaitUntilAsync(() => File.Exists(pidPath) && StoredTools.Read(database).Any(tool => tool.Started));
      Assert.Equal("x", await File.ReadAllTextAsync(Path.Combine(workingDirectory, "effect.marker")));
      child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidPath), CultureInfo.InvariantCulture));
      Assert.Equal("powershell", child.ProcessName, ignoreCase: true);
      Assert.False(child.SafeHandle.IsInvalid);
      Assert.False(child.HasExited);

      if (crashRoot)
        await server.CrashRootAsync();
      else {
        using HttpResponseMessage stop = await client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/executions/{executionId}/stop", new { }, json);
        stop.EnsureSuccessStatusCode();
        StopReceipt? stopReceipt = null;
        using var timeout = new CancellationTokenSource(TestTimeout);
        while (stopReceipt?.Status != StopStatus.Settled) {
          if (stopReceipt is not null)
            await Task.Delay(10, timeout.Token);
          using HttpResponseMessage poll = await client.PostAsJsonAsync(
              $"/api/sessions/{sessionId}/executions/{executionId}/stop", new { }, json, timeout.Token);
          poll.EnsureSuccessStatusCode();
          stopReceipt = await poll.Content.ReadFromJsonAsync<StopReceipt>(json, timeout.Token);
        }
        Assert.Equal(ExecutionOutcome.Cancelled, stopReceipt.Outcome);
      }
      await child.WaitForExitAsync().WaitAsync(TestTimeout);
      Assert.True(child.HasExited);

      HttpClient observedClient = client;
      HttpClient? recoveredClient = null;
      ServerProcess? restarted = null;
      try {
        if (crashRoot) {
          restarted = new ServerProcess(serverArguments);
          recoveredClient = CreateClient(await restarted.WaitForReadyAsync());
          observedClient = recoveredClient;
        }
        using HttpResponseMessage snapshotResponse = await observedClient.GetAsync($"/api/sessions/{sessionId}/snapshot");
        snapshotResponse.EnsureSuccessStatusCode();
        SessionSnapshot snapshot = (await snapshotResponse.Content.ReadFromJsonAsync<SessionSnapshot>(json))!;
        ExecutionOutcome expectedOutcome = crashRoot ? ExecutionOutcome.Failed : ExecutionOutcome.Cancelled;
        Assert.Null(snapshot.UnsettledExecutionId);
        Assert.Equal(executionId, snapshot.LastSettlement?.ExecutionId);
        Assert.Equal(expectedOutcome, snapshot.LastSettlement?.Outcome);

        StoredExecution run = Assert.Single(StoredExecutions.Read(database));
        Assert.Equal(executionId, run.ExecutionId);
        Assert.Equal(expectedOutcome, run.Outcome);
        StoredTool tool = Assert.Single(StoredTools.Read(database));
        Assert.True(tool.Started);
        Assert.Equal(expectedOutcome, tool.Outcome);
        if (crashRoot) {
          Assert.Contains("interrupted", tool.Error ?? "", StringComparison.OrdinalIgnoreCase);
          Assert.Contains("Do not automatically retry", tool.Error ?? "", StringComparison.Ordinal);
        }
        Assert.Equal(1, Volatile.Read(ref providerCalls));
        Assert.Equal("x", await File.ReadAllTextAsync(Path.Combine(workingDirectory, "effect.marker")));
      } finally {
        recoveredClient?.Dispose();
        if (restarted is not null)
          await restarted.DisposeAsync();
        if (crashRoot)
          await server.ReadErrorsAsync();
      }
    } finally {
      if (child is not null) {
        try {
          if (!child.HasExited)
            child.Kill();
          await child.WaitForExitAsync().WaitAsync(TestTimeout);
        } catch (InvalidOperationException) when (child.HasExited) {
          // The owned child exited during cleanup.
        } finally {
          child.Dispose();
        }
      }
      Directory.Delete(workingDirectory, recursive: true);
    }
  }

  private static HttpClient CreateClient(Uri origin) {
    var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) {
      BaseAddress = origin,
      Timeout = TestTimeout
    };
    client.DefaultRequestHeaders.Add("X-Urd-Client", "browser");
    return client;
  }

  private static async Task WaitUntilAsync(Func<bool> condition) {
    using var timeout = new CancellationTokenSource(TestTimeout);
    while (!condition())
      await Task.Delay(10, timeout.Token);
  }
}
