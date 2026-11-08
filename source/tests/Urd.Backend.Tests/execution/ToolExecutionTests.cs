using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

public sealed class ToolExecutionTests {
  [Fact]
  public async Task OrdinaryToolErrorDoesNotPreventLaterCallsOrLlmContinuation() {
    int invoked = 0;
    AIFunction count = AIFunctionFactory.Create((int amount) => {
      invoked++;
      if (amount == 1)
        throw new InvalidOperationException("Invalid command.");
      return amount;
    }, name: "count");
    int requests = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? ToolBatch(token,
            new("first", "count", new Dictionary<string, object?> { ["amount"] = 1 }),
            new("second", "count", new Dictionary<string, object?> { ["amount"] = 2 }))
        : Complete(token));
    await using var fixture = new ExecutionFixture(client, tools: [count]);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(2, invoked);
    Assert.Equal(2, client.Calls);
    Assert.All(client.Options, options => Assert.Same(count, Assert.Single(options!.Tools!)));
    FunctionResultContent[] results = client.LastRequest.SelectMany(message => message.Contents)
        .OfType<FunctionResultContent>().ToArray();
    Assert.Equal(2, results.Length);
    Assert.Equal("first", results[0].CallId);
    Assert.False(string.IsNullOrEmpty(Assert.IsType<JsonElement>(results[0].Result).GetProperty("error").GetString()));
    Assert.Equal("second", results[1].CallId);
    Assert.Equal(2, Assert.IsType<JsonElement>(results[1].Result).GetInt32());
    List<StoredTool> tools = StoredTools.Read(fixture.Database);
    Assert.Single(tools, tool => tool.Outcome == ExecutionOutcome.Failed);
    Assert.Equal(2, tools.Count(tool => tool.ResultJson is not null));
  }

  [Fact]
  public async Task ToolEffectsWaitForSuccessfulProviderCleanup() {
    int effects = 0;
    AIFunction effect = AIFunctionFactory.Create(() => Interlocked.Increment(ref effects), name: "effect");
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    int requests = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? new HeldDisposal(ToolCall(token), cleanupEntered, cleanupRelease.Task)
        : Complete(token));
    await using var fixture = new ExecutionFixture(client, tools: [effect]);
    Guid executionId = await fixture.StartExecutionAsync("go");
    Task<SessionExecutionResult> settlement = fixture.Session.WaitForSettlementAsync(executionId);

    try {
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.Equal(0, Volatile.Read(ref effects));
      Assert.False(settlement.IsCompleted);
    } finally {
      cleanupRelease.TrySetResult();
      await settlement.WaitAsync(TestTimeout);
    }

    Assert.Equal(ExecutionOutcome.Completed, (await settlement).Outcome);
    Assert.Equal(1, Volatile.Read(ref effects));
    Assert.Equal(2, client.Calls);
    Assert.Equal("call", Assert.Single(client.LastRequest.SelectMany(message => message.Contents)
        .OfType<FunctionResultContent>()).CallId);

    static async IAsyncEnumerable<ChatResponseUpdate> ToolCall(
        [EnumeratorCancellation] CancellationToken token) {
      await Task.CompletedTask;
      token.ThrowIfCancellationRequested();
      yield return new() {
        Contents = [new FunctionCallContent("call", "effect", null)],
        FinishReason = ChatFinishReason.ToolCalls
      };
    }
  }

  [Fact]
  public async Task PartialToolBatchNeverInvokesTools() {
    int effects = 0;
    AIFunction effect = AIFunctionFactory.Create(() => ++effects, name: "effect");
    var client = new ScriptedChatClient(PartialCall);
    await using var fixture = new ExecutionFixture(client, tools: [effect]);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Failed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(0, effects);
    Assert.Equal(1, client.Calls);
    Assert.Empty(StoredTools.Read(fixture.Database));

    static async IAsyncEnumerable<ChatResponseUpdate> PartialCall(
        [EnumeratorCancellation] CancellationToken token) {
      await Task.CompletedTask;
      token.ThrowIfCancellationRequested();
      yield return new() { Contents = [new FunctionCallContent("call", "effect", null)] };
    }
  }
}
