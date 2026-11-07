using System.Data.Common;
using System.Text;
using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

// Snapshot projection: bounded indexed message window, lifecycle facts and migration.
public sealed class SessionSnapshotStorageTests {
  [Fact]
  public void EmptyMissingAndDisposedReadsDoNotInventState() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    StoredSessionSnapshot snapshot = store.Snapshots.Read(session);
    Assert.Equal(0, snapshot.HistoryRevision);
    Assert.Empty(snapshot.Messages);
    Assert.False(snapshot.HasOlderMessages);
    Assert.Null(snapshot.UnsettledExecutionId);
    Assert.False(snapshot.StopRequested);
    Assert.Null(snapshot.LastSettlement);
    Assert.Throws<KeyNotFoundException>(() => store.Snapshots.Read(Guid.NewGuid()));
    Assert.Throws<ArgumentException>(() => store.Snapshots.Read(Guid.Empty));
    store.Dispose();
    Assert.Throws<ObjectDisposedException>(() => store.Snapshots.Read(session));
  }

  [Theory]
  [InlineData(30, true, false)]
  [InlineData(31, true, true)]
  [InlineData(60, false, false)]
  [InlineData(61, false, true)]
  public void NewestSixtyMessagesAreChronologicalAndOmissionIsExact(int executionCount, bool includeOutputs, bool hasOlder) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    SeedHistory(database, session, 0, executionCount, includeOutputs);
    StoredSessionSnapshot snapshot = store.Snapshots.Read(session);
    string[] expected = Enumerable.Range(1, executionCount)
        .SelectMany(index => includeOutputs ? new[] { $"input {index}", $"output {index}" } : new[] { $"input {index}" })
        .TakeLast(60).ToArray();
    Assert.Equal(expected, snapshot.Messages.Select(message => message.Preview));
    Assert.Equal(hasOlder, snapshot.HasOlderMessages);
    Assert.All(snapshot.Messages, message => {
      Assert.Equal(ExecutionOutcome.Completed, message.Settlement);
      Assert.False(message.Truncated);
      Assert.Equal(new MessageContentCursor(message.Message, 1, 0), message.AvailableThrough);
      Assert.Equal(message.Message.Role == MessageRole.Assistant, message.FinalOutputCommitted);
    });
    Assert.Throws<NotSupportedException>(() => ((IList<MessagePreview>)snapshot.Messages).Clear());
    SeedHistory(database, session, 0, 1, includeOutputs);
    Assert.Equal(expected, snapshot.Messages.Select(message => message.Preview));
  }

  [Theory]
  [InlineData(30, false)]
  [InlineData(31, true)]
  public void OneExecutionUsesMessageNotRequestOrExecutionCountForTheRecentWindow(int requestCount, bool hasOlder) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid();
    var submissions = new List<Guid>();
    var requests = new List<Guid>();
    for (int index = 1; index <= requestCount; index++) {
      Guid submission = Guid.NewGuid();
      submissions.Add(submission);
      Assert.True(store.UserMessages.Admit(session, 0, new(submission, 0, $"input {index}"), true,
          index == 1 ? execution : Guid.NewGuid(), activeExecutionId: index == 1 ? null : execution).NewlyAccepted);
      LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
      requests.Add(request.RequestId);
      Assert.True(store.LlmResponses.Append(session, execution, 0,
          new(request.RequestId, Guid.NewGuid(), 0, $"output {index}", true)));
      Assert.True(store.LlmResponses.CompleteRequest(session, execution, 0,
          new(request.RequestId, ExecutionOutcome.Completed)));
    }

    Assert.Equal(ExecutionOutcome.Completed, store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed)!.Outcome);
    if (hasOlder) {
      // Even within one execution, omitted message bodies must never be decoded for previews.
      database.Execute($"""
        UPDATE input_decisions SET original_text_utf8 = X'FF' WHERE input_id = '{submissions[0]:D}';
        UPDATE execution_output SET text_utf8 = X'FF' WHERE request_id = '{requests[0]:D}';
        """);
    }

    StoredSessionSnapshot snapshot = store.Snapshots.Read(session);
    Assert.Equal(Enumerable.Range(1, requestCount)
        .SelectMany(index => new[] { $"input {index}", $"output {index}" }).TakeLast(60),
        snapshot.Messages.Select(message => message.Preview));
    Assert.Equal(60, snapshot.Messages.Count);
    Assert.Equal(hasOlder, snapshot.HasOlderMessages);
    Assert.All(snapshot.Messages, message => {
      Assert.Equal(execution, message.Message.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed, message.Settlement);
    });
    Assert.Equal(60, snapshot.Messages.Select(message => message.Message.MessageId).Distinct().Count());
  }

  [Theory]
  [InlineData(39, false)]
  [InlineData(40, false)]
  [InlineData(41, true)]
  public void MixedExecutionsWithAndWithoutOutputKeepTheSameRecentMessageWindow(int executionCount, bool hasOlder) {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    SeedHistory(database, session, 0, executionCount, includeOutputs: true);
    database.Execute($"""
      DELETE FROM execution_messages WHERE role = 1 AND run_id IN (
          SELECT run_id FROM execution_runs WHERE session_id = '{session:D}' AND ordinal % 2 = 1);
      UPDATE execution_inputs SET request_id = NULL WHERE run_id IN (
          SELECT run_id FROM execution_runs WHERE session_id = '{session:D}' AND ordinal % 2 = 1);
      DELETE FROM execution_output WHERE request_id IN (
          SELECT q.request_id FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
          WHERE r.session_id = '{session:D}' AND r.ordinal % 2 = 1);
      DELETE FROM execution_requests WHERE run_id IN (
          SELECT run_id FROM execution_runs WHERE session_id = '{session:D}' AND ordinal % 2 = 1);
      """);

    StoredSessionSnapshot snapshot = store.Snapshots.Read(session);
    string[] expected = Enumerable.Range(1, executionCount)
        .SelectMany(index => index % 2 == 0 ? new[] { $"input {index}", $"output {index}" } : new[] { $"input {index}" })
        .TakeLast(60).ToArray();
    Assert.Equal(expected, snapshot.Messages.Select(message => message.Preview));
    Assert.Equal(hasOlder, snapshot.HasOlderMessages);
  }

  [Fact]
  public void LifecycleFactsSurviveReopenAndAreIndependentOfRevisionWindow() {
    using var database = new TemporaryDatabase();
    Guid session, terminal = Guid.NewGuid(), active = Guid.NewGuid();
    using (SessionStore store = SessionStore.Open(database.Path)) {
      session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "terminal"), true, terminal);
      store.Executions.TrySettle(session, terminal, ExecutionOutcome.Failed);
      store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, "unsettled"), true, active);
      store.Executions.RequestStop(session, active, ownsExecution: true);
    }

    database.ForceHistoryRevision(9);
    using SessionStore reopened = SessionStore.Open(database.Path);
    StoredSessionSnapshot snapshot = reopened.Snapshots.Read(session);
    Assert.Equal(9, snapshot.HistoryRevision);
    Assert.Empty(snapshot.Messages);
    Assert.False(snapshot.HasOlderMessages);
    Assert.Equal(active, snapshot.UnsettledExecutionId);
    Assert.True(snapshot.StopRequested);
    Assert.Equal(new SessionExecutionResult(terminal, ExecutionOutcome.Failed), snapshot.LastSettlement);
    Assert.Null(reopened.Executions.ReadSettlement(session, active)); // Reading never recovers or settles it.
  }

  [Fact]
  public void PrefixesAreScalarBoundedAndFinalOutputIsNotSettlement() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, execution = Guid.NewGuid(), output = Guid.NewGuid();
    store.UserMessages.Admit(session, 0, new(Guid.NewGuid(), 0, new string('a', 255) + "🙂\0tail"), true, execution);
    LlmRequest request = store.Executions.ClaimNextRequest(session, execution, 0)!;
    Assert.Single(store.Snapshots.Read(session).Messages); // Claim alone has no assistant row.
    store.LlmResponses.Append(session, execution, 0, new(request.RequestId, output, 0, new string('é', 300), false));
    store.LlmResponses.Append(session, execution, 0, new(request.RequestId, output, 1, "", true));
    StoredSessionSnapshot snapshot = store.Snapshots.Read(session);
    Assert.Equal(execution, snapshot.UnsettledExecutionId);
    Assert.Null(snapshot.LastSettlement);
    Assert.False(snapshot.StopRequested);
    MessagePreview submission = snapshot.Messages[0], assistant = snapshot.Messages[1];
    Assert.Equal(new string('a', 255), submission.Preview);
    Assert.True(submission.Truncated);
    Assert.Equal(new string('é', 256), assistant.Preview);
    Assert.True(assistant.Truncated);
    Assert.Equal(new MessageContentCursor(assistant.Message, 2, 0), assistant.AvailableThrough);
    Assert.True(assistant.FinalOutputCommitted);
    Assert.Null(assistant.Settlement);
    Assert.Null(submission.Settlement);
    Assert.True(snapshot.Messages.Sum(message => Encoding.UTF8.GetByteCount(message.Preview)) < 65536);

    store.LlmResponses.CompleteRequest(session, execution, 0, new(request.RequestId, ExecutionOutcome.Completed));
    store.Executions.TrySettle(session, execution, ExecutionOutcome.Completed);
    StoredSessionSnapshot settled = store.Snapshots.Read(session);
    Assert.Null(settled.UnsettledExecutionId);
    Assert.Equal(new SessionExecutionResult(execution, ExecutionOutcome.Completed), settled.LastSettlement);
    Assert.All(settled.Messages, message => Assert.Equal(ExecutionOutcome.Completed, message.Settlement));
    Assert.Null(assistant.Settlement); // The earlier detached snapshot does not change.
  }

  [Fact]
  public void PreviewsStillValidateMessageIdentityFromStoredRows() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    SeedHistory(database, session, 0, 1, includeOutputs: true);
    database.Execute($"UPDATE execution_messages SET message_id = '{Guid.Empty:D}' WHERE role = 1;");

    Assert.Throws<ArgumentException>(() => store.Snapshots.Read(session));
  }

  [Fact]
  public void LargeMixedHistoriesUseBoundedIndexedSelectionWithoutDecodingOmittedBodies() {
    using var database = new TemporaryDatabase();
    using SessionStore store = SessionStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid other = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    SeedHistory(database, session, 0, 3000, includeOutputs: true);
    database.Execute($"UPDATE sessions SET history_revision = 1 WHERE session_id = '{session:D}';");
    SeedHistory(database, session, 1, 3000, includeOutputs: true);
    SeedHistory(database, other, 0, 3000, includeOutputs: true);
    // If a read decodes all retained rows then trims, it fails on these omitted bytes.
    database.Execute($"""
      UPDATE input_decisions SET original_text_utf8 = X'FF'
      WHERE session_id = '{session:D}' AND input_id IN (
          SELECT input_id FROM execution_runs WHERE session_id = '{session:D}' ORDER BY ordinal LIMIT 4000);
      UPDATE execution_output SET text_utf8 = X'FF' WHERE request_id IN (
          SELECT q.request_id FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
          WHERE r.session_id = '{session:D}' ORDER BY r.ordinal LIMIT 4000);
      """);
    StoredSessionSnapshot snapshot = store.Snapshots.Read(session);
    Assert.Equal(60, snapshot.Messages.Count);
    Assert.True(snapshot.HasOlderMessages);
    Assert.Equal("input 2971", snapshot.Messages[0].Preview);
    Assert.Equal("output 3000", snapshot.Messages[^1].Preview);
    Assert.All(snapshot.Messages, message => Assert.Equal(1, message.Message.HistoryRevision));
    Assert.Equal(snapshot.Messages[^1].Message.ExecutionId, snapshot.LastSettlement!.ExecutionId);

    string[] windowPlan = Explain(database, SnapshotReader.SnapshotMessagesSql, session, 1);
    Assert.Contains(windowPlan, step => step.Contains("SEARCH execution_messages USING INDEX execution_messages_history_order", StringComparison.Ordinal));
    Assert.Contains(windowPlan, step => step.Contains("SEARCH r USING INDEX sqlite_autoindex_execution_runs_", StringComparison.Ordinal));
    Assert.DoesNotContain(windowPlan, step => step.Contains("SCAN execution_messages", StringComparison.Ordinal));
    Assert.DoesNotContain(windowPlan, step => step.Contains("execution_requests", StringComparison.Ordinal));
    // The only permitted temporary order is the outer, at-most-61-message window.
    Assert.DoesNotContain(windowPlan, step => step.Contains("RIGHT PART OF ORDER BY", StringComparison.Ordinal));
    string[] terminalPlan = Explain(database, SnapshotReader.SnapshotSettlementSql, session, 1);
    Assert.Contains(terminalPlan, step => step.Contains("USING INDEX execution_runs_terminal_order", StringComparison.Ordinal));
    Assert.DoesNotContain(terminalPlan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    string[] activePlan = Explain(database, SnapshotReader.SnapshotUnsettledSql, session, 1);
    Assert.Contains(activePlan, step => step.Contains("USING INDEX one_active_execution", StringComparison.Ordinal));
    Assert.DoesNotContain(activePlan, step => step.Contains("SCAN", StringComparison.Ordinal));
  }

  [Fact]
  public void VersionTwoMigrationPreservesReceiptsExecutionsRequestsAndOutput() {
    using var database = new TemporaryDatabase();
    Guid session, execution = Guid.NewGuid(), output = Guid.NewGuid(), refusedSubmissionId = Guid.NewGuid();
    Guid acceptedSubmissionId = Guid.NewGuid();
    Guid refusedProposedExecutionId = Guid.NewGuid(), request;
    UserMessageReceipt refusedReceipt;
    using (SessionStore store = SessionStore.Open(database.Path)) {
      session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      refusedReceipt = store.UserMessages.Admit(session, 0, new(refusedSubmissionId, 0, "refused\0🙂"),
          accepting: false, proposedExecutionId: refusedProposedExecutionId).Receipt;
      store.UserMessages.Admit(session, 0, new(acceptedSubmissionId, 0, "accepted\0🙂"), true, execution);
      request = store.Executions.ClaimNextRequest(session, execution, 0)!.RequestId;
      store.LlmResponses.Append(session, execution, 0, new(request, output, 0, "partial\0🙂", false));
      store.Executions.RequestStop(session, execution, ownsExecution: true);
    }

    MakeVersionTwo(database);
    using SessionStore migrated = SessionStore.Open(database.Path);
    Assert.Equal(5L, database.Scalar("PRAGMA user_version;")); // raw-sql: the migrated schema version is the subject under test
    UserMessageReceipt migratedRefusal = migrated.UserMessages.ReadReceipt(session, refusedSubmissionId)!;
    Assert.Equal(refusedReceipt, migratedRefusal);
    Assert.Equal(MessageAcceptance.Refused, migratedRefusal.Acceptance);
    Assert.Equal(MessageRefusalReason.Stopping, migratedRefusal.RefusalReason);
    Assert.Null(migratedRefusal.ExecutionId);
    Assert.DoesNotContain(StoredExecutions.Read(database), stored => stored.ExecutionId == refusedProposedExecutionId);
    StoredSessionSnapshot snapshot = migrated.Snapshots.Read(session);
    Assert.Equal(new[] { "accepted\0🙂", "partial\0🙂" }, snapshot.Messages.Select(message => message.Preview));
    Assert.Equal(execution, snapshot.UnsettledExecutionId);
    Assert.True(snapshot.StopRequested);
    Assert.Null(snapshot.LastSettlement);
    Assert.False(snapshot.Messages[1].FinalOutputCommitted);
    Assert.Equal(new(execution, acceptedSubmissionId, MessageRole.User, 0), snapshot.Messages[0].Message);
    Assert.Equal(new(execution, output, MessageRole.Assistant, 0), snapshot.Messages[1].Message);
    Assert.Equal("\0🙂", migrated.Content.Read(session,
        new(snapshot.Messages[1].Message, 0, Encoding.UTF8.GetByteCount("partial"))).Text);
    Assert.Equal(request, Assert.Single(StoredExecutions.ReadRequests(database)).RequestId);
    Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('execution_runs_history_order', 'execution_runs_terminal_order') AND type = 'index';")); // raw-sql: migrated indexes are schema structure under test
  }

  [Fact]
  public void FailedVersionTwoMigrationRollsBackFirstIndexAndVersionWithoutLosingRows() {
    using var database = new TemporaryDatabase();
    Guid session;
    using (SessionStore store = SessionStore.Open(database.Path)) {
      session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
      SeedHistory(database, session, 0, 2, includeOutputs: true);
    }

    MakeVersionTwo(database);
    database.Execute("CREATE TABLE execution_runs_terminal_order (value TEXT); INSERT INTO execution_runs_terminal_order VALUES ('retained');");
    Assert.Throws<SqliteException>(() => SessionStore.Open(database.Path));
    Assert.Equal(2L, database.Scalar("PRAGMA user_version;")); // raw-sql: rolled-back schema version is the subject under test
    Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'execution_runs_history_order';")); // raw-sql: rolled-back index is schema structure under test
    Assert.Equal("retained", database.Scalar("SELECT value FROM execution_runs_terminal_order;")); // raw-sql: fixture witness table must survive the rollback
    Assert.Equal(2, StoredExecutions.Read(database).Count);
    Assert.Equal(2, StoredExecutions.ReadRequests(database).Count);
    Assert.Equal(2, StoredLlmResponses.Read(database).Count);
    database.Execute("DROP TABLE execution_runs_terminal_order;");
    using SessionStore retried = SessionStore.Open(database.Path);
    Assert.Equal(4, retried.Snapshots.Read(session).Messages.Count);
  }

  private static void MakeVersionTwo(TemporaryDatabase database) => database.Execute("""
    BEGIN;
    DROP TABLE session_creation;
    DROP TABLE execution_tools;
    DROP TABLE execution_messages;
    DROP TABLE execution_inputs;
    CREATE TABLE execution_requests_v2 (
        request_id TEXT PRIMARY KEY NOT NULL,
        run_id TEXT NOT NULL UNIQUE REFERENCES execution_runs(run_id),
        output_id TEXT,
        next_batch_index INTEGER NOT NULL DEFAULT 0 CHECK (next_batch_index >= 0),
        output_final INTEGER NOT NULL DEFAULT 0 CHECK (output_final IN (0, 1)),
        outcome INTEGER CHECK (outcome IN (0, 1, 2)),
        error TEXT
    ) STRICT;
    CREATE TABLE execution_output_v2 (
        request_id TEXT NOT NULL REFERENCES execution_requests_v2(request_id),
        batch_index INTEGER NOT NULL CHECK (batch_index >= 0),
        text_utf8 BLOB NOT NULL CHECK (length(text_utf8) <= 16384),
        is_final INTEGER NOT NULL CHECK (is_final IN (0, 1)),
        PRIMARY KEY (request_id, batch_index)
    ) STRICT;
    INSERT INTO execution_requests_v2
    SELECT request_id, run_id, output_id, next_batch_index, output_final, outcome, error FROM execution_requests;
    INSERT INTO execution_output_v2 SELECT * FROM execution_output;
    DROP TABLE execution_output;
    DROP TABLE execution_requests;
    ALTER TABLE execution_requests_v2 RENAME TO execution_requests;
    ALTER TABLE execution_output_v2 RENAME TO execution_output;
    DROP INDEX execution_runs_history_order;
    DROP INDEX execution_runs_terminal_order;
    PRAGMA user_version = 2;
    COMMIT;
    """);

  private static string[] Explain(TemporaryDatabase database, string sql, Guid session, long revision) {
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.Path, Pooling = false }.ToString());
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "EXPLAIN QUERY PLAN " + sql;
    command.Parameters.AddWithValue("$session", session.ToString("D"));
    command.Parameters.AddWithValue("$revision", revision);
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Cast<DbDataRecord>().Select(row => row.GetString(3)).ToArray();
  }

  // Bulk fixture writes avoid the execution context builder, which deliberately reads full history.
  private static void SeedHistory(TemporaryDatabase database, Guid session, long revision, int executionCount, bool includeOutputs) {
    string submissionIdPrefix = Guid.NewGuid().ToString("D")[..24], executionIdPrefix = Guid.NewGuid().ToString("D")[..24];
    string requestIdPrefix = Guid.NewGuid().ToString("D")[..24], outputIdPrefix = Guid.NewGuid().ToString("D")[..24];
    database.Execute($"""
      BEGIN;
      WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x + 1 FROM n WHERE x < {executionCount})
      INSERT INTO input_decisions
      SELECT '{session:D}', '{submissionIdPrefix}' || printf('%012d', x), {revision}, CAST('input ' || x AS BLOB),
          1, '{executionIdPrefix}' || printf('%012d', x), NULL FROM n;
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
      SELECT session_id, run_id, expected_history_revision, input_id, 0 FROM input_decisions
      WHERE session_id = '{session:D}' AND input_id LIKE '{submissionIdPrefix}%' ORDER BY input_id;
      COMMIT;
      """);
    if (includeOutputs) {
      database.Execute($"""
        BEGIN;
        INSERT INTO execution_requests (request_id, run_id, output_id, next_batch_index, output_final, outcome)
        SELECT '{requestIdPrefix}' || substr(input_id, 25), run_id, '{outputIdPrefix}' || substr(input_id, 25), 1, 1, 0
        FROM execution_runs WHERE session_id = '{session:D}' AND input_id LIKE '{submissionIdPrefix}%';
        INSERT INTO execution_output
        SELECT request_id, 0, CAST('output ' || CAST(substr(request_id, 25) AS INTEGER) AS BLOB), 1
        FROM execution_requests WHERE request_id LIKE '{requestIdPrefix}%';
        COMMIT;
        """);
    }

    database.Execute($"""
      BEGIN;
      INSERT INTO execution_inputs (session_id, input_id, run_id, request_id, outcome)
      SELECT r.session_id, r.input_id, r.run_id, q.request_id, 0
      FROM execution_runs r LEFT JOIN execution_requests q ON q.run_id = r.run_id
      WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{submissionIdPrefix}%' ORDER BY r.ordinal;
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id, request_id)
      SELECT session_id, history_revision, run_id, role, message_id, input_id, request_id FROM (
          SELECT ordinal AS run_ordinal, session_id, history_revision, run_id,
              0 AS role, input_id AS message_id, input_id, NULL AS request_id
          FROM execution_runs WHERE session_id = '{session:D}' AND input_id LIKE '{submissionIdPrefix}%'
          UNION ALL
          SELECT r.ordinal, r.session_id, r.history_revision, r.run_id, 1, q.output_id, NULL, q.request_id
          FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
          WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{submissionIdPrefix}%'
      ) ORDER BY run_ordinal, role;
      COMMIT;
      """);
  }
}
