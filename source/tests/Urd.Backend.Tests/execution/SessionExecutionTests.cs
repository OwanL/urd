using System.Runtime.CompilerServices;
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

// Session execution lifecycle: terminal evidence, stop and cleanup ordering, and failure fencing.
public sealed class SessionExecutionTests {
  private const string CancellationCallbackFailureMessage = "Scripted cancellation callback failure.";
  private const string EnumeratorDisposalFailureMessage = "Scripted enumerator disposal failure.";

  [Theory]
  [InlineData("eof")]
  [InlineData("length")]
  [InlineData("exception")]
  [InlineData("exception-after-stop")]
  [InlineData("non-text")]
  [InlineData("text-after-stop")]
  [InlineData("length-after-stop")]
  public async Task RequiresPositiveTerminalEvidenceAndAnOtherwiseSuccessfulRequest(string failureScenario) {
    var client = new ScriptedChatClient(token => FailedResponse(failureScenario, token));
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Failed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(ExecutionOutcome.Failed, Assert.Single(StoredExecutions.ReadRequests(fixture.Database)).Outcome);
    Assert.All(StoredLlmResponses.Read(fixture.Database), row => Assert.False(row.Final));
    Assert.Equal(1, client.Calls);
    Assert.Equal(0, client.DisposeCalls);
  }

  [Fact]
  public async Task StopMarkerAllowsTrailingUsageAndEmptyTextBeforeSettlement() {
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      await Task.CompletedTask;
      token.ThrowIfCancellationRequested();
      yield return new() {
        Contents = [new TextContent("answer")],
        FinishReason = ChatFinishReason.Stop
      };
      yield return new() { Contents = [new UsageContent(), new TextContent("")] };
    }

    var client = new ScriptedChatClient(Response);
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("go");

