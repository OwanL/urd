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

public sealed class SessionExecutionTests {
  [Theory]
  [InlineData("eof")]
  [InlineData("length")]
  [InlineData("exception-after-stop")]
  public async Task RequiresPositiveTerminalEvidenceAndAnOtherwiseSuccessfulRequest(string scenario) {
    var client = new ScriptedChatClient(token => FailedResponse(scenario, token));
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Failed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(ExecutionOutcome.Failed, Assert.Single(StoredExecutions.ReadRequests(fixture.Database)).Outcome);
    Assert.All(StoredLlmResponses.Read(fixture.Database), row => Assert.False(row.Final));
    Assert.Equal(0L, fixture.Database.Scalar("SELECT count(*) FROM execution_requests WHERE usage_json IS NOT NULL;")); // raw-sql: missing provider usage must remain unknown on request rows
    Assert.Equal(1, client.Calls);
    Assert.Equal(0, client.DisposeCalls);
  }

  [Fact]
  public async Task PositiveStopMarkerAllowsTrailingUsageAndEmptyTextBeforeSettlement() {
    async IAsyncEnumerable<ChatResponseUpdate> Response([EnumeratorCancellation] CancellationToken token) {
      await Task.CompletedTask;
      token.ThrowIfCancellationRequested();
      yield return new() {
        Contents = [new TextContent("answer"), new UsageContent(new UsageDetails {
          InputTokenCount = 12, OutputTokenCount = 3, TotalTokenCount = 15
        })],
        FinishReason = ChatFinishReason.Stop
      };
      yield return new() { Contents = [new UsageContent(new UsageDetails {
        InputTokenCount = 2, OutputTokenCount = 1, TotalTokenCount = 3
      }), new TextContent("")] };
    }

    var client = new ScriptedChatClient(Response);
    await using var fixture = new ExecutionFixture(client);
    Guid executionId = await fixture.StartExecutionAsync("go");

    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(executionId).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal("answer", StoredLlmResponses.ReadText(fixture.Database));
    Assert.Single(StoredLlmResponses.Read(fixture.Database), chunk => chunk.Final);
    using (SqliteConnection connection = fixture.Database.OpenReadOnlyConnection())
    using (SqliteCommand command = connection.CreateCommand()) {
      command.CommandText = "SELECT usage_json FROM execution_requests;";
      using SqliteDataReader reader = await command.ExecuteReaderAsync();
      Assert.True(await reader.ReadAsync());
      using JsonDocument usage = JsonDocument.Parse(reader.GetString(0));
      Assert.Equal(14, usage.RootElement.GetProperty("InputTokenCount").GetInt64());
      Assert.Equal(4, usage.RootElement.GetProperty("OutputTokenCount").GetInt64());
      Assert.Equal(18, usage.RootElement.GetProperty("TotalTokenCount").GetInt64());
    }
    Assert.Equal(1, client.Calls);
  }

  [Fact]
  public async Task OutputWriteFailureCleansUpButFencesRatherThanSettlesOrReplacesTheExecution() {
    var database = new TemporaryDatabase();
    var store = UrdStore.Open(database.Path);
    var cleaned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new ScriptedChatClient(token => WriteFailureResponse(cleaned, token));
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid()), store, _ => client);
    Guid? executionId = null;
    try {
      database.Execute("""
        CREATE TRIGGER reject_output BEFORE INSERT ON execution_output
        BEGIN SELECT RAISE(ABORT, 'Injected output write failure'); END;
        """);
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), 0, "go", TestLlmSelection.Default));
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
      Assert.Equal("go", snapshot.FirstUserPreview);
      TranscriptHistoryPage history = await session.Transcript.ReadHistoryAsync(
          new(TranscriptHistoryMode.Latest, snapshot.HistoryRevision));
      Assert.Single(history.Entries, entry => entry.Entry.Kind == TranscriptEntryKind.User);
      Assert.Null(Assert.Single(StoredExecutions.Read(database)).Outcome);
      Assert.Null(Assert.Single(StoredExecutions.ReadRequests(database)).Outcome);
      Assert.Empty(StoredLlmResponses.Read(database));
      UserMessageReceipt refused = await session.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), 0, "replacement", TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
      Assert.Equal(MessageRefusalReason.Stopping, refused.RefusalReason);
      Assert.Null(refused.ExecutionId);
      Assert.Single(StoredExecutions.Read(database));
      Assert.Equal(1, client.Calls);
      Assert.Equal(0, client.DisposeCalls);
    } finally {
      if (executionId is { } acceptedExecution) {
        // Stop is only a cleanup fallback, not recovery or proof of saved settlement.
        await session.RequestStopAsync(acceptedExecution).WaitAsync(TestTimeout);
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

  private static async IAsyncEnumerable<ChatResponseUpdate> WriteFailureResponse(
      TaskCompletionSource<bool> cleaned, [EnumeratorCancellation] CancellationToken token) {
    try {
      yield return Text("working");
      await Task.Delay(Timeout.InfiniteTimeSpan, token);
    } finally {
      cleaned.TrySetResult(token.IsCancellationRequested);
    }
  }

  private static IAsyncEnumerable<ChatResponseUpdate> FailedResponse(string scenario, CancellationToken token) {
    var stop = new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
    var length = new ChatResponseUpdate { FinishReason = ChatFinishReason.Length };
    (ChatResponseUpdate[] Updates, bool ThrowAfterUpdates) script = scenario switch {
      "eof" => ([], false),
      "length" => ([length], false),
      "exception-after-stop" => ([stop], true),
      _ => throw new ArgumentOutOfRangeException(nameof(scenario))
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
}
