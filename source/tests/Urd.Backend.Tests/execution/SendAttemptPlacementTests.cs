using System.Collections.Concurrent;
using System.Globalization;
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

public sealed class SendAttemptPlacementTests {
  [Fact]
  public async Task PlacesHistoryAfterClientPreparationAndBeforeTheEagerProviderCall() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var factoryEntered = Signal();
    var factoryRelease = Signal();
    var providerObserved = new TaskCompletionSource<(long Attempt, long UserRows)>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var client = new ScriptedChatClient(token => {
      providerObserved.TrySetResult((ReadAttempt(database), ReadUserRowCount(database)));
      return Complete(token);
    });
    int factoryCalls = 0;
    var session = CreateSession(store, _ => {
      Interlocked.Increment(ref factoryCalls);
      factoryEntered.TrySetResult();
      factoryRelease.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
      return client;
    });
    var submission = NewSubmission("go");
    Guid? executionId = null;

    try {
      UserMessageReceipt receipt = await AcceptAsync(session, submission);
      executionId = Assert.IsType<Guid>(receipt.ExecutionId);
      await factoryEntered.Task.WaitAsync(TestTimeout);

      StoredDelivery delivery = Assert.Single(StoredDeliveries.Read(database));
      Assert.Equal(executionId, delivery.ExecutionId);
      Assert.NotNull(delivery.RequestId); // The request is assigned before client preparation.
      Assert.Null(delivery.Outcome);
      Assert.Empty((await ReadHistoryAsync(session)).Entries);
      Assert.Equal(0, client.Calls);
      Assert.Equal(receipt, await session.SubmitUserMessageAsync(submission));
      Assert.Single(StoredDeliveries.Read(database));

      factoryRelease.TrySetResult();
      Assert.Equal((2L, 1L), await providerObserved.Task.WaitAsync(TestTimeout));
      Assert.Equal(ExecutionOutcome.Completed,
          (await session.WaitForSettlementAsync(executionId.Value).WaitAsync(TestTimeout)).Outcome);
      Assert.Contains((await ReadHistoryAsync(session)).Entries,
          entry => entry.Entry.Kind == TranscriptEntryKind.User);
      Assert.Equal(1, client.Calls);
      Assert.Equal(1, Volatile.Read(ref factoryCalls));
    } finally {
      factoryRelease.TrySetResult();
      if (executionId is { } id)
        await session.WaitForSettlementAsync(id).WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task PreparationFailureLeavesInputNotSentWithoutHistoryOrProviderContext() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(Complete);
    var session = CreateSession(store, _ => throw new InvalidOperationException("Client preparation failed."));
    var submission = NewSubmission("before hook");
    UserMessageReceipt receipt = await AcceptAsync(session, submission);
    Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);

    Assert.Equal(ExecutionOutcome.Failed,
        (await session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(receipt, await session.GetUserMessageReceiptAsync(submission.SubmissionId));
    Assert.Empty((await ReadHistoryAsync(session)).Entries);
    Assert.Empty(StoredLlmResponses.Read(database));
    Assert.Equal(0L, ReadProviderContextCount(database));
    Assert.Equal(1L, ReadAttempt(database));
    Assert.Equal(ExecutionOutcome.Failed, Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
    Assert.Equal(ExecutionOutcome.Failed, Assert.Single(StoredExecutions.Read(database)).Outcome);
    Assert.Equal(new NotSentInput(submission.SubmissionId, executionId, submission.Text),
        Assert.Single((await session.ReadNotSentInputsAsync()).Inputs));
    Assert.Equal(0, client.Calls);
  }

  [Fact]
  public async Task ProviderFailureAfterStartRetainsUserHistoryAndPartialOutput() {
    const int partialLength = 16 * 1024;
    string partial = new('x', partialLength);
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var client = new ScriptedChatClient(token => FailAfterPartial(partial, token));
    var session = CreateSession(store, _ => client);
    var submission = NewSubmission("after hook");
    UserMessageReceipt receipt = await AcceptAsync(session, submission);
    Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);

    Assert.Equal(ExecutionOutcome.Failed,
        (await session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    TranscriptHistoryPage history = await ReadHistoryAsync(session);
    Assert.Contains(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.User);
    Assert.Contains(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.Text);
    Assert.Equal(partial, StoredLlmResponses.ReadText(database));
    Assert.False(Assert.Single(StoredLlmResponses.Read(database)).Final);
    Assert.Equal(2L, ReadAttempt(database));
    Assert.Equal(ExecutionOutcome.Failed, Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
    Assert.Empty((await session.ReadNotSentInputsAsync()).Inputs);
    Assert.Equal(0L, ReadProviderContextCount(database));
    Assert.Equal(1, client.Calls);
  }

  [Fact]
  public async Task StopDuringPreparationPreventsProviderCallAndLeavesInputNotSent() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var factoryEntered = Signal();
    var factoryRelease = Signal();
    using var client = new ScriptedChatClient(Complete);
    var session = CreateSession(store, _ => {
      factoryEntered.TrySetResult();
      factoryRelease.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
      return client;
    });
    var submission = NewSubmission("stop before hook");
    Guid? executionId = null;

    try {
      UserMessageReceipt receipt = await AcceptAsync(session, submission);
      executionId = Assert.IsType<Guid>(receipt.ExecutionId);
      await factoryEntered.Task.WaitAsync(TestTimeout);
      StoredDelivery delivery = Assert.Single(StoredDeliveries.Read(database));
      Assert.NotNull(delivery.RequestId);
      Assert.Empty((await ReadHistoryAsync(session)).Entries);
      Assert.Equal(receipt, await session.SubmitUserMessageAsync(submission));

      Assert.Equal(new StopReceipt(executionId.Value, StopStatus.Requested),
          await session.RequestStopAsync(executionId.Value).WaitAsync(TestTimeout));
      factoryRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Cancelled,
          (await session.WaitForSettlementAsync(executionId.Value).WaitAsync(TestTimeout)).Outcome);

      Assert.Equal(0, client.Calls);
      Assert.Equal(1L, ReadAttempt(database));
      Assert.Empty((await ReadHistoryAsync(session)).Entries);
      Assert.Equal(new NotSentInput(submission.SubmissionId, executionId.Value, submission.Text),
          Assert.Single((await session.ReadNotSentInputsAsync()).Inputs));
      Assert.Equal(0L, ReadProviderContextCount(database));
    } finally {
      factoryRelease.TrySetResult();
      if (executionId is { } id)
        await session.WaitForSettlementAsync(id).WaitAsync(TestTimeout);
    }
  }

  [Fact]
  public async Task MarkerCommitFailureFencesUnsettledExecutionAndNotifiesObservers() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    database.Execute("""
      CREATE TABLE deferred_attempt_failure (
          request_id TEXT NOT NULL REFERENCES execution_requests(request_id) DEFERRABLE INITIALLY DEFERRED
      ) STRICT;
      CREATE TRIGGER fail_attempt_commit
      AFTER UPDATE OF attempt ON execution_requests
      WHEN NEW.attempt = 2
      BEGIN
          INSERT INTO deferred_attempt_failure(request_id) VALUES ('missing-request');
      END;
      """);
    using var client = new ScriptedChatClient(Complete);
    var session = CreateSession(store, _ => client);
    var changes = new ConcurrentQueue<SessionChange>();
    using IDisposable subscription = await session.SubscribeAsync(changes.Enqueue, watched: true);
    var submission = NewSubmission("uncertain marker");
    UserMessageReceipt receipt = await AcceptAsync(session, submission);
    Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);

    Exception failure = await Assert.ThrowsAnyAsync<Exception>(
        () => session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout));
    Assert.Equal(19, Assert.IsType<SqliteException>(failure.GetBaseException()).SqliteErrorCode);
    Assert.Equal(receipt, await session.SubmitUserMessageAsync(submission));

