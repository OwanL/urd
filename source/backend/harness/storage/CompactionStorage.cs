using Microsoft.Data.Sqlite;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Successful summaries and their transcript markers commit together; failures leave no boundary.
internal sealed class CompactionStorage(Database database, ToolResultCodec toolResults) {
  internal ModelHistory ReadContext(Guid sessionId, long revision, Guid? runId = null, Guid? requestId = null) =>
      database.Read(transaction => new LlmContextReader(database, toolResults)
          .ReadContext(transaction, sessionId, revision, runId, requestId));

  // Keep inputs until their completion is summarized, including results not yet claimed.
  // Completions precede every safe cut within their receiving request; only later requests retain them.
  // An unfinished recipient still needs its input, including an overflow retry. Freeze this snapshot
  // before summarizing so a racing completion cannot remove its input.
  internal IReadOnlyList<BackgroundToolCallInput> ReadBackgroundToolCalls(Guid sessionId, long revision,
      HistoryMessageLocation firstRetained) => database.Read(transaction => database.Query(transaction, """
        SELECT t.request_id, t.call_id, t.name, t.arguments_json
        FROM execution_tools t
        JOIN execution_requests q ON q.request_id = t.request_id
        JOIN execution_runs r ON r.run_id = t.run_id
        LEFT JOIN execution_tool_completions c ON c.request_id = t.request_id AND c.call_id = t.call_id
        LEFT JOIN execution_requests recipient ON recipient.request_id = c.claimed_request_id
        LEFT JOIN execution_runs recipient_run ON recipient_run.run_id = recipient.run_id
        WHERE r.session_id = $session AND r.history_revision = $revision
            AND t.background = 1 AND t.started = 1
            AND (c.claimed_request_id IS NULL OR recipient.outcome IS NULL OR recipient.attempt = 1
                OR recipient_run.ordinal > $cutRun
                OR (recipient_run.ordinal = $cutRun AND recipient.request_index > $cutRequest))
        ORDER BY r.ordinal, q.request_index, t.call_index;
        """, row => new BackgroundToolCallInput(row.Guid("request_id"), row.String("call_id"),
          row.String("name"), row.String("arguments_json")),
          ("$session", sessionId), ("$revision", revision),
          ("$cutRun", firstRetained.ExecutionOrdinal), ("$cutRequest", firstRetained.RequestIndexInExecution)));

  internal static SavedHistoryCompaction? ReadLatest(Database database, SqliteTransaction transaction, Guid sessionId, long revision) =>
      database.QuerySingleOrDefault(transaction, """
        SELECT compaction_id, first_kept_request_id, first_kept_run_ordinal, first_kept_request_index,
            first_kept_message_index, summary
        FROM session_compactions WHERE session_id = $session AND history_revision = $revision
        ORDER BY ordinal DESC LIMIT 1;
        """, row => new SavedHistoryCompaction(row.Guid("compaction_id"),
          new HistoryMessageLocation(row.Guid("first_kept_request_id"), row.Long("first_kept_run_ordinal"),
              row.Int("first_kept_request_index"), row.Int("first_kept_message_index")), row.String("summary")),
          ("$session", sessionId), ("$revision", revision));

  internal CompactionSummary? Read(Guid sessionId, long revision, Guid id) => database.Read(transaction =>
      database.QuerySingleOrDefault(transaction, """
        SELECT c.summary, c.reason, c.tokens_before, c.tokens_after, c.created_at
        FROM session_compactions c JOIN sessions s ON s.session_id = c.session_id
        WHERE c.session_id = $session AND c.history_revision = $revision
            AND s.history_revision = $revision AND c.compaction_id = $id;
        """, row => new CompactionSummary(id, row.String("summary"), (CompactionReason)row.Int("reason"),
          row.Long("tokens_before"), row.Long("tokens_after"), row.Timestamp("created_at")),
          ("$session", sessionId), ("$revision", revision), ("$id", id)));

