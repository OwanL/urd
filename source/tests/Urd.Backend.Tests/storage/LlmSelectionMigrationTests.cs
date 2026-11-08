using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Storage;

public sealed class LlmSelectionMigrationTests {
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task SchemaSevenUpgradePreservesHistoryAndNullSelectionRetriesWithoutRestart(bool settled) {
    using var database = new TemporaryDatabase();
    LegacyHistory history = CreateSchemaSeven(database, settled);
    AssertSchemaSeven(database);
    Action assertEvidence = CaptureEvidence(database);
    int factoryCalls = 0;

    using (UrdStore store = UrdStore.Open(database.Path)) {
      Assert.Equal(8L, database.Scalar("PRAGMA user_version;")); // raw-sql: migration version is under test
      Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM input_decisions WHERE llm_selection_json IS NOT NULL;")); // raw-sql: migration must not invent historical selections
      AssertReceipts(store, history);
      assertEvidence();
      var manager = new SessionManager(store, _ => {
        Interlocked.Increment(ref factoryCalls);
        throw new InvalidOperationException("Legacy history must never restart provider work.");
      });
      PrimarySession session = await manager.GetPrimaryAsync(history.SessionId).WaitAsync(TestTimeout);
      try {
        Assert.Same(session, await manager.GetPrimaryAsync(history.SessionId).WaitAsync(TestTimeout));
        SessionSnapshot snapshot = await session.ReadSnapshotAsync().WaitAsync(TestTimeout);
        Assert.Equal(!settled, snapshot.AdmissionFenced);
        Assert.Equal(settled ? null : (Guid?)history.ExecutionId, snapshot.UnsettledExecutionId);
        Assert.False(snapshot.StopRequested);
        Assert.Null(snapshot.LatestLlmSelection);
        Assert.Equal(history.Messages, snapshot.Messages.Select(message => message.Preview));
        if (settled)
          Assert.Equal(new SessionExecutionResult(history.ExecutionId, ExecutionOutcome.Completed), snapshot.LastSettlement);
        else
          Assert.Null(snapshot.LastSettlement);

        await AssertRetriesAsync();
        await AssertFreshNullRejectedAsync();
        foreach (UserMessageReceipt receipt in history.Receipts) {
          UserMessageSubmission original = receipt.Submission;
          UserMessageSubmission[] conflicts = [
              original with { LlmSelection = TestLlmSelection.Default },
              original with { Text = original.Text + " " },
              original with { ExpectedHistoryRevision = original.ExpectedHistoryRevision + 1 }
          ];
          foreach (UserMessageSubmission conflict in conflicts)
            await Assert.ThrowsAsync<UserMessageConflictException>(() => session.SubmitUserMessageAsync(conflict));
        }

        StopReceipt expectedStop = settled
            ? new(history.ExecutionId, StopStatus.Settled, ExecutionOutcome.Completed)
            : new(history.ExecutionId, StopStatus.Unavailable);
        Assert.Equal(expectedStop, await session.RequestStopAsync(history.ExecutionId).WaitAsync(TestTimeout));
        await AssertRetriesAsync();
        await AssertFreshNullRejectedAsync();
        assertEvidence();
      } finally {
        await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      }

      // A retained handle still resolves originals after shutdown, before fresh-command validation.
      await AssertRetriesAsync();
      await AssertFreshNullRejectedAsync();
      SessionSnapshot shutdown = await session.ReadSnapshotAsync().WaitAsync(TestTimeout);
      Assert.True(shutdown.AdmissionFenced);
      Assert.Null(shutdown.LatestLlmSelection);
      Assert.Equal(settled ? null : (Guid?)history.ExecutionId, shutdown.UnsettledExecutionId);
      Assert.False(shutdown.StopRequested);
      Assert.Equal(0, factoryCalls);
      AssertReceipts(store, history);
      assertEvidence();

      async Task AssertRetriesAsync() {
        foreach (UserMessageReceipt receipt in history.Receipts) {
          Assert.Null(receipt.Submission.LlmSelection);
          Assert.Equal(receipt, await session.GetUserMessageReceiptAsync(receipt.Submission.SubmissionId).WaitAsync(TestTimeout));
          Assert.Equal(receipt, await session.SubmitUserMessageAsync(receipt.Submission).WaitAsync(TestTimeout));
          var retry = store.UserMessages.Admit(history.SessionId, 0, receipt.Submission, false, Guid.NewGuid());
          Assert.Equal(receipt, retry.Receipt);
          Assert.False(retry.NewlyAccepted);
        }
      }

      async Task AssertFreshNullRejectedAsync() {
        var fresh = new UserMessageSubmission(Guid.NewGuid(), 0, "new input without a selection", null);
        await Assert.ThrowsAsync<ArgumentException>(() => session.SubmitUserMessageAsync(fresh));
        Assert.Throws<ArgumentException>(() => store.UserMessages.Admit(history.SessionId, 0, fresh, true, Guid.NewGuid()));
        Assert.Null(await session.GetUserMessageReceiptAsync(fresh.SubmissionId).WaitAsync(TestTimeout));
      }
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    AssertReceipts(reopened, history);
    Assert.Null(reopened.Snapshots.ReadStored(history.SessionId).LatestLlmSelection);
    assertEvidence();
    if (settled)
      return;

    var executions = StoredExecutions.Read(database);
    var requests = StoredExecutions.ReadRequests(database);
    var reopenedManager = new SessionManager(reopened, _ => {
      Interlocked.Increment(ref factoryCalls);
      throw new InvalidOperationException("A fresh selection cannot recover a legacy execution.");
    });
    try {
      PrimarySession session = await reopenedManager.GetPrimaryAsync(history.SessionId).WaitAsync(TestTimeout);
      UserMessageReceipt refused = await session.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, "must not resume legacy work", TestLlmSelection.Default)).WaitAsync(TestTimeout);
      Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
      Assert.Equal(MessageRefusalReason.Capacity, refused.RefusalReason);
      Assert.Null(refused.ExecutionId);
      Assert.Equal(executions, StoredExecutions.Read(database));
      Assert.Equal(requests, StoredExecutions.ReadRequests(database));
      Assert.Equal(history.Messages, (await session.ReadSnapshotAsync().WaitAsync(TestTimeout)).Messages.Select(message => message.Preview));
    } finally {
      await reopenedManager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }
    Assert.Equal(0, factoryCalls);
  }

  [Fact]
  public void InputDecisionTableCollisionRollsBackSchemaSevenRowsAndVersion() {
    using var database = new TemporaryDatabase();
    LegacyHistory history = CreateSchemaSeven(database, settled: false);
    AssertSchemaSeven(database);
    Action assertEvidence = CaptureEvidence(database);
    object? originalSchema = database.Scalar("SELECT sql FROM sqlite_schema WHERE name = 'input_decisions';"); // raw-sql: rollback must retain the old constraints, not only its rows
    database.Execute("CREATE TABLE input_decisions_v8 (witness TEXT); INSERT INTO input_decisions_v8 VALUES ('retained');");

    Assert.Throws<SqliteException>(() => {
      using UrdStore store = UrdStore.Open(database.Path);
    });
    AssertSchemaSeven(database);
    Assert.Equal(originalSchema, database.Scalar("SELECT sql FROM sqlite_schema WHERE name = 'input_decisions';")); // raw-sql: failed rebuild must not replace the original table
    Assert.Equal("retained", database.Scalar("SELECT witness FROM input_decisions_v8;")); // raw-sql: fixture-owned collision survives the failed migration
    assertEvidence();

    database.Execute("DROP TABLE input_decisions_v8;");
    using UrdStore migrated = UrdStore.Open(database.Path);
    Assert.Equal(8L, database.Scalar("PRAGMA user_version;")); // raw-sql: removing the collision permits a normal upgrade
    AssertReceipts(migrated, history);
    assertEvidence();

    // The rebuilt CHECK permits schema eight's new refusal, with real selection JSON.
    Guid session = migrated.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "new selected execution", TestLlmSelection.Default);
    Assert.True(migrated.UserMessages.Admit(session, 0, submission, true, execution).NewlyAccepted);
    var steering = submission with {
      SubmissionId = Guid.NewGuid(),
      LlmSelection = TestLlmSelection.Default with { ModelId = "different-model" }
    };
    UserMessageReceipt refused = migrated.UserMessages.Admit(session, 0, steering, true, Guid.NewGuid(), execution).Receipt;
    Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
    Assert.Equal(MessageRefusalReason.SelectionLocked, refused.RefusalReason);
    Assert.Equal(refused, migrated.UserMessages.ReadReceipt(session, steering.SubmissionId));
  }

  private static LegacyHistory CreateSchemaSeven(TemporaryDatabase database, bool settled) {
    Guid session = Guid.NewGuid(), execution = Guid.NewGuid();
    var provider = new ProviderConnection(Guid.NewGuid(), "legacy provider", ProviderRoute.Custom,
        LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/v1/"), AuthenticationMethod.ApiKey);
    LlmSelection selection = TestLlmSelection.Create(provider);
    var receipts = new List<UserMessageReceipt>();
    var messages = new List<string>();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(session);
      ProviderState state = store.Providers.Add(provider);
      store.Providers.SetCredentials(provider.Id, state.Generation, null,
          new("synthetic-api-key", null, null, null, null, []));
      store.Providers.GetOrCreateHostId();
      Admit("original \0 e\u0301 🙂\r\n");
      Guid first = store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId;
      Output(first, "display answer \0 🙂", final: true);
      var native = new LlmProviderResponse(new(provider.Id, state.Generation, provider.Api, selection.ModelId), """
        {"version":1,"items":[
          {"type":"reasoning","id":"rs_legacy","summary":[{"type":"summary_text","text":"reasoning"}],"encrypted_content":"legacy-state"},
          {"type":"message","id":"msg_legacy","role":"assistant","content":[{"type":"output_text","text":"native answer"}]}
        ]}
        """);
      Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
          new(first, ExecutionOutcome.Completed, ProviderResponse: native)));
      Admit("delivered steering\r\n");
      Guid second = store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId;
      Output(second, settled ? "settled answer" : "partial answer \0 🙂", final: settled);
      if (settled) {
        Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0, new(second, ExecutionOutcome.Completed)));
        Assert.Equal(ExecutionOutcome.Completed, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
      } else {
        Admit("pending steering \0 🙂");
        Refuse("capacity refusal", 0, accepting: true, MessageRefusalReason.Capacity);
      }
      Refuse("stale refusal\r\n", 1, accepting: true, MessageRefusalReason.StaleRevision);
      Refuse("shutdown refusal \0 🙂", 0, accepting: false, MessageRefusalReason.Stopping);

      void Admit(string text) {
        var submission = new UserMessageSubmission(Guid.NewGuid(), 0, text, selection);
        var admission = store.UserMessages.Admit(session, 0, submission, true, execution, execution);
        Assert.True(admission.NewlyAccepted);
        receipts.Add(admission.Receipt);
        messages.Add(text);
      }

      void Output(Guid request, string text, bool final) {
        Assert.True(store.LlmResponses.Append(session, execution, 0, new(request, Guid.NewGuid(), 0, text, final)));
        messages.Add(text);
      }

      void Refuse(string text, long revision, bool accepting, MessageRefusalReason reason) {
        var admission = store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), revision, text, selection), accepting, Guid.NewGuid());
        Assert.False(admission.NewlyAccepted);
        Assert.Equal(MessageAcceptance.Refused, admission.Receipt.Acceptance);
        Assert.Equal(reason, admission.Receipt.RefusalReason);
        receipts.Add(admission.Receipt);
      }
    }

    SchemaSevenDatabase.Downgrade(database);
    return new(session, execution, receipts.Select(receipt => receipt with {
      Submission = receipt.Submission with { LlmSelection = null }
    }).ToArray(), messages.ToArray());
  }

  private static void AssertSchemaSeven(TemporaryDatabase database) {
    Assert.Equal(7L, database.Scalar("PRAGMA user_version;")); // raw-sql: fixture/rollback must really be schema seven
    Assert.Equal(7L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('input_decisions');")); // raw-sql: old receipt table has precisely seven columns
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM pragma_table_info('input_decisions') WHERE name = 'llm_selection_json';")); // raw-sql: no selection column may exist before migration
    SqliteException error = Assert.Throws<SqliteException>(() =>
        database.Execute("UPDATE input_decisions SET refusal_reason = 4 WHERE decision = 2;"));
    Assert.Equal(275, error.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_CHECK: the old refusal CHECK must reject four
    Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: downgrade/rollback must retain child references
  }

  private static void AssertReceipts(UrdStore store, LegacyHistory history) {
    foreach (UserMessageReceipt receipt in history.Receipts)
      Assert.Equal(receipt, store.UserMessages.ReadReceipt(history.SessionId, receipt.Submission.SubmissionId));
  }

  private static Action CaptureEvidence(TemporaryDatabase database) {
    var sessions = StoredSessions.Read(database);
    var creation = StoredSessions.ReadCreation(database);
    var submissions = StoredSubmissions.Read(database);
    var executions = StoredExecutions.Read(database);
    var requests = StoredExecutions.ReadRequests(database);
    var deliveries = StoredDeliveries.Read(database);
    var messages = StoredDeliveries.ReadMessages(database);
    var order = StoredDeliveries.ReadRequestOrder(database);
    var tools = StoredTools.Read(database);
    var output = StoredLlmResponses.Read(database);
    var providers = StoredProviders.Read(database);
    var hosts = StoredProviders.ReadHostIds(database);
    const string receiptEvidenceSql = """
      SELECT json_group_array(json_array(session_id, input_id, expected_history_revision,
          hex(original_text_utf8), decision, run_id, refusal_reason))
      FROM (SELECT * FROM input_decisions ORDER BY session_id, input_id);
      """;
    object? receiptRows = database.Scalar(receiptEvidenceSql); // raw-sql: supplement shared identity reader with every legacy receipt field, including exact text bytes
    object? nativeRequest = database.Scalar("SELECT request_id FROM execution_provider_context;"); // raw-sql: retain the native envelope's request association
    object? native = database.Scalar("SELECT response_json FROM execution_provider_context;"); // raw-sql: preserve the exact serialized native envelope, not a reconstructed display response
    Assert.NotNull(native);
    Assert.Equal("legacy-state", Assert.Single(LlmProviderContext.Restore(
        JsonSerializer.Deserialize<LlmProviderResponse>((string)native)!).Contents.OfType<TextReasoningContent>()).ProtectedData);
    return () => {
      Assert.Equal(sessions, StoredSessions.Read(database));
      Assert.Equal(creation, StoredSessions.ReadCreation(database));
      Assert.Equal(submissions, StoredSubmissions.Read(database));
      Assert.Equal(receiptRows, database.Scalar(receiptEvidenceSql)); // raw-sql: rollback and migration preserve all original receipt values
      Assert.Equal(executions, StoredExecutions.Read(database));
      Assert.Equal(requests, StoredExecutions.ReadRequests(database));
      Assert.Equal(deliveries, StoredDeliveries.Read(database));
      Assert.Equal(messages, StoredDeliveries.ReadMessages(database));
      Assert.Equal(order, StoredDeliveries.ReadRequestOrder(database));
      Assert.Equal(tools, StoredTools.Read(database));
      var currentOutput = StoredLlmResponses.Read(database);
      Assert.Equal(output.Count, currentOutput.Count);
      for (int index = 0; index < output.Count; index++) {
        var expected = output[index];
        var actual = currentOutput[index];
        Assert.Equal((expected.RequestId, expected.ResponseId, expected.ChunkIndex, expected.Final),
            (actual.RequestId, actual.ResponseId, actual.ChunkIndex, actual.Final));
        Assert.Equal(expected.Bytes, actual.Bytes);
      }
      Assert.Equal(providers, StoredProviders.Read(database));
      Assert.Equal(hosts, StoredProviders.ReadHostIds(database));
      Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM execution_provider_context;")); // raw-sql: native context must not be dropped or synthesized
      Assert.Equal(nativeRequest, database.Scalar("SELECT request_id FROM execution_provider_context;")); // raw-sql: native context must remain attached to its original request
      Assert.Equal(native, database.Scalar("SELECT response_json FROM execution_provider_context;")); // raw-sql: exact persisted native context is migration evidence
      Assert.Null(database.Scalar("PRAGMA foreign_key_check;")); // raw-sql: preservation includes referential integrity
    };
  }

  private sealed record LegacyHistory(Guid SessionId, Guid ExecutionId, UserMessageReceipt[] Receipts, string[] Messages);
}
