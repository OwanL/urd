using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class SessionAttentionTests {
  [Fact]
  public async Task SuccessfulSettlementNotifiesAndObserverFailuresAreIsolated() {
    var release = Signal();
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      await release.Task.WaitAsync(token);
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    using var database = new TemporaryDatabase();
    using var client = new ScriptedChatClient(Response);
    using var store = UrdStore.Open(database.Path);
    await using var manager = new SessionManager(store, _ => client);
    var attention = new TaskCompletionSource<AttentionKind>(TaskCreationOptions.RunContinuationsAsynchronously);
    manager.Attention += _ => throw new InvalidOperationException("Observer failure");
    manager.Attention += kind => attention.TrySetResult(kind);
    PrimarySession session = await manager.CreatePrimaryAsync();
    try {
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), 0, "hello", TestLlmSelection.Default));
      Assert.False(attention.Task.IsCompleted);
      release.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed, (await session.WaitForExecutionFinishAsync(receipt.ExecutionId!.Value)
          .WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(AttentionKind.Completion, await attention.Task.WaitAsync(TestTimeout));
    } finally {
      release.TrySetResult();
    }
  }

  [Fact]
  public async Task SteeringDoesNotNotifyUntilTheFollowUpCompletes() {
    var firstEntered = Signal();
    var firstRelease = Signal();
    var secondEntered = Signal();
    var secondRelease = Signal();
    int calls = 0;
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      bool first = Interlocked.Increment(ref calls) == 1;
      (first ? firstEntered : secondEntered).TrySetResult();
      await (first ? firstRelease : secondRelease).Task.WaitAsync(token);
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }

    using var database = new TemporaryDatabase();
    using var client = new ScriptedChatClient(Response);
    using var store = UrdStore.Open(database.Path);
    await using var manager = new SessionManager(store, _ => client);
    var attention = new TaskCompletionSource<AttentionKind>(TaskCreationOptions.RunContinuationsAsynchronously);
    manager.Attention += kind => attention.TrySetResult(kind);
    PrimarySession session = await manager.CreatePrimaryAsync();
    try {
      UserMessageReceipt first = await session.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), 0, "first", TestLlmSelection.Default));
      await firstEntered.Task.WaitAsync(TestTimeout);
      await session.SubmitUserMessageAsync(new UserMessageSubmission(Guid.NewGuid(), 0, "follow up", TestLlmSelection.Default));
      firstRelease.TrySetResult();
      await secondEntered.Task.WaitAsync(TestTimeout);
      Assert.False(attention.Task.IsCompleted);
      secondRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed, (await session.WaitForExecutionFinishAsync(first.ExecutionId!.Value)
          .WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(AttentionKind.Completion, await attention.Task.WaitAsync(TestTimeout));
    } finally {
      firstRelease.TrySetResult();
      secondRelease.TrySetResult();
    }
  }

  [Fact]
  public async Task StoppedExecutionDoesNotNotifyCompletion() {
    var entered = Signal();
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      entered.TrySetResult();
      await Task.Delay(Timeout.Infinite, token);
      yield break;
    }

    using var database = new TemporaryDatabase();
    using var client = new ScriptedChatClient(Response);
    using var store = UrdStore.Open(database.Path);
    await using var manager = new SessionManager(store, _ => client);
    var attention = new List<AttentionKind>();
    manager.Attention += attention.Add;
    PrimarySession session = await manager.CreatePrimaryAsync();
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
        new UserMessageSubmission(Guid.NewGuid(), 0, "hello", TestLlmSelection.Default));
    await entered.Task.WaitAsync(TestTimeout);
    await session.RequestStopAsync(receipt.ExecutionId!.Value);
    Assert.Equal(ExecutionOutcome.Cancelled, (await session.WaitForExecutionFinishAsync(receipt.ExecutionId.Value)
        .WaitAsync(TestTimeout)).Outcome);
    Assert.Empty(attention);
  }
}
