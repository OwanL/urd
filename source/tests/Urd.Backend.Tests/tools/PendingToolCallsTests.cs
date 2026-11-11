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
      Assert.Equal(0, pending.RequestCancelAll(request, "cancel"));
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

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task JoinOwnsCompletionAndReentrantCancellationCallbacks(bool completeFirst) {
    var pending = new PendingToolCalls();
    Guid execution = Guid.NewGuid();
    var done = Signal();
    var entered = Signal();
    var release = Signal();
    var callbackFinished = Signal();
    var source = new CancellationTokenSource();
    using CancellationTokenRegistration registration = source.Token.Register(() => {
      Assert.Single(pending.List());
      entered.TrySetResult();
      if (!release.Task.Wait(TestTimeout))
        throw new TimeoutException("Cancellation callback was not released.");
      callbackFinished.TrySetResult();
    });
    pending.Add(execution, Guid.NewGuid(), Call("held"), done.Task, source);
    pending.RequestCancelExecution(execution);
    Task join = pending.JoinExecutionAsync(execution);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      Assert.False(join.IsCompleted);
      if (completeFirst)
        done.SetResult();
      else {
        release.SetResult();
        await callbackFinished.Task.WaitAsync(TestTimeout);
      }
      Assert.False(join.IsCompleted);
      Assert.True(source.Token.CanBeCanceled); // Completion cannot dispose a source with reserved callbacks.
    } finally {
      release.TrySetResult();
      done.TrySetResult();
      await join.WaitAsync(TestTimeout);
    }
    Assert.Empty(pending.List());
    Assert.Throws<ObjectDisposedException>(() => source.Token);
  }

  [Fact]
  public async Task EndedCompletionFaultIsRetainedUntilJoinAndOnRepeatedJoins() {
    var pending = new PendingToolCalls();
    Guid execution = Guid.NewGuid();
    var failure = new IOException("result save failed");
    var source = new CancellationTokenSource();
    pending.Add(execution, Guid.NewGuid(), Call("failed"), Task.FromException(failure), source);
    Assert.Empty(pending.List());
    Assert.Throws<ObjectDisposedException>(() => source.Token);
    Task join = pending.JoinExecutionAsync(execution);
    AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => join.WaitAsync(TestTimeout));
    Assert.Contains(failure, error.Flatten().InnerExceptions);
    Assert.Same(join, pending.JoinExecutionAsync(execution));
    await Assert.ThrowsAsync<AggregateException>(() => pending.JoinExecutionAsync(execution).WaitAsync(TestTimeout));
  }

  [Fact]
  public async Task CallbackAndCompletionFaultsBothRemainObservable() {
    var pending = new PendingToolCalls();
    Guid execution = Guid.NewGuid();
    var done = Signal();
    var callbackFailure = new IOException("callback failed");
    var completionFailure = new IOException("cleanup failed");
    var source = new CancellationTokenSource();
    using CancellationTokenRegistration registration = source.Token.Register(() => throw callbackFailure);
    pending.Add(execution, Guid.NewGuid(), Call("failed"), done.Task, source);
    pending.RequestCancelExecution(execution);
    done.SetException(completionFailure);
    AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => pending.JoinExecutionAsync(execution).WaitAsync(TestTimeout));
    Assert.Contains(callbackFailure, error.Flatten().InnerExceptions);
    Assert.Contains(completionFailure, error.Flatten().InnerExceptions);
    Assert.Empty(pending.List());
    AggregateException repeated = await Assert.ThrowsAsync<AggregateException>(() => pending.JoinExecutionAsync(execution).WaitAsync(TestTimeout));
    Assert.Contains(callbackFailure, repeated.Flatten().InnerExceptions);
  }

  [Fact]
  public async Task CallbackFailureNotifiesBeforeCompletionAndJoinOwnsFailingNotification() {
    var pending = new PendingToolCalls();
    Guid execution = Guid.NewGuid();
    var done = Signal();
    var notified = Signal();
    var release = Signal();
    var callbackFailure = new IOException("callback failed");
    var notificationFailure = new IOException("notification failed");
    var source = new CancellationTokenSource();
    int notifications = 0;
    using CancellationTokenRegistration registration = source.Token.Register(() => throw callbackFailure);
    pending.Add(execution, Guid.NewGuid(), Call("held"), done.Task, source, async failure => {
      Assert.Same(callbackFailure, failure);
      Assert.Single(pending.List());
      Interlocked.Increment(ref notifications);
      notified.TrySetResult();
      await release.Task.WaitAsync(TestTimeout);
      throw notificationFailure;
    });
    pending.RequestCancelExecution(execution);
    Task join = pending.JoinExecutionAsync(execution);
    AggregateException error;
    try {
      await notified.Task.WaitAsync(TestTimeout);
      Assert.False(done.Task.IsCompleted);
      done.SetException(callbackFailure); // The same root must not cause another notification.
      Assert.False(join.IsCompleted);
    } finally {
      done.TrySetResult();
      release.TrySetResult();
      error = await Assert.ThrowsAsync<AggregateException>(() => join.WaitAsync(TestTimeout));
    }
    Assert.Equal(1, Volatile.Read(ref notifications));
    Assert.Contains(callbackFailure, error.Flatten().InnerExceptions);
    Assert.Contains(notificationFailure, error.Flatten().InnerExceptions);
    Assert.Empty(pending.List());
  }

  [Fact]
  public async Task AlreadyCompletedSuccessReleasesSourceAndAllowsIdentityReuse() {
    var pending = new PendingToolCalls();
    Guid request = Guid.NewGuid();
    for (int index = 0; index < 2; index++) {
      Guid execution = Guid.NewGuid();
      var source = new CancellationTokenSource();
      pending.Add(execution, request, Call("reused"), Task.CompletedTask, source);
      Assert.Empty(pending.List());
      Assert.Throws<ObjectDisposedException>(() => source.Token);
      await pending.JoinExecutionAsync(execution).WaitAsync(TestTimeout);
      await pending.JoinExecutionAsync(execution).WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task ConcurrentDuplicateIdentityIsRejectedWithoutTakingSourceOwnership() {
    var pending = new PendingToolCalls();
    Guid execution = Guid.NewGuid(), request = Guid.NewGuid();
    var done = Signal();
    var source = new CancellationTokenSource();
    using var rejected = new CancellationTokenSource();
    pending.Add(execution, request, Call("same"), done.Task, source);
    try {
      Assert.Throws<InvalidOperationException>(() => pending.Add(Guid.NewGuid(), request, Call("same"), done.Task, rejected));
      Assert.True(rejected.Token.CanBeCanceled);
      Assert.Single(pending.List());
    } finally {
      done.TrySetResult();
      await pending.JoinExecutionAsync(execution).WaitAsync(TestTimeout);
    }
  }

  private static JsonElement Arguments => JsonSerializer.SerializeToElement(new { });
  private static LlmToolCall Call(string id, string name = "test") => new(id, name, Arguments);
}