  // False means the boundary moved or the run can no longer record compaction.
  internal bool Save(Guid sessionId, Guid runId, long revision, HistoryCompactionPlan plan, GeneratedCompactionSummary result) {
    if (string.IsNullOrWhiteSpace(result.Summary) || result.Summary.Length > HistoryCompactor.MaxSummaryCharacters
        || result.EstimatedContextTokensAfter < 0 || result.EstimatedContextTokensAfter >= plan.EstimatedContextTokensBefore)
      throw new ArgumentException("Compaction must provide a bounded summary that reduces context.", nameof(result));
    return database.Write(transaction => {
      if (ReadLatest(database, transaction, sessionId, revision)?.Id != plan.History.PreviousCompaction?.Id)
        return false;
      HistoryMessageLocation cut = plan.FirstRetainedMessage.Location;
      if (!database.Scalar<bool>(transaction, $"""
        SELECT EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
            WHERE r.run_id = $run AND r.session_id = $session AND {ExecutionSql.CurrentRunCondition})
            AND EXISTS (SELECT 1 FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
                WHERE q.request_id = $keep AND r.session_id = $session AND r.history_revision = $revision
                    AND r.ordinal = $ordinal AND q.request_index = $index);
        """, ("$session", sessionId), ("$run", runId), ("$revision", revision),
          ("$keep", cut.RequestId), ("$ordinal", cut.ExecutionOrdinal), ("$index", cut.RequestIndexInExecution)))
        return false;
      Guid id = Guid.NewGuid();
      database.Execute(transaction, $"""
        INSERT INTO session_compactions (compaction_id, session_id, history_revision, run_id,
            first_kept_request_id, first_kept_message_index, first_kept_run_ordinal, first_kept_request_index,
            reason, tokens_before, tokens_after, created_at, summary)
        VALUES ($id, $session, $revision, $run, $keep, $message, $ordinal, $index,
            $reason, $before, $after, $created, $summary);
        INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, created_at)
        VALUES ($session, $revision, $run, {(int)TranscriptEntryKind.Compaction}, $id, $created);
        """, ("$id", id), ("$session", sessionId), ("$revision", revision), ("$run", runId),
          ("$keep", cut.RequestId), ("$message", cut.MessageIndexInRequest), ("$ordinal", cut.ExecutionOrdinal),
          ("$index", cut.RequestIndexInExecution), ("$reason", (int)plan.Reason), ("$before", plan.EstimatedContextTokensBefore),
          ("$after", result.EstimatedContextTokensAfter), ("$created", StorageText.UtcTimestamp()),
          ("$summary", plan.AppendBackgroundToolCalls(result.Summary)));
      return true;
    });
  }

  // Null means busy/fenced. A retained operation is never started a second time.
  internal (CompactionReceipt Receipt, bool New)? Start(Guid sessionId, ManualCompactionRequest submission, bool accepting) {
    return database.Write<(CompactionReceipt, bool)?>(transaction => {
      CompactionReceipt? retained = database.QuerySingleOrDefault(transaction, """
        SELECT session_id, history_revision, kind, llm_selection_json, compaction_focus, outcome, error
        FROM execution_runs WHERE run_id = $run;
        """, row => {
          if (row.Guid("session_id") != sessionId || row.Long("history_revision") != submission.ExpectedHistoryRevision
              || row.Int("kind") != (int)ExecutionRunKind.Compaction || row.NullableString("llm_selection_json") is not { } selection
              || !LlmSelectionJson.SameOperation(LlmSelectionJson.Read(selection), submission.LlmSelection)
              || row.NullableString("compaction_focus") != submission.Focus)
            throw new UserMessageConflictException(submission.OperationId);
          return new CompactionReceipt(submission.OperationId,
              (ExecutionOutcome?)row.NullableInt("outcome"), row.NullableString("error"));
        }, ("$run", submission.OperationId));
      if (retained is not null)
        return (retained, false);
      int insertedCompactionRunCount = database.Execute(transaction, """
        INSERT INTO execution_runs (session_id, run_id, history_revision, kind, llm_selection_json,
            compaction_focus, working_started_at)
        SELECT s.session_id, $run, s.history_revision, $kind, $selection, $focus, $started FROM sessions s
        WHERE s.session_id = $session AND s.history_revision = $revision AND $accepting = 1
            AND NOT EXISTS (SELECT 1 FROM execution_runs r WHERE r.session_id = $session AND r.outcome IS NULL);
        """, ("$session", sessionId), ("$run", submission.OperationId), ("$revision", submission.ExpectedHistoryRevision),
          ("$kind", (int)ExecutionRunKind.Compaction),
          ("$selection", LlmSelectionJson.Write(submission.LlmSelection)), ("$focus", submission.Focus),
          ("$started", StorageText.UtcTimestamp()), ("$accepting", accepting ? 1 : 0));
      return insertedCompactionRunCount == 0 ? null : (new CompactionReceipt(submission.OperationId, null), true);
    });
  }

  internal CompactionReceipt? ReadReceipt(Guid sessionId, Guid operationId) => database.Read(transaction =>
      database.QuerySingleOrDefault(transaction, """
        SELECT outcome, error FROM execution_runs
        WHERE run_id = $run AND session_id = $session AND kind = $kind;
        """, row => new CompactionReceipt(operationId, (ExecutionOutcome?)row.NullableInt("outcome"), row.NullableString("error")),
          ("$run", operationId), ("$session", sessionId), ("$kind", (int)ExecutionRunKind.Compaction)));

  internal void RecordError(Guid sessionId, Guid runId, string? error) => database.Write(transaction =>
      database.Execute(transaction, """
        UPDATE execution_runs SET error = $error WHERE session_id = $session AND run_id = $run AND outcome IS NULL;
        """, ("$session", sessionId), ("$run", runId), ("$error", error)));
}
