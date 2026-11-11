using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;
using static Urd.Backend.Tests.Support.ScriptedResponses;

namespace Urd.Backend.Tests.Hosting;

public sealed class ServerProcessTests {
  [Fact]
  public void ToolOptionsDefaultToDriveRootAndPreserveAbsoluteOverrides() {
    string root = Path.GetFullPath(Path.GetTempPath());
    string[] basic = ["--data-root", root, "--port", "0"];
    Assert.Equal(OperatingSystem.IsWindows() ? Path.GetPathRoot(Environment.CurrentDirectory) : null,
        ServerOptions.Parse(basic).WorkingDirectory);
    ServerOptions tools = ServerOptions.Parse([.. basic, "--working-directory", root]);
    Assert.Equal(root, tools.WorkingDirectory);
    Assert.Null(tools.GitBashPath);
    Assert.Equal(Path.Combine(root, "bash.exe"), ServerOptions.Parse([.. basic, "--git-bash", Path.Combine(root, "bash.exe")]).GitBashPath);
    Assert.Throws<ArgumentException>(() => ServerOptions.Parse([.. basic, "--working-directory", "relative"]));
    Assert.Throws<ArgumentException>(() => ServerOptions.Parse([
        .. basic, "--working-directory", root, "--working-directory", root]));
  }

  [Fact]
  public async Task DefaultWorkingFolderOffersGenericToolsAndReadsAnAbsoluteFile() {
    if (!OperatingSystem.IsWindows())
      return;
    using var database = new TemporaryDatabase();
    string file = Path.Combine(Path.GetDirectoryName(database.Path)!, "fixture.txt");
    await File.WriteAllTextAsync(file, "default-folder dispatch");
    var entered = Signal();
    int round = 0;
    var chatClient = new ScriptedChatClient(token => {
      entered.TrySetResult();
      return ++round == 1 ? ToolBatch(token,
          new FunctionCallContent("read-call", "read", new Dictionary<string, object?> { ["path"] = file }))
          : Complete(token);
    });
    await using var server = new RunningServer(new ServerOptions(Path.GetDirectoryName(database.Path)!, 0),
        createClient: () => chatClient);
    await server.WaitForReadyAsync();
    server.Client.DefaultRequestHeaders.Add("X-Urd-Client", "browser");

    using HttpResponseMessage sessionResponse = await server.Client.PostAsJsonAsync("/api/sessions", new { });
    sessionResponse.EnsureSuccessStatusCode();
    using JsonDocument session = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
    Guid sessionId = session.RootElement.GetProperty("sessionId").GetGuid();
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) {
      Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "inspect tools", TestLlmSelection.Default);
    using HttpResponseMessage submitted = await server.Client.PostAsJsonAsync(
        $"/api/sessions/{sessionId}/message-submissions", submission, json);
    submitted.EnsureSuccessStatusCode();
    UserMessageReceipt receipt = (await submitted.Content.ReadFromJsonAsync<UserMessageReceipt>(json))!;

    await entered.Task.WaitAsync(TestTimeout);
    Assert.Equal(new[] { "bash", "cancel_pending_calls", "edit", "list_pending_calls", "read", "write" },
        chatClient.Options[0]!.Tools!.Select(tool => tool.Name).Order(StringComparer.Ordinal));
    SessionSnapshot settled = await HttpTest.SettledAsync(receipt.ExecutionId!.Value, async () =>
        (await server.Client.GetFromJsonAsync<SessionSnapshot>($"/api/sessions/{sessionId}/snapshot", json))!);
    Assert.Equal(ExecutionOutcome.Completed, settled.LastSettlement!.Outcome);
    Assert.Contains("default-folder dispatch", Assert.Single(StoredTools.Read(database)).ResultJson!);
  }

  [Fact]
  public async Task AnotherProcessCannotOpenTheRunningServersDataDirectory() {
    using var database = new TemporaryDatabase("urd exclusive process data");
    await using var owner = new RunningServer(new ServerOptions(Path.GetDirectoryName(database.Path)!, 0));
    await owner.WaitForReadyAsync();
    await using var competing = new ServerProcess(Arguments(database));

    Assert.Null(await competing.Output.ReadLineAsync().WaitAsync(TestTimeout));
    Assert.Equal(1, await competing.WaitForExitAsync());
    Assert.Empty(await competing.Output.ReadToEndAsync().WaitAsync(TestTimeout));
    Assert.Contains("urd.lock", await competing.ReadErrorsAsync(), StringComparison.Ordinal);
    using HttpResponseMessage health = await owner.Client.GetAsync("/health");
    Assert.Equal(HttpStatusCode.NoContent, health.StatusCode);
  }

  private static string[] Arguments(TemporaryDatabase database) =>
      ["--data-root", Path.GetDirectoryName(database.Path)!, "--port", "0"];
}
