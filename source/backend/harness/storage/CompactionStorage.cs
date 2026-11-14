using System.Globalization;
using Microsoft.Data.Sqlite;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Successful summaries and their transcript markers commit together; failures leave no boundary.
internal sealed class CompactionStorage(Database database, ToolResultCodec toolResults) {
  internal CompactionContext ReadContext(Guid sessionId, long revision, Guid? runId = null, Guid? requestId = null) =>
      database.Read(transaction => new LlmContextReader(database, toolResults)
          .ReadContext(transaction, sessionId, revision, runId, requestId));

  internal static CompactionBoundary? ReadLatest(Database database, SqliteTransaction transaction, Guid sessionId, long revision) {
    using SqliteCommand read = database.Command(transaction, """
      SELECT compaction_id, first_kept_request_id, first_kept_run_ordinal, first_kept_request_index,
          first_kept_message_index, summary
      FROM session_compactions WHERE session_id = $session AND history_revision = $revision
      ORDER BY ordinal DESC LIMIT 1;
      """, ("$session", sessionId), ("$revision", revision));
    using SqliteDataReader reader = read.ExecuteReader();
    if (!reader.Read())
      return null;
    var firstKept = new ContextPosition(reader.GetGuid(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4));
    return new(reader.GetGuid(0), firstKept, reader.GetString(5));
  }