    SessionExecutionResult result = await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout);
    Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
    Assert.Equal("answer", StoredLlmResponses.ReadText(fixture.Database));
    Assert.Single(StoredLlmResponses.Read(fixture.Database), chunk => chunk.Final);
    Assert.Equal(1, client.Calls);
    Assert.Equal(0, client.DisposeCalls);
  }

  [Fact]
  public async Task StopWaitsForActualCleanupBeforeSettlementOrReplacementAndBorrowsClient() {
    var waiting = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    int request = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref request) == 1
        ? HeldCleanupResponse(waiting, cleanupEntered, cleanupRelease, token)
        : Complete(token));
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("first");
    try {
      await waiting.Task.WaitAsync(TestTimeout);
      Assert.Equal(new StopReceipt(executionId, StopStatus.Requested),
          await fixture.Session.RequestStopAsync(executionId).WaitAsync(TestTimeout));
      Assert.Equal(new StopReceipt(executionId, StopStatus.Requested),
          await fixture.Session.RequestStopAsync(executionId).WaitAsync(TestTimeout));
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.False(fixture.Session.WaitForSettlementAsync(executionId).IsCompleted);
      UserMessageReceipt refused = await fixture.Session.SubmitUserMessageAsync(new UserMessageSubmission(Guid.NewGuid(), 0, "too early", TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
      Assert.Equal(MessageRefusalReason.Stopping, refused.RefusalReason);
      Assert.Null(refused.ExecutionId);
      Assert.Equal(1, client.Calls);
    } finally {
      cleanupRelease.TrySetResult();
    }

    Assert.Equal(ExecutionOutcome.Cancelled,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Guid replacement = await fixture.StartExecutionAsync("second");
    Assert.NotEqual(executionId, replacement);
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(replacement).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(2, client.Calls);
    Assert.Equal(0, client.DisposeCalls);
  }

  [Fact]
  public async Task StopAcknowledgesBeforeReentrantCallbackJoinAndStaleStopCannotCancelReplacement() {
    var sourceEntered = Signal();
    var sourceRelease = Signal();
    var sourceExited = Signal();
    var callbackRelease = Signal();
    var replacementEntered = Signal();
    var replacementRelease = Signal();
    var callbackSnapshot = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    PrimarySession session = null!;
    int request = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref request) == 1
        ? ReentrantBlockedCancellationResponse(session, sourceEntered, sourceRelease, sourceExited,
            callbackSnapshot, callbackRelease, token)
        : Paused(replacementEntered, replacementRelease, token));
    await using var fixture = new ExecutionFixture(client);
    session = fixture.Session;
    Guid executionId = await fixture.StartExecutionAsync("first");
    Task<SessionExecutionResult> settlement = session.WaitForSettlementAsync(executionId);
    try {
      await sourceEntered.Task.WaitAsync(TestTimeout);
      Task<StopReceipt> firstStop = session.RequestStopAsync(executionId);
      Assert.Equal(new StopReceipt(executionId, StopStatus.Requested), await firstStop.WaitAsync(TestTimeout));
      SessionSnapshot callbackRead = await callbackSnapshot.Task.WaitAsync(TestTimeout);
      Assert.True(callbackRead.StopRequested);
      sourceRelease.TrySetResult();
      await sourceExited.Task.WaitAsync(TestTimeout);
      // Reach saved request completion, not merely the iterator's finally block,
      // while the execution still owns the blocked cancellation callback.
      using (var deadline = new CancellationTokenSource(TestTimeout)) {
        while (Assert.Single(StoredExecutions.ReadRequests(fixture.Database)).Outcome is null)
          await Task.Delay(10, deadline.Token);
      }

      Task<StopReceipt> secondStop = session.RequestStopAsync(executionId);
      Assert.Equal(new StopReceipt(executionId, StopStatus.Requested), await secondStop.WaitAsync(TestTimeout));
      Assert.False(settlement.IsCompleted);
      UserMessageReceipt refused = await session.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), 0, "too early", TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
      Assert.Equal(MessageRefusalReason.Stopping, refused.RefusalReason);
      Assert.Equal(1, client.Calls);
    } finally {
      sourceRelease.TrySetResult();
      callbackRelease.TrySetResult();
    }

    Assert.Equal(ExecutionOutcome.Cancelled, (await settlement.WaitAsync(TestTimeout)).Outcome);

    Guid replacement = await fixture.StartExecutionAsync("replacement");
    Task<SessionExecutionResult> replacementSettlement = session.WaitForSettlementAsync(replacement);
    try {
      await replacementEntered.Task.WaitAsync(TestTimeout);
      Assert.Equal(new StopReceipt(executionId, StopStatus.Settled, ExecutionOutcome.Cancelled),
          await session.RequestStopAsync(executionId).WaitAsync(TestTimeout));
      Assert.False(replacementSettlement.IsCompleted);
    } finally {
      replacementRelease.TrySetResult();
    }

    Assert.Equal(ExecutionOutcome.Completed, (await replacementSettlement.WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(2, client.Calls);
  }

  [Fact]
  public async Task CancellationCallbackFailureFencesUnsettledExecutionAfterProviderCleanup() {
    var requestEntered = Signal();
    var providerExited = Signal();
    var database = new TemporaryDatabase();
    var store = UrdStore.Open(database.Path);
    var client = new ScriptedChatClient(token => CancellationCallbackFailureResponse(
        requestEntered, providerExited, token));
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);
    Guid? executionId = null;
    try {
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new UserMessageSubmission(Guid.NewGuid(), 0, "go", TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      executionId = Assert.IsType<Guid>(receipt.ExecutionId);
      Guid acceptedExecution = executionId.Value;
      await requestEntered.Task.WaitAsync(TestTimeout);

      Assert.Equal(new StopReceipt(acceptedExecution, StopStatus.Requested),
          await session.RequestStopAsync(acceptedExecution).WaitAsync(TestTimeout));
      await providerExited.Task.WaitAsync(TestTimeout);
      await AssertCancellationCallbackFailureAsync(session.WaitForSettlementAsync(acceptedExecution));

      SessionSnapshot snapshot = await session.ReadSnapshotAsync();
      Assert.True(snapshot.AdmissionFenced);
      Assert.Equal(acceptedExecution, snapshot.UnsettledExecutionId);
      Assert.Null(snapshot.LastSettlement);
      Assert.Null(Assert.Single(StoredExecutions.Read(database)).Outcome);
      UserMessageReceipt refused = await session.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), 0, "replacement", TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
      Assert.Equal(MessageRefusalReason.Stopping, refused.RefusalReason);
      Assert.Single(StoredExecutions.Read(database));
    } finally {
      if (executionId is { } acceptedExecution) {
        if (requestEntered.Task.IsCompletedSuccessfully) {
          Assert.Equal(new StopReceipt(acceptedExecution, StopStatus.Requested),
              await session.RequestStopAsync(acceptedExecution).WaitAsync(TestTimeout));
          await providerExited.Task.WaitAsync(TestTimeout);
          await AssertCancellationCallbackFailureAsync(session.WaitForSettlementAsync(acceptedExecution));
        } else {
          await session.RequestStopAsync(acceptedExecution).WaitAsync(TestTimeout);
          await session.WaitForSettlementAsync(acceptedExecution).WaitAsync(TestTimeout);
        }
      }

      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Fact]
  public async Task OutputWriteAndCleanupFailuresAreRetainedWhileExecutionRemainsFenced() {
    var database = new TemporaryDatabase();
    var store = UrdStore.Open(database.Path);
    var disposed = Signal();
    var client = new ScriptedChatClient(token => WriteAndCleanupFailureResponse(disposed, token));
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);
    Guid? executionId = null;
    try {
      database.Execute("""
        CREATE TRIGGER reject_output BEFORE INSERT ON execution_output
        BEGIN SELECT RAISE(ABORT, 'Injected output write failure'); END;
        """);
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new UserMessageSubmission(Guid.NewGuid(), 0, "go", TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      executionId = receipt.ExecutionId;
      Guid acceptedExecution = Assert.IsType<Guid>(executionId);

      Exception failure = await Assert.ThrowsAnyAsync<Exception>(
          () => session.WaitForSettlementAsync(acceptedExecution).WaitAsync(TestTimeout));
      Exception[] failures = FlattenFailures(failure).ToArray();
      Assert.Contains(failures, error => error.GetBaseException() is SqliteException sqlite &&
          sqlite.SqliteErrorCode == 19);
      Assert.Contains(failures, error => error.Message == CancellationCallbackFailureMessage);
      Assert.Contains(failures, error => error.Message == EnumeratorDisposalFailureMessage);
      await disposed.Task.WaitAsync(TestTimeout);

      SessionSnapshot snapshot = await session.ReadSnapshotAsync();
      Assert.True(snapshot.AdmissionFenced);
      Assert.Equal(acceptedExecution, snapshot.UnsettledExecutionId);
      Assert.Null(snapshot.LastSettlement);
      Assert.Null(Assert.Single(StoredExecutions.Read(database)).Outcome);
      Assert.Null(Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
      UserMessageReceipt refused = await session.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), 0, "replacement", TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
      Assert.Equal(MessageRefusalReason.Stopping, refused.RefusalReason);
      Assert.Null(refused.ExecutionId);
      Assert.Equal(1, client.Calls);
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      if (executionId is { } acceptedExecution) {
        await session.RequestStopAsync(acceptedExecution).WaitAsync(TestTimeout);
        await disposed.Task.WaitAsync(TestTimeout);
        try {
          await session.WaitForSettlementAsync(acceptedExecution).WaitAsync(TestTimeout);
        } catch (Exception failure) when (failure is not TimeoutException &&
              FlattenFailures(failure).All(error => error is SqliteException { SqliteErrorCode: 19 } ||
                  error.Message == CancellationCallbackFailureMessage || error.Message == EnumeratorDisposalFailureMessage)) {
          // Known injected teardown failures must not hide an earlier assertion failure.
        }
      }

      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Fact]
  public async Task OutputWriteFailureCleansUpButFencesRatherThanSettlesOrReplacesTheExecution() {
    var database = new TemporaryDatabase();
    var store = UrdStore.Open(database.Path);
    var cleaned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new ScriptedChatClient(token => WriteFailureResponse(cleaned, token));
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);
    Guid? executionId = null;
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "go", TestLlmSelection.Default);
    try {
      database.Execute("""
        CREATE TRIGGER reject_output BEFORE INSERT ON execution_output
        BEGIN SELECT RAISE(ABORT, 'Injected output write failure'); END;
        """);
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(submission);
      executionId = receipt.ExecutionId;
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      Guid acceptedExecution = Assert.IsType<Guid>(executionId);

      Exception failure = await Assert.ThrowsAnyAsync<Exception>(
          () => session.WaitForSettlementAsync(acceptedExecution).WaitAsync(TestTimeout));
      Assert.Equal(19, Assert.IsType<SqliteException>(failure.GetBaseException()).SqliteErrorCode);
      Assert.True(await cleaned.Task.WaitAsync(TestTimeout));
      SessionSnapshot snapshot = await session.ReadSnapshotAsync();
      Assert.True(snapshot.AdmissionFenced);
      Assert.Equal(acceptedExecution, snapshot.UnsettledExecutionId);
      Assert.Null(snapshot.LastSettlement);
      Assert.Single(snapshot.Messages, row => row.Message.Role == MessageRole.User);
      Assert.DoesNotContain(snapshot.Messages, row => row.Message.Role == MessageRole.Assistant);
      Assert.Null(Assert.Single(StoredExecutions.Read(database)).Outcome);
      Assert.Null(Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
      Assert.Empty(StoredLlmResponses.Read(database));
      UserMessageReceipt refused = await session.SubmitUserMessageAsync(new UserMessageSubmission(Guid.NewGuid(), 0, "replacement", TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
      Assert.Equal(MessageRefusalReason.Stopping, refused.RefusalReason);
      Assert.Null(refused.ExecutionId);
      Assert.Single(StoredExecutions.Read(database));
      Assert.Equal(1, client.Calls);
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      if (executionId is { } acceptedExecution) {
        // Stop is only a cleanup fallback, not recovery or proof of saved settlement.
        await session.RequestStopAsync(acceptedExecution);
        await cleaned.Task.WaitAsync(TestTimeout);
        try {
          await session.WaitForSettlementAsync(acceptedExecution).WaitAsync(TestTimeout);
        } catch (Exception failure) when (failure.GetBaseException() is SqliteException) {
          // Expected driver failure. Timeouts/other uncertain exits must not permit disposal.
        }

      }

      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> WriteAndCleanupFailureResponse(
      TaskCompletionSource disposed, [EnumeratorCancellation] CancellationToken token) {
    token.Register(() => throw new InvalidOperationException(CancellationCallbackFailureMessage));
    try {
      yield return Text(new string('x', 16 * 1024));
      await Task.Delay(Timeout.InfiniteTimeSpan, token);
    } finally {
      disposed.TrySetResult();
#pragma warning disable CA2219 // Deliberately simulate a provider whose enumerator disposal throws.
      throw new InvalidOperationException(EnumeratorDisposalFailureMessage);
#pragma warning restore CA2219
    }
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> WriteFailureResponse(TaskCompletionSource<bool> cleaned,
      [EnumeratorCancellation] CancellationToken token) {
    try {
      yield return Text("working");
      await Task.Delay(Timeout.InfiniteTimeSpan, token);
    } finally {
      cleaned.TrySetResult(token.IsCancellationRequested);
    }
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> ReentrantBlockedCancellationResponse(
      PrimarySession session, TaskCompletionSource sourceEntered, TaskCompletionSource sourceRelease,
      TaskCompletionSource sourceExited, TaskCompletionSource<SessionSnapshot> callbackSnapshot,
      TaskCompletionSource callbackRelease,
      [EnumeratorCancellation] CancellationToken token) {
    // Do not dispose this registration in the iterator: enumerator cleanup must be
    // able to finish while the callback deliberately waits for its release barrier.
    token.Register(() => {
      // Stop has already cancelled token; these reads/waits must exercise the live callback.
      SessionSnapshot snapshot = session.ReadSnapshotAsync(CancellationToken.None)
          .WaitAsync(TestTimeout, CancellationToken.None).GetAwaiter().GetResult();
      callbackSnapshot.TrySetResult(snapshot);
      callbackRelease.Task.WaitAsync(TestTimeout, CancellationToken.None).GetAwaiter().GetResult();
    });

    try {
      yield return Text("working");
      sourceEntered.TrySetResult();
      await sourceRelease.Task;
    } finally {
      sourceExited.TrySetResult();
    }
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> CancellationCallbackFailureResponse(
      TaskCompletionSource requestEntered, TaskCompletionSource providerExited,
      [EnumeratorCancellation] CancellationToken token) {
    var cancellationObserved = Signal();
    token.Register(() => {
      cancellationObserved.TrySetResult();
      throw new InvalidOperationException(CancellationCallbackFailureMessage);
    });
    try {
      yield return Text("working");
      requestEntered.TrySetResult();
      await cancellationObserved.Task;
    } finally {
      providerExited.TrySetResult();
    }
  }

  private static IAsyncEnumerable<ChatResponseUpdate> FailedResponse(string failureScenario, CancellationToken token) {
    // Reject unknown test cases before they can masquerade as a scripted stream failure.
    var stop = new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
    var length = new ChatResponseUpdate { FinishReason = ChatFinishReason.Length };
    (ChatResponseUpdate[] Updates, bool ThrowAfterUpdates) script = failureScenario switch {
      "eof" => ([], false),
      "length" => ([length], false),
      "exception" => ([], true),
      "exception-after-stop" => ([stop], true),
      "non-text" => ([new() { Contents = [new FunctionResultContent("call", "unsupported")] }], false),
      "text-after-stop" => ([stop, Text("late text")], false),
      "length-after-stop" => ([stop, length], false),
      _ => throw new ArgumentOutOfRangeException(nameof(failureScenario))
    };
    return RespondAsync(token);

    async IAsyncEnumerable<ChatResponseUpdate> RespondAsync([EnumeratorCancellation] CancellationToken cancellation) {
      await Task.CompletedTask;
      cancellation.ThrowIfCancellationRequested();
      yield return Text("partial");
      foreach (ChatResponseUpdate update in script.Updates)
        yield return update;
      if (script.ThrowAfterUpdates)
        throw new InvalidOperationException("Scripted request failure.");
    }
  }

  private static async IAsyncEnumerable<ChatResponseUpdate> HeldCleanupResponse(TaskCompletionSource waiting,
      TaskCompletionSource cleanupEntered, TaskCompletionSource cleanupRelease,
      [EnumeratorCancellation] CancellationToken token) {
    try {
      yield return Text("working");
      waiting.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, token);
    } finally {
      cleanupEntered.TrySetResult();
      await cleanupRelease.Task;
    }
  }

  private static IEnumerable<Exception> FlattenFailures(Exception failure) {
    if (failure is AggregateException aggregate)
      return aggregate.InnerExceptions.SelectMany(FlattenFailures);

    if (failure.InnerException is { } inner)
      return FlattenFailures(inner);

    return [failure];
  }

  private static async Task AssertCancellationCallbackFailureAsync(Task operation) {
    AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
        async () => await operation.WaitAsync(TestTimeout));
    Assert.Contains(failure.Flatten().InnerExceptions,
        error => error is InvalidOperationException && error.Message == CancellationCallbackFailureMessage);
  }

}
