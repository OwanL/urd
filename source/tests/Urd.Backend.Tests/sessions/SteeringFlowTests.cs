using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

// End-to-end steering across tool and text boundaries, with real persistence and owned cleanup.
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
      var first = new UserMessageSubmission(Guid.NewGuid(), 0, "focus on performance");
      var second = new UserMessageSubmission(Guid.NewGuid(), 0, "keep Unicode: 🙂\0");
      UserMessageReceipt[] retries = await Task.WhenAll(fixture.Session.SubmitUserMessageAsync(first),
          fixture.Session.SubmitUserMessageAsync(first));
      UserMessageReceipt secondReceipt = await fixture.Session.SubmitUserMessageAsync(second);
      Assert.Equal(retries[0], retries[1]);
      Assert.All(new[] { retries[0], secondReceipt }, receipt => {
        Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
        Assert.Equal(executionId, receipt.ExecutionId);
      });
      Assert.Equal(1, client.Calls);
      SessionSnapshot pending = await fixture.Session.ReadSnapshotAsync();
      Assert.Equal(new[] { "start", first.Text, second.Text },
          pending.Messages.Where(message => message.Message.Role == MessageRole.User).Select(message => message.Preview));
      MessageRef secondRef = Assert.Single(pending.Messages,
          message => message.Message.MessageId == second.SubmissionId).Message;
      Assert.Equal(second.Text, (await fixture.Session.ReadMessageContentAsync(new(secondRef, 0, 0))).Text);

      toolRelease.TrySetResult();
      await nextEntered.Task.WaitAsync(TestTimeout);
      IReadOnlyList<ChatMessage> continued = client.Requests[1];
      Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.User },
          continued.Select(message => message.Role));
      FunctionResultContent[] results = continued[2].Contents.OfType<FunctionResultContent>().ToArray();
      Assert.Equal(new[] { "first", "second" }, results.Select(result => result.CallId));
      Assert.Equal(new[] { 1, 2 }, results.Select(result => Assert.IsType<JsonElement>(result.Result).GetInt32()));
      Assert.Equal(new[] { "start", first.Text, second.Text }, Users(continued));

      IEnumerable<ChatMessage> inFlight = client.LastRequestSource;
      string[] claimedUsers = Users(inFlight);
      var late = new UserMessageSubmission(Guid.NewGuid(), 0, "also explain the trade-off");
      UserMessageReceipt lateReceipt = await fixture.Session.SubmitUserMessageAsync(late);
      Assert.Equal(executionId, lateReceipt.ExecutionId);
      Assert.Equal(claimedUsers, Users(inFlight)); // Inspect the actual passed collection, not the fake's copy.
      nextRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(3, client.Calls); // Pending input continues even when the preceding response has no tools.
      Assert.Equal(claimedUsers, Users(inFlight)); // A later claim cannot append to the previous request either.
      Assert.Equal(new[] { 1, 2 }, effects.ToArray());
      Assert.Equal(new[] { "start", first.Text, second.Text, late.Text }, Users(client.LastRequest));
      Assert.Equal(new[] { "visible" }, client.LastRequest.Where(message => message.Role == ChatRole.Assistant)
          .Select(message => message.Text).Where(text => !string.IsNullOrEmpty(text)));
      Assert.Equal(3, StoredExecutions.ReadRequests(fixture.Database).Count);
      Assert.All(StoredTools.Read(fixture.Database), tool => Assert.Equal(ExecutionOutcome.Completed, tool.Outcome));

      using UrdStore reopened = UrdStore.Open(fixture.Database.Path);
      var manager = new SessionManager(reopened, client, [step]);
      PrimarySession loaded = await manager.GetPrimaryAsync(fixture.Session.Id);
      Assert.Equal(retries[0], await loaded.SubmitUserMessageAsync(first));
      Assert.Equal(secondReceipt, await loaded.SubmitUserMessageAsync(second));
      Assert.Equal(lateReceipt, await loaded.SubmitUserMessageAsync(late));
      Assert.Equal(3, client.Calls);
      Assert.Equal(new[] { 1, 2 }, effects.ToArray());
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    } finally {
      toolRelease.TrySetResult();
      nextRelease.TrySetResult();
    }
  }

  [Fact]
  public async Task ShutdownJoinsToolCleanupAndCallbacksWithoutDeliveringPendingSteeringOrStartingAnotherCall() {
    var entered = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    var callbackEntered = Signal();
    var callbackRelease = Signal();
    PrimarySession session = null!;
    int effects = 0;
    async Task<string> Step(int number, CancellationToken token) {
      Interlocked.Increment(ref effects);
      using CancellationTokenRegistration registration = token.Register(() => {
        Assert.True(session.ReadSnapshotAsync().WaitAsync(TestTimeout).GetAwaiter().GetResult().AdmissionFenced);
        callbackEntered.TrySetResult();
        callbackRelease.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
      });
      try {
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
      } finally {
        cleanupEntered.TrySetResult();
        await cleanupRelease.Task;
      }
    }

    int round = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref round) == 1
        ? ToolBatch(token, Call("first", 1), Call("second", 2)) : Complete(token));
    var database = new TemporaryDatabase();
    var store = UrdStore.Open(database.Path);
    AIFunction step = AIFunctionFactory.Create(Step, name: "step");
    AIFunction[] configured = [step];
    var manager = new SessionManager(store, client, configured);
    configured[0] = AIFunctionFactory.Create(() => "replacement", name: "replacement");
    try {
      session = await manager.CreatePrimaryAsync();
      UserMessageReceipt origin = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "start"));
      Guid executionId = Assert.IsType<Guid>(origin.ExecutionId);
      await entered.Task.WaitAsync(TestTimeout);
      var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "pending steering");
      UserMessageReceipt pending = await session.SubmitUserMessageAsync(submission);
      Assert.Equal(executionId, pending.ExecutionId);
      Assert.Same(step, Assert.Single(client.Options[0]!.Tools!)); // The manager snapshots the caller's collection.

      Task shutdown = manager.ShutdownSessionsAsync();
      await Task.WhenAll(cleanupEntered.Task, callbackEntered.Task).WaitAsync(TestTimeout);
      Assert.False(shutdown.IsCompleted);
      cleanupRelease.TrySetResult();
      Assert.False(shutdown.IsCompleted);
      callbackRelease.TrySetResult();
      await shutdown.WaitAsync(TestTimeout);
      Assert.Equal(ExecutionOutcome.Cancelled, (await session.WaitForSettlementAsync(executionId)).Outcome);
      Assert.Equal(1, effects);
      Assert.Equal(1, client.Calls);
      Assert.Equal(pending, await session.GetUserMessageReceiptAsync(submission.SubmissionId));
      Assert.Equal(pending, await session.SubmitUserMessageAsync(submission));
      StoredTool[] tools = StoredTools.Read(database).ToArray();
      Assert.Equal(2, tools.Length);
      Assert.True(tools[0].Started);
      Assert.False(tools[1].Started);
      Assert.All(tools, tool => {
        Assert.Equal(ExecutionOutcome.Cancelled, tool.Outcome);
        Assert.NotNull(tool.ResultJson);
      });
      Assert.Equal(ExecutionOutcome.Cancelled, Assert.Single((await session.ReadSnapshotAsync()).Messages,
          message => message.Message.MessageId == submission.SubmissionId).Settlement);

      using UrdStore reopened = UrdStore.Open(database.Path);
      var nextManager = new SessionManager(reopened, client);
      try {
        PrimarySession loaded = await nextManager.GetPrimaryAsync(session.Id);
        Assert.Equal(pending, await loaded.SubmitUserMessageAsync(submission));
        Assert.Equal(1, client.Calls);
        UserMessageReceipt fresh = await loaded.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "fresh"));
        await loaded.WaitForSettlementAsync(fresh.ExecutionId!.Value).WaitAsync(TestTimeout);
        Assert.Equal(new[] { "start", "fresh" }, Users(client.LastRequest));
        Assert.Equal(1, effects);
      } finally {
        await nextManager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      }
    } finally {
      cleanupRelease.TrySetResult();
      callbackRelease.TrySetResult();
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Fact]
  public async Task SubmissionRacingTheLastResponseBoundaryIsNeverStranded() {
    for (int attempt = 0; attempt < 12; attempt++) {
      var finishing = Signal();
      var release = Signal();
      int requests = 0;
      async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
        token.ThrowIfCancellationRequested();
        yield return new() { FinishReason = ChatFinishReason.Stop };
        if (Interlocked.Increment(ref requests) == 1) {
          finishing.TrySetResult();
          await release.Task;
        }
      }

      var client = new ScriptedChatClient(Response);
      await using var fixture = new ExecutionFixture(client);
      Guid original = await fixture.StartExecutionAsync("start");
      await finishing.Task.WaitAsync(TestTimeout);
      Task<Guid> late = Task.Run(() => fixture.StartExecutionAsync("steer"));
      try {
        release.TrySetResult();
        Guid accepted = await late.WaitAsync(TestTimeout);
        await Task.WhenAll(fixture.Session.WaitForSettlementAsync(original),
            fixture.Session.WaitForSettlementAsync(accepted)).WaitAsync(TestTimeout);
        Assert.Equal(2, client.Calls);
        Assert.Equal(new[] { "start", "steer" }, Users(client.LastRequest));
        Assert.All(StoredExecutions.Read(fixture.Database), execution => Assert.Equal(ExecutionOutcome.Completed, execution.Outcome));
      } finally {
        release.TrySetResult();
        await late.WaitAsync(TestTimeout); // The fixture must know every run before it drains them.
      }
    }
  }

  private static string[] Users(IEnumerable<ChatMessage> messages) =>
      messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text).ToArray();

  private static FunctionCallContent Call(string id, int number) =>
      new(id, "step", new Dictionary<string, object?> { ["number"] = number });
}