  internal CompactionSummary? Read(Guid sessionId, long revision, Guid id) => database.Read(transaction => {
    using SqliteCommand read = database.Command(transaction, """
      SELECT c.summary, c.reason, c.tokens_before, c.tokens_after, c.created_at
      FROM session_compactions c JOIN sessions s ON s.session_id = c.session_id
      WHERE c.session_id = $session AND c.history_revision = $revision
          AND s.history_revision = $revision AND c.compaction_id = $id;
      """, ("$session", sessionId), ("$revision", revision), ("$id", id));
    using SqliteDataReader reader = read.ExecuteReader();
    if (!reader.Read())
      return null;
    return new CompactionSummary(id, reader.GetString(0), (CompactionReason)reader.GetInt32(1),
        reader.GetInt64(2), reader.GetInt64(3),
        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
  });

  // False means the boundary moved or the run can no longer record compaction.
  internal bool Save(Guid sessionId, Guid runId, long revision, CompactionPlan plan, CompactionResult result) {
    if (string.IsNullOrWhiteSpace(result.Summary) || result.Summary.Length > 32_768
        || result.TokensAfter < 0 || result.TokensAfter >= plan.TokensBefore)
      throw new ArgumentException("Compaction must provide a bounded summary that reduces context.", nameof(result));
    return database.Write(transaction => {
      if (ReadLatest(database, transaction, sessionId, revision)?.Id != plan.Context.Previous?.Id)
        return false;
      ContextPosition cut = plan.FirstKept.Position;
      using SqliteCommand allowed = database.Command(transaction, """
        SELECT EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
            WHERE r.run_id = $run AND r.session_id = $session AND r.history_revision = $revision
                AND s.history_revision = $revision AND r.stopping = 0 AND r.outcome IS NULL)
            AND EXISTS (SELECT 1 FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
                WHERE q.request_id = $keep AND r.session_id = $session AND r.history_revision = $revision
                    AND r.ordinal = $ordinal AND q.request_index = $index);
        """, ("$session", sessionId), ("$run", runId), ("$revision", revision),
          ("$keep", cut.RequestId), ("$ordinal", cut.RunOrdinal), ("$index", cut.RequestIndex));
      if ((long)allowed.ExecuteScalar()! == 0)
        return false;
      Guid id = Guid.NewGuid();
      using SqliteCommand insert = database.Command(transaction, """
        INSERT INTO session_compactions (compaction_id, session_id, history_revision, run_id,
            first_kept_request_id, first_kept_message_index, first_kept_run_ordinal, first_kept_request_index,
            reason, tokens_before, tokens_after, created_at, summary)
        VALUES ($id, $session, $revision, $run, $keep, $message, $ordinal, $index,
            $reason, $before, $after, $created, $summary);
        INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, created_at)
        VALUES ($session, $revision, $run, 4, $id, $created);
        """, ("$id", id), ("$session", sessionId), ("$revision", revision), ("$run", runId),
          ("$keep", cut.RequestId), ("$message", cut.MessageIndex), ("$ordinal", cut.RunOrdinal),
          ("$index", cut.RequestIndex), ("$reason", (int)plan.Reason), ("$before", plan.TokensBefore),
          ("$after", result.TokensAfter), ("$created", StorageText.UtcTimestamp()), ("$summary", result.Summary));
      insert.ExecuteNonQuery();
      return true;
    });
  }

  // Null means busy/fenced. A retained operation is never started a second time.
  internal (CompactionReceipt Receipt, bool New)? Admit(Guid sessionId, CompactionSubmission submission, bool accepting) {
    return database.Write<(CompactionReceipt, bool)?>(transaction => {
      using (SqliteCommand retained = database.Command(transaction, """
        SELECT session_id, history_revision, kind, llm_selection_json, compaction_focus, outcome, error
        FROM execution_runs WHERE run_id = $run;
        """, ("$run", submission.OperationId)))
      using (SqliteDataReader reader = retained.ExecuteReader()) {
        if (reader.Read()) {
          if (reader.GetGuid(0) != sessionId || reader.GetInt64(1) != submission.ExpectedHistoryRevision
              || reader.GetInt32(2) != (int)ExecutionRunKind.Compaction || reader.IsDBNull(3)
              || !LlmSelectionJson.SameOperation(LlmSelectionJson.Read(reader.GetString(3)), submission.LlmSelection)
              || (reader.IsDBNull(4) ? null : reader.GetString(4)) != submission.Focus)
            throw new UserMessageConflictException(submission.OperationId);
          return (new CompactionReceipt(submission.OperationId,
              reader.IsDBNull(5) ? null : (ExecutionOutcome)reader.GetInt32(5),
              reader.IsDBNull(6) ? null : reader.GetString(6)), false);
        }
      }
      using SqliteCommand insert = database.Command(transaction, """
        INSERT INTO execution_runs (session_id, run_id, history_revision, kind, llm_selection_json,
            compaction_focus, working_started_at)
        SELECT s.session_id, $run, s.history_revision, $kind, $selection, $focus, $started FROM sessions s
        WHERE s.session_id = $session AND s.history_revision = $revision AND $accepting = 1
            AND NOT EXISTS (SELECT 1 FROM execution_runs r WHERE r.session_id = $session AND r.outcome IS NULL);
        """, ("$session", sessionId), ("$run", submission.OperationId), ("$revision", submission.ExpectedHistoryRevision),
          ("$kind", (int)ExecutionRunKind.Compaction),
          ("$selection", LlmSelectionJson.Write(submission.LlmSelection)), ("$focus", submission.Focus),
          ("$started", StorageText.UtcTimestamp()), ("$accepting", accepting ? 1 : 0));
      return insert.ExecuteNonQuery() == 0 ? null : (new CompactionReceipt(submission.OperationId, null), true);
    });
  }

  internal CompactionReceipt? ReadReceipt(Guid sessionId, Guid operationId) => database.Read(transaction => {
    using SqliteCommand read = database.Command(transaction, """
      SELECT outcome, error FROM execution_runs
      WHERE run_id = $run AND session_id = $session AND kind = $kind;
      """, ("$run", operationId), ("$session", sessionId),
        ("$kind", (int)ExecutionRunKind.Compaction));
    using SqliteDataReader reader = read.ExecuteReader();
    if (!reader.Read())
      return null;
    return new CompactionReceipt(operationId, reader.IsDBNull(0) ? null : (ExecutionOutcome)reader.GetInt32(0),
        reader.IsDBNull(1) ? null : reader.GetString(1));
  });

  internal void RecordError(Guid sessionId, Guid runId, string? error) => database.Write(transaction => {
    using SqliteCommand write = database.Command(transaction, """
      UPDATE execution_runs SET error = $error WHERE session_id = $session AND run_id = $run AND outcome IS NULL;
      """, ("$session", sessionId), ("$run", runId), ("$error", error));
    return write.ExecuteNonQuery();
  });
}
