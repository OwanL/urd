using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

public sealed class ToolExecutionTests {
  [Theory]
  [InlineData("throws")]
  [InlineData("unknown-name")]
  [InlineData("invalid-arguments")]
  [InlineData("function-marshalling")]
  public async Task OrdinaryToolErrorsAreSavedAndRemainingCallsContinue(string scenario) {
    int invoked = 0;
    var cycle = new Dictionary<string, object?>();
    cycle["self"] = cycle;
    AIFunction count = AIFunctionFactory.Create((int amount) => {
      invoked++;
      // A function's internal failure can follow an effect, including factory-owned marshaling.
      // It is opaque to Core and never proves that nothing happened.
      if (amount == 1 && scenario == "function-marshalling")
        return (object)cycle;
      if (amount == 1)
        throw new InvalidOperationException("Invalid command.");
      return amount;
    }, name: "count");
    string firstName = scenario == "unknown-name" ? "COUNT" : "count";
    object argument = scenario == "invalid-arguments" ? "not an integer" : 1;
    int requests = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? ToolBatch(token, new("first", firstName, new Dictionary<string, object?> { ["amount"] = argument }),
            new("second", "count", new Dictionary<string, object?> { ["amount"] = 2 }))
        : Complete(token));
    await using var fixture = new ExecutionFixture(client, tools: [count]);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(scenario is "throws" or "function-marshalling" ? 2 : 1, invoked);
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
  public async Task CallsAreDeepCopiedAndToolsRunAfterProviderCleanupWithTheExecutionToken() {
    bool cleaned = false;
    var value = new Dictionary<string, object?> { ["text"] = "original" };
    var arguments = new Dictionary<string, object?> { ["value"] = value };
    AIFunction echo = AIFunctionFactory.Create((JsonElement value, CancellationToken token) => {
      Assert.True(cleaned);
      Assert.False(token.IsCancellationRequested);
      return value.GetProperty("text").GetString();
    }, name: "echo");
    int requests = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? Response(token) : Complete(token));
    await using var fixture = new ExecutionFixture(client, tools: [echo]);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    FunctionResultContent result = Assert.Single(client.LastRequest.SelectMany(message => message.Contents)
        .OfType<FunctionResultContent>());
    Assert.Equal("original", Assert.IsType<JsonElement>(result.Result).GetString());
    Assert.Equal(2, client.Calls);

    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      try {
        token.ThrowIfCancellationRequested();
        yield return new() {
          Contents = [new FunctionCallContent("echo-call", "echo", arguments)],
          FinishReason = ChatFinishReason.ToolCalls
        };
        value["text"] = "changed";
        await Task.CompletedTask;
      } finally {
        cleaned = true;
      }
    }
  }

  [Theory]
  [InlineData("result-write")]
  [InlineData("oversized-result")]
  [InlineData("serialization")]
  public async Task UncertainResultAfterAnEffectFencesWithoutStartingAnotherCallOrRequest(string scenario) {
    var database = new TemporaryDatabase();
    var store = UrdStore.Open(database.Path);
    int effects = 0;
    var cycle = new Dictionary<string, object?>();
    cycle["self"] = cycle;
    AIFunction effect = AIFunctionFactory.Create(() => {
      Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
      Assert.True(Assert.Single(StoredTools.Read(database), tool => tool.CallId == "first").Started);
      effects++;
      return scenario switch {
        "oversized-result" => (object)new string('x', 16 * 1024),
        "serialization" => cycle,
        _ => "done"
      };
    }, new AIFunctionFactoryOptions {
      Name = "effect",
      // Keep serialization in the execution layer, outside its invocation-error catch.
      MarshalResult = (value, _, _) => ValueTask.FromResult(value),
      ExcludeResultSchema = true
    });
    var client = new ScriptedChatClient(token => ToolBatch(token,
        new("first", "effect", null), new("second", "effect", null)));
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client, [effect]);
    if (scenario == "result-write") {
      database.Execute("""
        CREATE TRIGGER reject_tool_result BEFORE UPDATE OF result_json ON execution_tools
        WHEN NEW.result_json IS NOT NULL
        BEGIN SELECT RAISE(ABORT, 'Injected tool result write failure'); END;
        """);
    }

    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new UserMessageSubmission(Guid.NewGuid(), 0, "go", TestLlmSelection.Default));
    Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
    try {
      Exception failure = await Assert.ThrowsAnyAsync<Exception>(
          () => session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout));
      Assert.IsNotType<TimeoutException>(failure);
      if (scenario == "result-write")
        Assert.IsType<SqliteException>(failure.GetBaseException());
      Assert.Equal(1, effects);
      Assert.Equal(1, client.Calls);
      Assert.True((await session.ReadSnapshotAsync()).AdmissionFenced);
      Assert.Null(Assert.Single(StoredExecutions.Read(database)).Outcome);
      Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
      Assert.All(StoredTools.Read(database), tool => Assert.Null(tool.ResultJson));
    } finally {
      await session.RequestStopAsync(executionId).WaitAsync(TestTimeout);
      Exception failure = await Assert.ThrowsAnyAsync<Exception>(
          () => session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout));
      Assert.IsNotType<TimeoutException>(failure);
      // A timeout must not dispose borrowed resources beneath uncertain live work.
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Theory]
  [InlineData("eof")]
  [InlineData("stop-with-call")]
  [InlineData("empty-tool-finish")]
  [InlineData("length")]
  [InlineData("duplicate")]
  [InlineData("informational")]
  [InlineData("mapping-error")]
  [InlineData("call-after-finish")]
  [InlineData("too-many")]
  [InlineData("malformed-arguments")]
  [InlineData("oversized-arguments")]
  [InlineData("exception-after-finish")]
  public async Task InvalidOrIncompleteBatchesNeverAuthorizeEffects(string scenario) {
    int effects = 0;
    AIFunction effect = AIFunctionFactory.Create(() => ++effects, name: "effect");
    var call = new FunctionCallContent("call", "effect", null);
    var toolFinish = new ChatResponseUpdate { FinishReason = ChatFinishReason.ToolCalls };
    var stopFinish = new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
    ChatResponseUpdate[] script = scenario switch {
      "eof" => [new() { Contents = [call] }],
      "stop-with-call" => [new() { Contents = [call], FinishReason = ChatFinishReason.Stop }],
      "empty-tool-finish" => [toolFinish],
      "length" => [new() { Contents = [call], FinishReason = ChatFinishReason.Length }],
      "duplicate" => [new() { Contents = [call, call] }, toolFinish],
      "informational" => [new() {
        Contents = [new FunctionCallContent("call", "effect", null) { InformationalOnly = true }]
      }, toolFinish],
      "mapping-error" => [new() {
        Contents = [new FunctionCallContent("call", "effect", null) { Exception = new JsonException("Malformed call.") }]
      }, toolFinish],
      "call-after-finish" => [stopFinish, new() { Contents = [call] }],
      "too-many" => [new() {
        Contents = [.. Enumerable.Range(0, 17).Select(index => new FunctionCallContent($"call-{index}", "effect", null))]
      }, toolFinish],
      "malformed-arguments" => [new() {
        Contents = [new FunctionCallContent("call", "effect", new Dictionary<string, object?> {
          ["bad"] = default(JsonElement)
        })]
      }, toolFinish],
      "oversized-arguments" => [new() {
        Contents = [new FunctionCallContent("call", "effect", new Dictionary<string, object?> {
          ["large"] = new string('x', 16 * 1024)
        })]
      }, toolFinish],
      "exception-after-finish" => [new() { Contents = [call], FinishReason = ChatFinishReason.ToolCalls }],
      _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };
    var client = new ScriptedChatClient(Response);
    await using var fixture = new ExecutionFixture(client, tools: [effect]);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Failed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(0, effects);
    Assert.Equal(1, client.Calls);
    Assert.Empty(StoredTools.Read(fixture.Database));

    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      await Task.CompletedTask;
      token.ThrowIfCancellationRequested();
      foreach (ChatResponseUpdate update in script)
        yield return update;
      if (scenario == "exception-after-finish")
        throw new InvalidOperationException("Failure after tool finish.");
    }
  }

}
