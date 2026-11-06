using System.Text;
using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

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
        Assert.Null(snapshot.UnsettledRunId);
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
    public void NewestSixtyMessagesAreChronologicalAndOmissionIsExact(int runs, bool outputs, bool hasOlder) {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
        SeedHistory(database, session, 0, runs, outputs);
        StoredSessionSnapshot snapshot = store.Snapshots.Read(session);
        string[] expected = Enumerable.Range(1, runs)
            .SelectMany(index => outputs ? new[] { $"input {index}", $"output {index}" } : new[] { $"input {index}" })
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
        SeedHistory(database, session, 0, 1, outputs);
        Assert.Equal(expected, snapshot.Messages.Select(message => message.Preview));
    }

    [Fact]
    public void LifecycleFactsSurviveReopenAndAreIndependentOfRevisionWindow() {
        using var database = new TemporaryDatabase();
        Guid session, terminal = Guid.NewGuid(), active = Guid.NewGuid();
        using (SessionStore store = SessionStore.Open(database.Path)) {
            session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
            store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "terminal"), true, terminal);
            store.Runs.TrySettle(session, terminal, ExecutionOutcome.Failed);
            store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "unsettled"), true, active);
            store.Runs.RequestStop(session, active);
        }

        database.Execute("UPDATE sessions SET history_revision = 9;");
        using SessionStore reopened = SessionStore.Open(database.Path);
        StoredSessionSnapshot snapshot = reopened.Snapshots.Read(session);
        Assert.Equal(9, snapshot.HistoryRevision);
        Assert.Empty(snapshot.Messages);
        Assert.False(snapshot.HasOlderMessages);
        Assert.Equal(active, snapshot.UnsettledRunId);
        Assert.True(snapshot.StopRequested);
        Assert.Equal(new ExecutionResult(terminal, ExecutionOutcome.Failed), snapshot.LastSettlement);
        Assert.Null(reopened.Runs.ReadSettlement(session, active)); // Reading never recovers or settles it.
    }

    [Fact]
    public void PrefixesAreScalarBoundedAndFinalOutputIsNotSettlement() {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id, run = Guid.NewGuid(), output = Guid.NewGuid();
        store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, new string('a', 255) + "🙂\0tail"), true, run);
        LlmRequest request = store.Runs.ClaimNextRequest(session, run, 0)!;
        Assert.Single(store.Snapshots.Read(session).Messages); // Claim alone has no assistant row.
        store.Output.Append(session, run, 0, new(request.RequestId, output, 0, new string('é', 300), false));
        store.Output.Append(session, run, 0, new(request.RequestId, output, 1, "", true));
        StoredSessionSnapshot snapshot = store.Snapshots.Read(session);
        Assert.Equal(run, snapshot.UnsettledRunId);
        Assert.Null(snapshot.LastSettlement);
        Assert.False(snapshot.StopRequested);
        MessagePreview input = snapshot.Messages[0], assistant = snapshot.Messages[1];
        Assert.Equal(new string('a', 255), input.Preview);
        Assert.True(input.Truncated);
        Assert.Equal(new string('é', 256), assistant.Preview);
        Assert.True(assistant.Truncated);
        Assert.Equal(new MessageContentCursor(assistant.Message, 2, 0), assistant.AvailableThrough);
        Assert.True(assistant.FinalOutputCommitted);
        Assert.Null(assistant.Settlement);
        Assert.Null(input.Settlement);
        Assert.True(snapshot.Messages.Sum(message => Encoding.UTF8.GetByteCount(message.Preview)) < 65536);

        store.Output.CompleteRequest(session, run, 0, new(request.RequestId, ExecutionOutcome.Completed));
        store.Runs.TrySettle(session, run, ExecutionOutcome.Completed);
        StoredSessionSnapshot settled = store.Snapshots.Read(session);
        Assert.Null(settled.UnsettledRunId);
        Assert.Equal(new ExecutionResult(run, ExecutionOutcome.Completed), settled.LastSettlement);
        Assert.All(settled.Messages, message => Assert.Equal(ExecutionOutcome.Completed, message.Settlement));
        Assert.Null(assistant.Settlement); // The earlier detached snapshot does not change.
    }

    [Fact]
    public void PreviewsStillValidateMessageIdentityFromStoredRows() {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
        SeedHistory(database, session, 0, 1, outputs: true);
        database.Execute($"UPDATE execution_requests SET output_id = '{Guid.Empty:D}';");

        Assert.Throws<ArgumentException>(() => store.Snapshots.Read(session));
    }

    [Fact]
    public void LargeMixedHistoriesUseBoundedIndexedSelectionWithoutDecodingOmittedBodies() {
        using var database = new TemporaryDatabase();
        using SessionStore store = SessionStore.Open(database.Path);
        Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
        Guid other = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
        SeedHistory(database, session, 0, 3000, true);
        database.Execute($"UPDATE sessions SET history_revision = 1 WHERE session_id = '{session:D}';");
        SeedHistory(database, session, 1, 3000, true);
        SeedHistory(database, other, 0, 3000, true);
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
        Assert.Equal(snapshot.Messages[^1].Message.RunId, snapshot.LastSettlement!.RunId);

        string[] windowPlan = Explain(database, SnapshotReader.SnapshotRunsSql, session, 1);
        Assert.Contains(windowPlan, step => step.Contains("SEARCH execution_runs USING INDEX execution_runs_history_order", StringComparison.Ordinal));
        Assert.Contains(windowPlan, step => step.Contains("SEARCH q USING INDEX sqlite_autoindex_execution_requests_", StringComparison.Ordinal));
        Assert.DoesNotContain(windowPlan, step => step.Contains("SCAN execution_runs", StringComparison.Ordinal));
        Assert.DoesNotContain(windowPlan, step => step.Contains("SCAN q", StringComparison.Ordinal));
        // The only permitted temporary order is the outer, at-most-61-run joined window.
        Assert.DoesNotContain(windowPlan, step => step.Contains("RIGHT PART OF ORDER BY", StringComparison.Ordinal));
        string[] terminalPlan = Explain(database, SnapshotReader.SnapshotSettlementSql, session, 1);
        Assert.Contains(terminalPlan, step => step.Contains("USING INDEX execution_runs_terminal_order", StringComparison.Ordinal));
        Assert.DoesNotContain(terminalPlan, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
        string[] activePlan = Explain(database, SnapshotReader.SnapshotUnsettledSql, session, 1);
        Assert.Contains(activePlan, step => step.Contains("USING INDEX one_active_execution", StringComparison.Ordinal));
        Assert.DoesNotContain(activePlan, step => step.Contains("SCAN", StringComparison.Ordinal));
    }

    [Fact]
    public void VersionTwoMigrationPreservesReceiptsRunsRequestsAndOutput() {
        using var database = new TemporaryDatabase();
        Guid session, run = Guid.NewGuid(), output = Guid.NewGuid(), refused = Guid.NewGuid();
        Guid request;
        using (SessionStore store = SessionStore.Open(database.Path)) {
            session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
            store.Inputs.RecordRefusal(session, new(refused, 0, "refused\0🙂"), InputRefusalReason.Capacity);
            store.Inputs.Admit(session, 0, new(Guid.NewGuid(), 0, "accepted\0🙂"), true, run);
            request = store.Runs.ClaimNextRequest(session, run, 0)!.RequestId;
            store.Output.Append(session, run, 0, new(request, output, 0, "partial\0🙂", false));
            store.Runs.RequestStop(session, run);
        }

        MakeVersionTwo(database);
        using SessionStore migrated = SessionStore.Open(database.Path);
        Assert.Equal(3L, database.Scalar("PRAGMA user_version;"));
        Assert.Equal("refused\0🙂", migrated.Inputs.ReadReceipt(session, refused)!.Command.Text);
        StoredSessionSnapshot snapshot = migrated.Snapshots.Read(session);
        Assert.Equal(new[] { "accepted\0🙂", "partial\0🙂" }, snapshot.Messages.Select(message => message.Preview));
        Assert.Equal(run, snapshot.UnsettledRunId);
        Assert.True(snapshot.StopRequested);
        Assert.Null(snapshot.LastSettlement);
        Assert.False(snapshot.Messages[1].FinalOutputCommitted);
        Assert.Equal(output, snapshot.Messages[1].Message.MessageId);
        Assert.Equal(request.ToString("D"), database.Scalar("SELECT request_id FROM execution_requests;"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('execution_runs_history_order', 'execution_runs_terminal_order') AND type = 'index';"));
    }

    [Fact]
    public void FailedVersionTwoMigrationRollsBackFirstIndexAndVersionWithoutLosingRows() {
        using var database = new TemporaryDatabase();
        Guid session;
        using (SessionStore store = SessionStore.Open(database.Path)) {
            session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
            SeedHistory(database, session, 0, 2, true);
        }

        MakeVersionTwo(database);
        database.Execute("CREATE TABLE execution_runs_terminal_order (value TEXT); INSERT INTO execution_runs_terminal_order VALUES ('retained');");
        Assert.Throws<SqliteException>(() => SessionStore.Open(database.Path));
        Assert.Equal(2L, database.Scalar("PRAGMA user_version;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'execution_runs_history_order';"));
        Assert.Equal("retained", database.Scalar("SELECT value FROM execution_runs_terminal_order;"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM execution_runs;"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM execution_requests;"));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM execution_output;"));
        database.Execute("DROP TABLE execution_runs_terminal_order;");
        using SessionStore retried = SessionStore.Open(database.Path);
        Assert.Equal(4, retried.Snapshots.Read(session).Messages.Count);
    }

    private static void MakeVersionTwo(TemporaryDatabase database) => database.Execute("""
        DROP INDEX execution_runs_history_order;
        DROP INDEX execution_runs_terminal_order;
        PRAGMA user_version = 2;
        """);

    private static string[] Explain(TemporaryDatabase database, string sql, Guid session, long revision) {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.Path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("$session", session.ToString("D"));
        command.Parameters.AddWithValue("$revision", revision);
        using SqliteDataReader reader = command.ExecuteReader();
        var steps = new List<string>();
        while (reader.Read()) {
            steps.Add(reader.GetString(3));
        }

        return steps.ToArray();
    }

    // Bulk fixture writes avoid the execution context builder, which deliberately reads full history.
    private static void SeedHistory(TemporaryDatabase database, Guid session, long revision, int count, bool outputs) {
        string input = Guid.NewGuid().ToString("D")[..24], run = Guid.NewGuid().ToString("D")[..24];
        string request = Guid.NewGuid().ToString("D")[..24], output = Guid.NewGuid().ToString("D")[..24];
        database.Execute($"""
            BEGIN;
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x + 1 FROM n WHERE x < {count})
            INSERT INTO input_decisions
            SELECT '{session:D}', '{input}' || printf('%012d', x), {revision}, CAST('input ' || x AS BLOB),
                1, '{run}' || printf('%012d', x), NULL FROM n;
            INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
            SELECT session_id, run_id, expected_history_revision, input_id, 0 FROM input_decisions
            WHERE session_id = '{session:D}' AND input_id LIKE '{input}%' ORDER BY input_id;
            COMMIT;
            """);
        if (outputs) {
            database.Execute($"""
                BEGIN;
                INSERT INTO execution_requests (request_id, run_id, output_id, next_batch_index, output_final, outcome)
                SELECT '{request}' || substr(input_id, 25), run_id, '{output}' || substr(input_id, 25), 1, 1, 0
                FROM execution_runs WHERE session_id = '{session:D}' AND input_id LIKE '{input}%';
                INSERT INTO execution_output
                SELECT request_id, 0, CAST('output ' || CAST(substr(request_id, 25) AS INTEGER) AS BLOB), 1
                FROM execution_requests WHERE request_id LIKE '{request}%';
                COMMIT;
                """);
        }
    }
}
