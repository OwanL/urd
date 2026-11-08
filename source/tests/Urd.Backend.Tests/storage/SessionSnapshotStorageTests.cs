using System.Data.Common;
using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class SessionSnapshotStorageTests {
  [Fact]
  public void MixedLargeHistoriesSelectOnlyTheIndexedRecentWindow() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid other = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    SeedHistory(database, session, 0, 3000);
    database.Execute($"UPDATE sessions SET history_revision = 1 WHERE session_id = '{session:D}';");
    SeedHistory(database, session, 1, 3000);
    SeedHistory(database, other, 0, 3000);

    // Omitted bodies are invalid UTF-8; selecting all history before trimming would fail.
    database.Execute($"""
      UPDATE input_decisions SET original_text_utf8 = X'FF'
      WHERE session_id = '{session:D}' AND input_id IN (
          SELECT input_id FROM execution_runs WHERE session_id = '{session:D}' ORDER BY ordinal LIMIT 4000);
      UPDATE execution_output SET text_utf8 = X'FF' WHERE request_id IN (
          SELECT q.request_id FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
          WHERE r.session_id = '{session:D}' ORDER BY r.ordinal LIMIT 4000);
      """);

    SessionSnapshot snapshot = store.Snapshots.ReadStored(session);
    Assert.Equal(60, snapshot.Messages.Count);
    Assert.True(snapshot.HasOlderMessages);
    Assert.Equal("input 2971", snapshot.Messages[0].Preview);
    Assert.Equal("output 3000", snapshot.Messages[^1].Preview);
    Assert.All(snapshot.Messages, message => Assert.Equal(1, message.Message.HistoryRevision));
    Assert.NotNull(snapshot.LastSettlement);
    Assert.Equal(snapshot.Messages[^1].Message.ExecutionId, snapshot.LastSettlement.ExecutionId);

    string[] plan = Explain(database, SnapshotReader.SnapshotMessagesSql, session, 1);
    Assert.Contains(plan, step => step.Contains("SEARCH execution_messages USING INDEX execution_messages_history_order", StringComparison.Ordinal));
    Assert.DoesNotContain(plan, step => step.Contains("SCAN execution_messages", StringComparison.Ordinal));
    Assert.DoesNotContain(plan, step => step.Contains("execution_requests", StringComparison.Ordinal));
    string[] settlement = Explain(database, SnapshotReader.SnapshotSettlementSql, session, 1);
    Assert.Contains(settlement, step => step.Contains("USING INDEX execution_runs_terminal_order", StringComparison.Ordinal));
    Assert.DoesNotContain(settlement, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    string[] active = Explain(database, SnapshotReader.SnapshotUnsettledSql, session, 1);
    Assert.Contains(active, step => step.Contains("USING INDEX one_active_execution", StringComparison.Ordinal));
    Assert.DoesNotContain(active, step => step.Contains("SCAN", StringComparison.Ordinal));
  }

  private static string[] Explain(TemporaryDatabase database, string sql, Guid session, long revision) {
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
      DataSource = database.Path, Pooling = false
    }.ToString());
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "EXPLAIN QUERY PLAN " + sql;
    command.Parameters.AddWithValue("$session", session.ToString("D"));
    command.Parameters.AddWithValue("$revision", revision);
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Cast<DbDataRecord>().Select(row => row.GetString(3)).ToArray();
  }

  // Bulk fixture writes avoid the execution context builder, which deliberately reads full history.
  private static void SeedHistory(TemporaryDatabase database, Guid session, long revision, int count) {
    string submissionPrefix = Guid.NewGuid().ToString("D")[..24];
    string executionPrefix = Guid.NewGuid().ToString("D")[..24];
    string requestPrefix = Guid.NewGuid().ToString("D")[..24];
    string outputPrefix = Guid.NewGuid().ToString("D")[..24];
    database.Execute($"""
      BEGIN;
      WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x + 1 FROM n WHERE x < {count})
      INSERT INTO input_decisions
          (session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason)
      SELECT '{session:D}', '{submissionPrefix}' || printf('%012d', x), {revision}, CAST('input ' || x AS BLOB),
          1, '{executionPrefix}' || printf('%012d', x), NULL FROM n;
      INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, outcome)
      SELECT session_id, run_id, expected_history_revision, input_id, 0 FROM input_decisions
      WHERE session_id = '{session:D}' AND input_id LIKE '{submissionPrefix}%' ORDER BY input_id;
      INSERT INTO execution_requests (request_id, run_id, output_id, next_batch_index, output_final, outcome)
      SELECT '{requestPrefix}' || substr(input_id, 25), run_id, '{outputPrefix}' || substr(input_id, 25), 1, 1, 0
      FROM execution_runs WHERE session_id = '{session:D}' AND input_id LIKE '{submissionPrefix}%';
      INSERT INTO execution_output
      SELECT request_id, 0, CAST('output ' || CAST(substr(request_id, 25) AS INTEGER) AS BLOB), 1
      FROM execution_requests WHERE request_id LIKE '{requestPrefix}%';
      INSERT INTO execution_inputs (session_id, input_id, run_id, request_id, outcome)
      SELECT r.session_id, r.input_id, r.run_id, q.request_id, 0
      FROM execution_runs r LEFT JOIN execution_requests q ON q.run_id = r.run_id
      WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{submissionPrefix}%' ORDER BY r.ordinal;
      INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id, request_id)
      SELECT session_id, history_revision, run_id, role, message_id, input_id, request_id FROM (
          SELECT ordinal AS run_ordinal, session_id, history_revision, run_id,
              0 AS role, input_id AS message_id, input_id, NULL AS request_id
          FROM execution_runs WHERE session_id = '{session:D}' AND input_id LIKE '{submissionPrefix}%'
          UNION ALL
          SELECT r.ordinal, r.session_id, r.history_revision, r.run_id, 1, q.output_id, NULL, q.request_id
          FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
          WHERE r.session_id = '{session:D}' AND r.input_id LIKE '{submissionPrefix}%'
      ) ORDER BY run_ordinal, role;
      COMMIT;
      """);
  }
}
