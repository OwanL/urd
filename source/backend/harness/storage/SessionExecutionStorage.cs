using Microsoft.Data.Sqlite;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Creates execution requests and retains stop and settlement facts. The caller joins request cleanup first.
internal sealed class SessionExecutionStorage(Database database, ToolResultCodec toolResults,
    AuxiliaryConversationStorage auxiliaryConversations) {
  internal LlmRequest? CreateNextRequest(ExecutionIdentity execution) {
    return database.Write<LlmRequest?>(transaction => {
      Guid requestId = Guid.NewGuid();
      int insertedRequestCount = database.Execute(transaction, $"""
        INSERT INTO execution_requests (request_id, run_id, request_index, attempt)
        SELECT $request, r.run_id,
            COALESCE((SELECT MAX(request_index) + 1 FROM execution_requests WHERE run_id = r.run_id), 0), 1
        FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
        WHERE r.session_id = $session AND r.run_id = $run
            AND {ExecutionSql.CurrentRunCondition}
            AND NOT EXISTS (SELECT 1 FROM execution_requests q
                WHERE q.run_id = r.run_id AND (q.outcome IS NULL OR q.outcome <> {(int)ExecutionOutcome.Completed}))
            AND NOT EXISTS (SELECT 1 FROM execution_tools t WHERE t.run_id = r.run_id
                AND (t.background = 0 OR t.started = 0) AND (t.outcome IS NULL OR t.result_json IS NULL))
            AND (EXISTS (SELECT 1 FROM execution_tool_completions c
                    WHERE c.session_id = $session AND c.history_revision = $revision AND c.claimed_request_id IS NULL)
                OR EXISTS (SELECT 1 FROM execution_inputs i WHERE i.run_id = r.run_id
                    AND i.request_id IS NULL AND i.outcome IS NULL)
                OR EXISTS (SELECT 1 FROM execution_tools t JOIN execution_requests q ON q.request_id = t.request_id
                    WHERE q.run_id = r.run_id AND q.request_index =
                        (SELECT MAX(request_index) FROM execution_requests WHERE run_id = r.run_id))
                OR (r.kind = $continuation AND NOT EXISTS (
                    SELECT 1 FROM execution_requests first_request WHERE first_request.run_id = r.run_id)));
        """,
          ExecutionSql.Parameters(execution, ("$request", requestId), ("$continuation", (int)ExecutionRunKind.Continuation)));
      if (insertedRequestCount == 0)
        return null;

      PublishCompletions(transaction, execution.SessionId, execution.HistoryRevision, requestId);
      database.Execute(transaction, """
        UPDATE execution_tool_completions SET claimed_request_id = $request
        WHERE session_id = $session AND history_revision = $revision AND claimed_request_id IS NULL;
        UPDATE execution_inputs SET request_id = $request
        WHERE run_id = $run AND request_id IS NULL AND outcome IS NULL;
        """, ExecutionSql.Parameters(execution, ("$request", requestId)));
      var context = new LlmContextReader(database, toolResults).ReadContext(transaction,
          execution.SessionId, execution.HistoryRevision, execution.ExecutionId, requestId);
      return new LlmRequest(requestId, context.ReadMessages(), context);
    });
  }

  internal bool RemovePendingInput(ExecutionIdentity execution, Guid submissionId) => database.Write(transaction =>
    database.Execute(transaction, $"""
      UPDATE execution_inputs SET outcome = $cancelled, not_sent = 0
      WHERE session_id = $session AND run_id = $run AND input_id = $input
          AND request_id IS NULL AND outcome IS NULL
          AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
              WHERE r.session_id = $session AND r.run_id = $run AND {ExecutionSql.CurrentRunCondition}
                  AND (r.input_id IS NULL OR r.input_id <> $input));
      """, ExecutionSql.Parameters(execution, ("$input", submissionId),
        ("$cancelled", (int)ExecutionOutcome.Cancelled))) == 1);

  // A rejected provider request may be retried only before any output or call was observed.
  internal bool CanRetryOverflow(Guid sessionId, Guid runId, Guid requestId) => database.Read(transaction =>
    database.Scalar<bool>(transaction, $"""
      SELECT EXISTS (SELECT 1 FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
          WHERE q.request_id = $request AND q.run_id = $run AND r.session_id = $session
              AND q.outcome IS NULL AND q.next_chunk_index = 0 AND r.stopping = 0 AND r.outcome IS NULL
              AND NOT EXISTS (SELECT 1 FROM execution_messages m WHERE m.request_id = q.request_id
                  AND m.role IN ({(int)TranscriptEntryKind.Text}, {(int)TranscriptEntryKind.Tool}))
              AND NOT EXISTS (SELECT 1 FROM execution_tools t WHERE t.request_id = q.request_id));
      """, ("$session", sessionId), ("$run", runId), ("$request", requestId)));

  // The marker and its user entries share a transaction. A repeat hook never allocates
  // entries again, and the stopped/settled/revision checks also apply to repeats.
  internal bool StartRequest(ExecutionIdentity execution, Guid requestId, StartingContext? startingContext = null) {
    return database.Write(transaction => {
      int startedRequestCount = database.Execute(transaction, $"""
        UPDATE execution_requests SET attempt = 2,
            compaction_id = (SELECT compaction_id FROM session_compactions
                WHERE session_id = $session AND history_revision = $revision ORDER BY ordinal DESC LIMIT 1)
        WHERE request_id = $request AND run_id = $run AND outcome IS NULL AND attempt IN (1, 2)
            AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
                WHERE r.run_id = $run AND r.session_id = $session AND {ExecutionSql.CurrentRunCondition}
                    AND ($boundary IS NULL OR $boundary = COALESCE((SELECT c.compaction_id
                        FROM session_compactions c WHERE c.session_id = $session AND c.history_revision = $revision
                        ORDER BY c.ordinal DESC LIMIT 1), '00000000-0000-0000-0000-000000000000')));
        """, ExecutionSql.Parameters(execution, ("$request", requestId),
          ("$boundary", startingContext?.BoundaryId)));
      if (startedRequestCount == 0)
        return false;

      if (startingContext is not null)
        new StartingContextStorage(database).Activate(transaction, execution.SessionId, startingContext);

      database.Execute(transaction, $"""
        INSERT INTO execution_messages (
            session_id, history_revision, run_id, role, message_id, input_id, request_id, created_at, layout_weight)
        SELECT i.session_id, $revision, i.run_id, {(int)TranscriptEntryKind.User}, i.input_id, i.input_id, $request, $created,
            256 + length(d.original_text_utf8)
        FROM execution_inputs i JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
        WHERE i.run_id = $run AND i.request_id = $request
            AND NOT EXISTS (SELECT 1 FROM execution_messages m
                WHERE m.run_id = i.run_id AND m.role = {(int)TranscriptEntryKind.User} AND m.message_id = i.input_id)
        ORDER BY i.ordinal;
        """, ExecutionSql.Parameters(execution, ("$request", requestId), ("$created", StorageText.UtcTimestamp())));
      return true;
    });
  }

  internal StopReceipt RequestStop(Guid sessionId, Guid executionId, bool ownsExecution) {
    return database.Write(transaction => {
      StopReceipt? receipt = database.QuerySingleOrDefault(transaction,
          "SELECT outcome FROM execution_runs WHERE session_id = $session AND run_id = $run;",
          row => row.NullableInt("outcome") is { } outcome
              ? new StopReceipt(executionId, StopStatus.Settled, (ExecutionOutcome)outcome)
              : new StopReceipt(executionId, StopStatus.Requested),
          ("$session", sessionId), ("$run", executionId));
      if (receipt is null)
        return new StopReceipt(executionId, StopStatus.NotFound);

      if (receipt.Status == StopStatus.Settled)
        return receipt;

      if (!ownsExecution)
        return new StopReceipt(executionId, StopStatus.Unavailable);

      database.Execute(transaction, $"""
        UPDATE execution_runs SET stopping = 1
        WHERE session_id = $session AND run_id = $run AND stopping = 0 AND outcome IS NULL;
        UPDATE execution_inputs SET outcome = {(int)ExecutionOutcome.Cancelled},
            not_sent = CASE WHEN NOT EXISTS (SELECT 1 FROM execution_messages m
                WHERE m.run_id = execution_inputs.run_id AND m.role = {(int)TranscriptEntryKind.User}
                    AND m.input_id = execution_inputs.input_id) THEN 1 ELSE 0 END
        WHERE session_id = $session AND run_id = $run AND request_id IS NULL AND outcome IS NULL
            AND EXISTS (SELECT 1 FROM execution_runs WHERE run_id = $run AND stopping = 1 AND outcome IS NULL);
        """, ("$session", sessionId), ("$run", executionId));
      return receipt;
    });
  }

  // The caller has joined all owned work, including cancellation callbacks, before settlement.
  internal SessionExecutionResult? TryFinish(Guid sessionId, Guid executionId, ExecutionOutcome driverOutcome) {
    if (!Enum.IsDefined(driverOutcome))
      throw new ArgumentOutOfRangeException(nameof(driverOutcome));

    return database.Write<SessionExecutionResult?>(transaction => {
      RunFinishState? state = database.QuerySingleOrDefault(transaction, $"""
        SELECT r.outcome, r.stopping,
            (SELECT q.outcome FROM execution_requests q WHERE q.run_id = r.run_id
                ORDER BY q.request_index DESC LIMIT 1) AS last_outcome,
            (EXISTS (SELECT 1 FROM execution_inputs i WHERE i.run_id = r.run_id AND i.request_id IS NULL AND i.outcome IS NULL)
                OR EXISTS (SELECT 1 FROM execution_tool_completions c WHERE c.session_id = r.session_id
                    AND c.history_revision = r.history_revision AND c.claimed_request_id IS NULL)) AS pending_inputs,
            EXISTS (SELECT 1 FROM execution_tools t WHERE t.run_id = r.run_id
                AND (t.outcome IS NULL OR t.result_json IS NULL)) AS missing_results,
            EXISTS (SELECT 1 FROM auxiliary_conversations c WHERE c.run_id = r.run_id AND (
                EXISTS (SELECT 1 FROM auxiliary_requests q
                    WHERE q.conversation_id = c.conversation_id AND q.outcome IS NULL)
                OR EXISTS (SELECT 1 FROM auxiliary_tools t
                    WHERE t.conversation_id = c.conversation_id AND t.result_json IS NULL))) AS auxiliary_work_pending,
            EXISTS (SELECT 1 FROM execution_tools t JOIN execution_requests q ON q.request_id = t.request_id
                WHERE q.run_id = r.run_id AND q.request_index =
                    (SELECT MAX(request_index) FROM execution_requests WHERE run_id = r.run_id)) AS needs_tool_continuation,
            r.kind = $compaction AS is_compaction,
            COALESCE((SELECT substr(q.error, 1, 1024) FROM execution_requests q
                WHERE q.run_id = r.run_id AND q.outcome = {(int)ExecutionOutcome.Failed}
                ORDER BY q.request_index DESC LIMIT 1), substr(r.error, 1, 1024)) AS error,
            r.working_started_at
        FROM execution_runs r WHERE r.session_id = $session AND r.run_id = $run;
        """, row => new RunFinishState((ExecutionOutcome?)row.NullableInt("outcome"), row.Bool("stopping"),
          (ExecutionOutcome?)row.NullableInt("last_outcome"), row.Bool("pending_inputs"), row.Bool("missing_results"),
          row.Bool("needs_tool_continuation"), row.Bool("auxiliary_work_pending"), row.Bool("is_compaction"),
          row.NullableString("error"), row.NullableTimestamp("working_started_at")),
        ("$session", sessionId), ("$run", executionId), ("$compaction", (int)ExecutionRunKind.Compaction));
      if (ChooseFinalOutcome(state, driverOutcome) is not { } settlement)
        return null;

      if (settlement.AlreadySettled)
        return new(executionId, settlement.Outcome, settlement.Error);

      auxiliaryConversations.Retire(transaction, executionId, settlement.Outcome);
      TimeSpan elapsed = settlement.StartedAt is { } startedAt
          ? DateTimeOffset.UtcNow - startedAt : TimeSpan.Zero;
      bool unknownDuration = settlement.StartedAt is null || elapsed < TimeSpan.Zero;
      long elapsedMilliseconds = elapsed > TimeSpan.Zero ? (long)elapsed.TotalMilliseconds : 0L;
      int updatedSessionCount = database.Execute(transaction, """
        UPDATE sessions SET completed_working_ms = completed_working_ms + $elapsed,
            working_time_incomplete = CASE WHEN $unknown = 1 THEN 1 ELSE working_time_incomplete END
        WHERE session_id = $session AND EXISTS (SELECT 1 FROM execution_runs
            WHERE session_id = $session AND run_id = $run AND outcome IS NULL);
        """, ("$session", sessionId), ("$run", executionId), ("$elapsed", elapsedMilliseconds),
          ("$unknown", unknownDuration ? 1 : 0));
      if (updatedSessionCount != 1)
        throw new InvalidOperationException("The active execution working-time interval could not be settled.");

      database.Execute(transaction, $"""
        UPDATE execution_tool_completions SET claimed_request_id = NULL WHERE claimed_request_id IN (
            SELECT request_id FROM execution_requests WHERE run_id = $run AND attempt = 1);
        UPDATE execution_messages SET end_chunk_index = (
            SELECT next_chunk_index FROM execution_requests q WHERE q.request_id = execution_messages.request_id)
        WHERE run_id = $run AND role = {(int)TranscriptEntryKind.Text} AND end_chunk_index IS NULL;
        UPDATE execution_requests SET outcome = $outcome WHERE run_id = $run AND outcome IS NULL;
        UPDATE execution_inputs SET outcome = $outcome,
            not_sent = CASE WHEN (request_id IS NULL OR EXISTS (
                SELECT 1 FROM execution_requests q WHERE q.request_id = execution_inputs.request_id AND q.attempt = 1))
                AND NOT EXISTS (SELECT 1 FROM execution_messages m WHERE m.run_id = execution_inputs.run_id
                    AND m.role = {(int)TranscriptEntryKind.User} AND m.input_id = execution_inputs.input_id) THEN 1 ELSE 0 END
        WHERE run_id = $run AND outcome IS NULL;
        UPDATE execution_tools SET outcome = $toolOutcome, interrupted = 1,
            error = CASE WHEN started = 0 THEN 'Tool was not executed.' ELSE 'Tool result unavailable; effects may have occurred.' END,
            result_json = CASE WHEN started = 0 THEN $unstartedResult ELSE $unknownResult END,
            ended_at = CASE WHEN started = 1 THEN $ended ELSE NULL END
        WHERE run_id = $run AND result_json IS NULL;
        UPDATE execution_runs SET outcome = $outcome, working_started_at = NULL
        WHERE session_id = $session AND run_id = $run;
        """,
          ("$session", sessionId), ("$run", executionId), ("$outcome", (int)settlement.Outcome),
          ("$ended", StorageText.UtcTimestamp()),
          ("$unstartedResult", toolResults.Error("Tool was not executed.")),
          ("$unknownResult", toolResults.Error("Tool result unavailable; effects may have occurred.")),
          ("$toolOutcome", (int)(settlement.Outcome == ExecutionOutcome.Cancelled
              ? ExecutionOutcome.Cancelled : ExecutionOutcome.Failed)));
      QueueMissingCompletions(transaction, executionId);
      long revision = database.Scalar<long>(transaction,
          "SELECT history_revision FROM execution_runs WHERE run_id = $run;", ("$run", executionId));
      PublishCompletions(transaction, sessionId, revision);
      return new(executionId, settlement.Outcome, settlement.Error);
    });
  }

  // Host-only startup operation, under its exclusive data-directory lease. Opening a store
  // alone never changes history or restarts work. Completed facts remain untouched.
  internal void RecoverInterruptedExecutions() {
    database.Write(transaction => {
      var interrupted = database.Query(transaction,
          "SELECT run_id, session_id, history_revision FROM execution_runs WHERE outcome IS NULL;",
          row => (RunId: row.Guid("run_id"), SessionId: row.Guid("session_id"), Revision: row.Long("history_revision")));

      foreach (var run in interrupted) {
        database.Execute(transaction, $"""
          UPDATE execution_tool_completions SET claimed_request_id = NULL WHERE claimed_request_id IN (
              SELECT request_id FROM execution_requests WHERE run_id = $run AND attempt = 1);
          UPDATE execution_messages SET end_chunk_index = (
              SELECT next_chunk_index FROM execution_requests q WHERE q.request_id = execution_messages.request_id)
          WHERE run_id = $run AND role = {(int)TranscriptEntryKind.Text} AND end_chunk_index IS NULL;
          UPDATE execution_inputs SET outcome = $failed,
              not_sent = CASE WHEN request_id IS NULL OR EXISTS (SELECT 1 FROM execution_requests q
                  WHERE q.request_id = execution_inputs.request_id AND q.attempt = 1) THEN 1 ELSE 0 END
          WHERE run_id = $run AND outcome IS NULL;
          UPDATE execution_requests SET outcome = $failed, error = 'Interrupted by backend shutdown.'
          WHERE run_id = $run AND outcome IS NULL;
          UPDATE execution_tools SET outcome = $failed, interrupted = 1,
              error = CASE WHEN started = 0 THEN $unstarted ELSE $unknown END,
              result_json = CASE WHEN started = 0 THEN $unstartedResult ELSE $unknownResult END,
              ended_at = CASE WHEN started = 1 THEN $ended ELSE NULL END
          WHERE run_id = $run AND result_json IS NULL;
          UPDATE sessions SET working_time_incomplete = 1 WHERE session_id = $session;
          UPDATE execution_runs SET outcome = $failed, working_started_at = NULL WHERE run_id = $run;
          """, ("$run", run.RunId), ("$session", run.SessionId), ("$failed", (int)ExecutionOutcome.Failed),
            ("$unstarted", "Interrupted before tool execution; tool was not executed."),
            ("$unknown", "Tool execution interrupted; effects may have occurred. Do not automatically retry."),
            ("$unstartedResult", toolResults.Error("Interrupted before tool execution; tool was not executed.")),
            ("$unknownResult", toolResults.Error("Tool execution interrupted; effects may have occurred. Do not automatically retry.")),
            ("$ended", StorageText.UtcTimestamp()));
        auxiliaryConversations.Retire(transaction, run.RunId, ExecutionOutcome.Failed);
        QueueMissingCompletions(transaction, run.RunId);
        PublishCompletions(transaction, run.SessionId, run.Revision);
      }
      return true;
    });
  }

  // Synthetic interruption results need the same one-event guarantee as real completions.
  private void QueueMissingCompletions(SqliteTransaction transaction, Guid executionId) {
    var missing = database.Query(transaction, """
      SELECT t.request_id, t.call_id FROM execution_tools t JOIN execution_requests q ON q.request_id = t.request_id
      WHERE t.run_id = $run AND t.background = 1 AND t.started = 1 AND t.result_json IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM execution_tool_completions c
              WHERE c.request_id = t.request_id AND c.call_id = t.call_id)
      ORDER BY q.request_index, t.call_index;
      """, row => (RequestId: row.Guid("request_id"), CallId: row.String("call_id")), ("$run", executionId));
    foreach (var call in missing)
      database.Execute(transaction, """
        INSERT INTO execution_tool_completions (
            completion_id, session_id, history_revision, request_id, call_id, created_at)
        SELECT $completion, r.session_id, r.history_revision, t.request_id, t.call_id, t.ended_at
        FROM execution_tools t JOIN execution_runs r ON r.run_id = t.run_id
        WHERE t.request_id = $request AND t.call_id = $call;
        """, ("$completion", Guid.NewGuid()), ("$request", call.RequestId), ("$call", call.CallId));
  }

  // Never append behind a streaming text tail: publication and input delivery are separate.
  private void PublishCompletions(SqliteTransaction transaction, Guid sessionId, long revision, Guid? unstartedRequest = null) {
    database.Execute(transaction, $"""
      INSERT INTO execution_messages (
          session_id, history_revision, run_id, role, message_id, request_id, created_at, call_id, tool_name)
      SELECT c.session_id, c.history_revision, t.run_id, {(int)TranscriptEntryKind.ToolCompletion},
          c.completion_id, c.request_id, c.created_at, c.call_id, t.name
      FROM execution_tool_completions c JOIN execution_tools t ON t.request_id = c.request_id AND t.call_id = c.call_id
      WHERE c.session_id = $session AND c.history_revision = $revision AND c.published = 0
          AND NOT EXISTS (SELECT 1 FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
              WHERE r.session_id = $session AND r.history_revision = $revision AND q.outcome IS NULL
                  AND ($claim IS NULL OR q.request_id <> $claim OR q.attempt <> 1))
      ORDER BY c.ordinal;
      UPDATE execution_tool_completions SET published = 1
      WHERE session_id = $session AND history_revision = $revision AND published = 0
          AND EXISTS (SELECT 1 FROM execution_messages m
              WHERE m.role = {(int)TranscriptEntryKind.ToolCompletion} AND m.message_id = completion_id);
      """, ("$session", sessionId), ("$revision", revision), ("$claim", unstartedRequest));
  }

  internal SessionExecutionResult? ReadSettlement(Guid sessionId, Guid executionId) => database.RunLocked(() =>
    database.QuerySingleOrDefault<SessionExecutionResult?>(null, $"""
      SELECT r.outcome,
          COALESCE((SELECT substr(q.error, 1, 1024) FROM execution_requests q
              WHERE q.run_id = r.run_id AND q.outcome = {(int)ExecutionOutcome.Failed}
              ORDER BY q.request_index DESC LIMIT 1), substr(r.error, 1, 1024)) AS error
      FROM execution_runs r WHERE r.session_id = $session AND r.run_id = $run;
      """, row => {
        if (row.NullableInt("outcome") is not { } outcome)
          return null;
        return new(executionId, (ExecutionOutcome)outcome,
            (ExecutionOutcome)outcome == ExecutionOutcome.Failed ? row.NullableString("error") : null);
      }, ("$session", sessionId), ("$run", executionId)));

  private static (ExecutionOutcome Outcome, bool AlreadySettled, string? Error, DateTimeOffset? StartedAt)? ChooseFinalOutcome(
      RunFinishState? state, ExecutionOutcome driverOutcome) {
    if (state is null)
      return null;

    if (state.Outcome is { } settled)
      return (settled, AlreadySettled: true, settled == ExecutionOutcome.Failed ? state.Error : null, null);

    if (state.StopRequested)
      return (ExecutionOutcome.Cancelled, AlreadySettled: false, null, state.StartedAt);

    if (driverOutcome != ExecutionOutcome.Completed)
      return (driverOutcome, AlreadySettled: false,
          driverOutcome == ExecutionOutcome.Failed ? state.Error : null, state.StartedAt);

    if (state.IsCompaction)
      return state.AuxiliaryWorkPending ? null : (driverOutcome, AlreadySettled: false, null, state.StartedAt);

    if (state.LastOutcome is not { } outcome)
      return null;

    if (outcome == ExecutionOutcome.Completed && (state.PendingInputs || state.MissingResults
        || state.AuxiliaryWorkPending || state.NeedsToolContinuation))
      return null;

    return (outcome, AlreadySettled: false, outcome == ExecutionOutcome.Failed ? state.Error : null, state.StartedAt);
  }

  // Facts used to decide whether an execution can finish before changing stored state.
  private sealed record RunFinishState(ExecutionOutcome? Outcome, bool StopRequested, ExecutionOutcome? LastOutcome,
      bool PendingInputs, bool MissingResults, bool NeedsToolContinuation, bool AuxiliaryWorkPending,
      bool IsCompaction, string? Error, DateTimeOffset? StartedAt);
}
