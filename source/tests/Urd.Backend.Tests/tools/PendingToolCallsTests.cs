using System.Text.Json;
using Urd.Core.Execution;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Tools;

public sealed class PendingToolCallsTests {
  [Fact]
  public async Task CancellationAndJoinAreScopedToExecution() {
    var pending = new PendingToolCalls();
    Guid first = Guid.NewGuid(), second = Guid.NewGuid();
    var firstDone = Signal();
    var secondDone = Signal();
    var firstSource = new CancellationTokenSource();
    var secondSource = new CancellationTokenSource();
    pending.Add(first, Guid.NewGuid(), Call("first"), firstDone.Task, firstSource);
    pending.Add(second, Guid.NewGuid(), Call("second"), secondDone.Task, secondSource);
    pending.RequestCancelExecution(first);
    Task join = pending.JoinExecutionAsync(first);
    try {
      Assert.True(firstSource.IsCancellationRequested);
      Assert.False(secondSource.IsCancellationRequested);
      Assert.False(join.IsCompleted);
      firstDone.SetResult();
      await join.WaitAsync(TestTimeout);
      Assert.Equal("second", Assert.Single(pending.List()).CallId);
    } finally {
      firstDone.TrySetResult();
      secondDone.TrySetResult();
      await Task.WhenAll(join, pending.JoinExecutionAsync(second)).WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task BackgroundCancelControlExcludesOnlyItsOwnIdentityAndAcknowledgesWithoutJoining() {
    var pending = new PendingToolCalls();
    Guid execution = Guid.NewGuid(), request = Guid.NewGuid();
    var done = Signal();
    var caller = new CancellationTokenSource();
    var sibling = new CancellationTokenSource();
    var otherRequest = new CancellationTokenSource();
    pending.Add(execution, request, Call("cancel", "cancel_pending_calls"), done.Task, caller);
    pending.Add(execution, request, Call("sibling"), done.Task, sibling);
    pending.Add(execution, Guid.NewGuid(), Call("cancel"), done.Task, otherRequest);
    var context = new ToolContext(".", request, "cancel", false, _ => { });
    var cancel = new CancelPendingCallsTool(pending);
    var list = new ListPendingCallsTool(pending);
    try {
      ToolOutput output = await cancel.RunAsync(Arguments, context, CancellationToken.None).WaitAsync(TestTimeout);
      Assert.Equal(2, output.Details!.Value.GetProperty("cancellationRequested").GetInt32());
      Assert.False(caller.IsCancellationRequested);
      Assert.True(sibling.IsCancellationRequested);
      Assert.True(otherRequest.IsCancellationRequested);
      Assert.Equal(0, pending.RequestCancelOthers(request, "cancel"));
      Assert.False(done.Task.IsCompleted);
      ToolOutput snapshot = await list.RunAsync(Arguments, context, CancellationToken.None);
      Assert.Equal(3, snapshot.Details!.Value.GetArrayLength());
      Assert.Equal(2, pending.List().Count(call => call.CancellationRequested));
      Assert.Equal("list_pending_calls", list.Name);
      Assert.Equal("cancel_pending_calls", cancel.Name);
      Assert.All(new AgentTool[] { list, cancel }, tool => {
        Assert.Equal("object", tool.ParametersSchema.GetProperty("type").GetString());
        Assert.Empty(tool.ParametersSchema.GetProperty("properties").EnumerateObject());
      });
    } finally {
      done.TrySetResult();
      await pending.JoinExecutionAsync(execution).WaitAsync(TestTimeout);
    }
  }

  private static JsonElement Arguments => JsonSerializer.SerializeToElement(new { });
  private static LlmToolCall Call(string id, string name = "test") => new(id, name, Arguments);
}