    SessionSnapshot snapshot = await session.ReadSnapshotAsync();
    Assert.True(snapshot.AdmissionFenced);
    Assert.Equal(executionId, snapshot.UnsettledExecutionId);
    Assert.Null(snapshot.LastSettlement);
    Assert.Null(Assert.Single(StoredExecutions.Read(database)).Outcome);
    Assert.Null(Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
    Assert.Equal(1L, ReadAttempt(database)); // The deferred constraint rolled the marker transaction back.
    Assert.Equal(0L, ReadUserRowCount(database));
    Assert.NotNull(Assert.Single(StoredDeliveries.Read(database)).RequestId);
    Assert.Empty(StoredLlmResponses.Read(database));
    Assert.Equal(0L, ReadProviderContextCount(database));
    Assert.Equal(0, client.Calls);

    SessionChange attention = Assert.Single(changes, change => change.Status == "needs-attention");
    Assert.Contains(changes, change => change.Status is null && change.Version == attention.Version);
  }

  private static PrimarySession CreateSession(UrdStore store, Func<Urd.Core.Providers.ProviderConnection, IChatClient> createClient) =>
      new(store.Sessions.CreatePrimary(Guid.NewGuid()), store, createClient);

  private static UserMessageSubmission NewSubmission(string text) =>
      new(Guid.NewGuid(), 0, text, TestLlmSelection.Default);

  private static async Task<UserMessageReceipt> AcceptAsync(PrimarySession session, UserMessageSubmission submission) {
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(submission);
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    return receipt;
  }

  private static async Task<TranscriptHistoryPage> ReadHistoryAsync(PrimarySession session) {
    SessionSnapshot snapshot = await session.ReadSnapshotAsync();
    return await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, snapshot.HistoryRevision));
  }

  private static long ReadAttempt(TemporaryDatabase database) =>
      Convert.ToInt64(database.Scalar("SELECT attempt FROM execution_requests;"), CultureInfo.InvariantCulture); // raw-sql: observe send-attempt marker timing

  private static long ReadUserRowCount(TemporaryDatabase database) =>
      Convert.ToInt64(database.Scalar("SELECT count(*) FROM execution_messages WHERE role = 0;"), CultureInfo.InvariantCulture); // raw-sql: observe history placement timing

  private static long ReadProviderContextCount(TemporaryDatabase database) =>
      Convert.ToInt64(database.Scalar("SELECT count(*) FROM execution_provider_context;"), CultureInfo.InvariantCulture); // raw-sql: verify provider context was not persisted

  private static async IAsyncEnumerable<ChatResponseUpdate> FailAfterPartial(string partial,
      [EnumeratorCancellation] CancellationToken token) {
    await Task.CompletedTask;
    token.ThrowIfCancellationRequested();
    yield return Text(partial);
    throw new InvalidOperationException("Provider stream failed after partial output.");
  }
}
