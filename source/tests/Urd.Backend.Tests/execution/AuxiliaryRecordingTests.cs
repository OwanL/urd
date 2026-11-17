using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Tools;
using Urd.Core.Usage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

public sealed class AuxiliaryRecordingTests {
  [Fact]
  public async Task ParallelScopesUseSharedMechanicsWithoutMixingMainHistoryOrCapabilities() {
    var mainWaiting = Signal();
    var mainRelease = Signal();
    var main = new ScriptedChatClient(token => Paused(mainWaiting, mainRelease, token));
    await using var fixture = new ExecutionFixture(main);
    Guid execution = await fixture.StartExecutionAsync("main input");
    await mainWaiting.Task.WaitAsync(TestTimeout);
    var bothStarted = Signal();
    var release = Signal();
    int started = 0, effects = 0;
    var allowed = new TestAgentTool("read", (_, _, _) => {
      Interlocked.Increment(ref effects);
      return Task.FromResult(new ToolOutput("explored", []));
    });
    var excluded = new TestAgentTool("cancel_pending_calls", (_, _, _) =>
        throw new InvalidOperationException("A sibling's tools must not be available."));
    AIFunctionDeclaration definition = AgentToolDeclaration.Create(allowed);
    using var skills = new ScriptedChatClient(Response);
    using var tools = new ScriptedChatClient(Response);
    Task auxiliary = Task.WhenAll(RunScopeAsync("pruning.skills", skills), RunScopeAsync("pruning.tools", tools));
    try {
      await bothStarted.Task.WaitAsync(TestTimeout);
      release.TrySetResult();
      await auxiliary.WaitAsync(TestTimeout);
      Assert.Equal(2, effects);
      Assert.Single(main.Requests);
      Assert.DoesNotContain(main.LastRequest, message => message.Text.Contains("private task", StringComparison.Ordinal));
      Assert.Contains(skills.LastRequest, message => message.Text == "pruning.skills private task");
      Assert.DoesNotContain(skills.LastRequest, message => message.Text.Contains("pruning.tools", StringComparison.Ordinal));
      Assert.Contains(tools.LastRequest, message => message.Text == "pruning.tools private task");
      Assert.Single(Assert.Single(skills.Options)!.Tools!);
      Assert.Equal("pruning.skills", Assert.IsType<UsageContext>(
          Assert.Single(skills.Options)!.AdditionalProperties![UsageContext.PropertyName]).Purpose);
      Assert.Equal(0, skills.DisposeCalls);
      mainRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await fixture.Session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal("visible", StoredLlmResponses.ReadText(fixture.Database));
      Assert.Empty(StoredTools.Read(fixture.Database));
    } finally {
      release.TrySetResult();
      mainRelease.TrySetResult();
      await auxiliary.WaitAsync(TestTimeout);
    }

    async Task RunScopeAsync(string purpose, ScriptedChatClient client) {
      ChatMessage[] input = [new(ChatRole.System, "Select resources."), new(ChatRole.User, purpose + " private task")];
      AuxiliaryConversationRecorder recorder = Assert.IsType<AuxiliaryConversationRecorder>(
          await fixture.Session.Recorder.CreateConversationAsync(Guid.NewGuid(), purpose, TestLlmSelection.DefaultResolution, input, [definition]));
      Guid request = Guid.NewGuid();
      Assert.True(await recorder.StartRequestAsync(request));
      var options = new ChatOptions {
        Tools = [definition], AdditionalProperties = new() {
          [UsageContext.PropertyName] = new UsageContext(fixture.Session.Id, execution, purpose)
        }
      };
      LlmResponseRunner.Result response = await LlmResponseRunner.RunAsync(client, input, options, request,
          async chunk => Assert.True(await recorder.RecordOutputAsync(chunk)),
          async (id, name) => Assert.True(await recorder.RecordToolCallAsync(request, id, name)), CancellationToken.None);
      Assert.Null(response.Failure);
      Assert.True(await recorder.CompleteRequestAsync(new(request, ExecutionOutcome.Completed,
          ToolCalls: response.ToolCalls, Usage: response.Usage, ProviderResponse: response.ProviderResponse)));
      LlmToolCall call = Assert.Single(response.ToolCalls);
      Assert.True(await recorder.TryStartToolAsync(request, call.CallId));
      Assert.False(await recorder.TryStartToolAsync(request, call.CallId));
      LlmToolResult result = await ToolInvoker.InvokeAsync(request, call, [allowed], [definition],
          fixture.Session.WorkingDirectory, false, CancellationToken.None);
      Assert.True(await recorder.CompleteToolAsync(request, result));
      Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
      LlmToolResult refused = await ToolInvoker.InvokeAsync(request, call with { Name = excluded.Name }, [allowed], [definition],
          fixture.Session.WorkingDirectory, false, CancellationToken.None);
      Assert.Equal(ExecutionOutcome.Failed, refused.Outcome);
    }

    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      yield return Text("before ");
      if (Interlocked.Increment(ref started) == 2)
        bothStarted.TrySetResult();
      await release.Task.WaitAsync(token);
      yield return new() { Contents = [new FunctionCallContent("same-id", "read", new Dictionary<string, object?>())] };
      yield return Text("after");
      yield return new() { FinishReason = ChatFinishReason.ToolCalls };
    }
  }

  [Fact]
  public async Task AuxiliaryWriteFailureImmediatelyFencesAdmissionWithoutPlacingMoreMainInput() {
    var waiting = Signal();
    var release = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    var client = new ScriptedChatClient(token => Paused(waiting, release, token, cleanupEntered, cleanupRelease));
    await using var fixture = new ExecutionFixture(client);
    Guid execution = await fixture.StartExecutionAsync("main input");
    await waiting.Task.WaitAsync(TestTimeout);
    AuxiliaryConversationRecorder recorder = Assert.IsType<AuxiliaryConversationRecorder>(
        await fixture.Session.Recorder.CreateConversationAsync(Guid.NewGuid(), "title", TestLlmSelection.DefaultResolution,
          [new(ChatRole.User, "private title task")], []));
    Guid request = Guid.NewGuid();
    Assert.True(await recorder.StartRequestAsync(request));
    fixture.Database.Execute("""
      CREATE TRIGGER reject_auxiliary_output BEFORE INSERT ON auxiliary_output
      BEGIN SELECT RAISE(ABORT, 'Injected auxiliary output failure'); END;
      """);
    try {
      await Assert.ThrowsAsync<SqliteException>(() => recorder.RecordOutputAsync(new(request, Guid.NewGuid(), 0, "title", true)));
      Assert.True((await fixture.Session.ReadSnapshotAsync()).AdmissionFenced);
      Assert.Equal("main input", (await fixture.Session.ReadSnapshotAsync()).FirstUserPreview);
      Assert.Equal(1, client.Calls);
      await fixture.Session.RequestStopAsync(execution).WaitAsync(TestTimeout);
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.False(await recorder.StartRequestAsync(Guid.NewGuid()));
      Assert.True(await recorder.CompleteRequestAsync(new(request, ExecutionOutcome.Cancelled)));
      Assert.False(await recorder.RecordOutputAsync(new(request, Guid.NewGuid(), 0, "late", true)));
    } finally {
      release.TrySetResult();
      cleanupRelease.TrySetResult();
    }
  }
}
