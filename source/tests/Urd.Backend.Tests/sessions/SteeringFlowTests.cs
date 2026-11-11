using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class SteeringFlowTests {
  [Fact]
  public async Task ToolResultsPrecedeOrderedSteeringAndLaterSubmissionWaitsForTheNextRequest() {
    var toolEntered = Signal();
    var toolRelease = Signal();
    var nextEntered = Signal();
    var nextRelease = Signal();
    var effects = new ConcurrentQueue<int>();
    async Task<int> Step(int number, CancellationToken token) {
      effects.Enqueue(number);
      if (number == 1) {
        toolEntered.TrySetResult();
        await toolRelease.Task.WaitAsync(token);
      }
      return number;
    }

    AIFunction step = AIFunctionFactory.Create(Step, name: "step");
    int round = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref round) switch {
      1 => ToolBatch(token, Call("first", 1), Call("second", 2)),
      2 => Paused(nextEntered, nextRelease, token),
      _ => Complete(token)
    });
    await using var fixture = new ExecutionFixture(client, [step]);
    try {
      Guid executionId = await fixture.StartExecutionAsync("start");
      await toolEntered.Task.WaitAsync(TestTimeout);
      var first = new UserMessageSubmission(Guid.NewGuid(), 0, "focus on performance", TestLlmSelection.Default);
      var second = new UserMessageSubmission(Guid.NewGuid(), 0, "keep Unicode: 🙂\0", TestLlmSelection.Default);
      UserMessageReceipt[] receipts = await Task.WhenAll(
          fixture.Session.SubmitUserMessageAsync(first), fixture.Session.SubmitUserMessageAsync(second));
      Assert.All(receipts, receipt => {
        Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
        Assert.Equal(executionId, receipt.ExecutionId);
      });
      Assert.Equal(1, client.Calls);

      toolRelease.TrySetResult();
      await nextEntered.Task.WaitAsync(TestTimeout);
      ChatMessage[] continued = client.Requests[1]
          .SkipWhile(message => message.Role == ChatRole.System || message.Role == new ChatRole("developer")).ToArray();
      Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.User },
          continued.Select(message => message.Role));
      FunctionResultContent[] results = continued[2].Contents.OfType<FunctionResultContent>().ToArray();
      Assert.Equal(new[] { "first", "second" }, results.Select(result => result.CallId));
      Assert.Equal(new[] { "1", "2" }, results.Select(result => Assert.IsType<string>(result.Result)));
      Assert.Equal(new[] { "start", first.Text, second.Text }, Users(continued));

      IEnumerable<ChatMessage> inFlight = client.LastRequestSource;
      string[] claimedUsers = Users(inFlight);
      var late = new UserMessageSubmission(Guid.NewGuid(), 0, "also explain the trade-off", TestLlmSelection.Default);
      UserMessageReceipt lateReceipt = await fixture.Session.SubmitUserMessageAsync(late);
      Assert.Equal(executionId, lateReceipt.ExecutionId);
      Assert.Equal(claimedUsers, Users(inFlight));
      nextRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(3, client.Calls);
      Assert.Equal(claimedUsers, Users(inFlight));
      Assert.Equal(new[] { 1, 2 }, effects.ToArray());
      Assert.Equal(new[] { "start", first.Text, second.Text, late.Text }, Users(client.LastRequest));
      Assert.Equal(new[] { "visible" }, client.LastRequest.Where(message => message.Role == ChatRole.Assistant)
          .Select(message => message.Text).Where(text => !string.IsNullOrEmpty(text)));
      Assert.Equal(3, StoredExecutions.ReadRequests(fixture.Database).Count);
      Assert.All(StoredTools.Read(fixture.Database), tool => Assert.Equal(ExecutionOutcome.Completed, tool.Outcome));
    } finally {
      toolRelease.TrySetResult();
      nextRelease.TrySetResult();
    }
  }

  private static string[] Users(IEnumerable<ChatMessage> messages) =>
      messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text).ToArray();

  private static FunctionCallContent Call(string id, int number) =>
      new(id, "step", new Dictionary<string, object?> { ["number"] = number });
}
