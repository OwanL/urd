using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Context;

public sealed class StartingContextPreparationTests {
  [Fact]
  public async Task PreparationKeepsNestedScopedDocumentsAndUnionsRuntimeProtectedTools() {
    string root = Path.Combine(Path.GetTempPath(), $"urd-context-{Guid.NewGuid():N}");
    string project = Path.Combine(root, "project");
    string packaged = Path.Combine(root, "packaged");
    Directory.CreateDirectory(project);
    try {
      Git(project, "init", "--quiet");
      string rootDocument = Write(project, "AGENTS.md", "Root rule.\n");
      string nestedDocument = Write(project, Path.Combine("nested", "AGENTS.md"), "Nested rule.\nFull nested content.\n");
      Git(project, "add", "AGENTS.md", "nested/AGENTS.md");

      string answer = JsonSerializer.Serialize(new { keptIds = new[] { rootDocument, nestedDocument } });
      var auxiliary = new ScriptedChatClient(token => TextUpdates([answer], token));
      await using RunningParent parent = await StartParentAsync();
      var runner = new ConversationRunner(parent.Fixture.Session.Recorder, _ => auxiliary, [], project);
      var statuses = new ConcurrentQueue<StartingResourcePruningStatus>();
      var pruner = new StartingResourcePruner(runner, (_, _) => Task.FromResult(TestLlmSelection.Default), status => {
        statuses.Enqueue(status);
        return Task.CompletedTask;
      }, "Apply the project guidance.");
      var builder = new StartingContextBuilder(new ToolCatalog([
        TestTool("read"), TestTool("write")
      ]), instructionDiscovery: new InstructionResourceDiscovery(packaged));
      var preferences = new UserPreferences(project, null) {
        ProtectedResources = new() { Tools = ["write"], Skills = [], Instructions = [] }
      };
      Guid boundary = Guid.NewGuid();

      StartingContext context = await builder.PrepareAsync(
          new(SessionKind.Primary, ModelFamily.Other, project, null), boundary, "Apply the project guidance.",
          preferences, new HashSet<string>(["read"], StringComparer.Ordinal), pruner, CancellationToken.None)
          .WaitAsync(TestTimeout);

      string[] instructions = context.Instructions.Select(message => message.Text).ToArray();
      int rootIndex = Array.FindIndex(instructions, text => text.Contains("Root rule.", StringComparison.Ordinal));
      int nestedIndex = Array.FindIndex(instructions, text => text.Contains("Nested rule.", StringComparison.Ordinal));
      Assert.True(rootIndex >= 0 && nestedIndex > rootIndex);
      Assert.Contains(rootDocument, instructions[rootIndex], StringComparison.Ordinal);
      Assert.Contains(nestedDocument, instructions[nestedIndex], StringComparison.Ordinal);
      Assert.Contains(Path.GetDirectoryName(nestedDocument)!, instructions[nestedIndex], StringComparison.Ordinal);
      Assert.Contains("Full nested content.", instructions[nestedIndex], StringComparison.Ordinal);
      Assert.Equal(boundary, context.BoundaryId);
      Assert.Equal(new[] { "read", "write" }, context.Tools.Select(tool => tool.Name));
      Assert.Equal(1, auxiliary.Calls);
      Assert.DoesNotContain("read", auxiliary.LastRequest[^1].Text, StringComparison.Ordinal);
      Assert.DoesNotContain("write", auxiliary.LastRequest[^1].Text, StringComparison.Ordinal);
      Assert.Contains(statuses, status => status.Category == "instructions" && status.State == "completed"
          && status.Total == 2 && status.Retained == 2);
      Assert.Contains(statuses, status => status.Category == "tools" && status.State == "completed"
          && status.Total == 2 && status.Retained == 2);
    } finally {
      if (Directory.Exists(root)) {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
          File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
      }
    }
  }

  private static TestAgentTool TestTool(string name) =>
      new TestAgentTool(name, (_, _, _) => Task.FromResult(new ToolOutput("unused", [])));

  private static string Write(string project, string relativePath, string text) {
    string path = Path.Combine(project, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
    return Path.GetFullPath(path);
  }

  private static void Git(string workingDirectory, params string[] arguments) {
    string executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(directory => Path.Combine(directory.Trim('"'), "git.exe"))
        .First(File.Exists);
    var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false };
    foreach (string argument in arguments)
      start.ArgumentList.Add(argument);
    using Process git = Process.Start(start)!;
    git.WaitForExit();
    Assert.Equal(0, git.ExitCode);
  }

  private static async Task<RunningParent> StartParentAsync() {
    var mainWaiting = Signal();
    var mainRelease = Signal();
    var main = new ScriptedChatClient(token => Paused(mainWaiting, mainRelease, token));
    var fixture = new ExecutionFixture(main);
    Guid executionId = await fixture.StartExecutionAsync("main input");
    await mainWaiting.Task.WaitAsync(TestTimeout);
    return new(fixture, executionId, mainRelease);
  }

  private sealed class RunningParent(ExecutionFixture fixture, Guid executionId, TaskCompletionSource mainRelease)
      : IAsyncDisposable {
    internal ExecutionFixture Fixture => fixture;

    public async ValueTask DisposeAsync() {
      mainRelease.TrySetResult();
      await fixture.Session.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout);
      await fixture.DisposeAsync();
    }
  }
}
