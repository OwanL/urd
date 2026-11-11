using Microsoft.Data.Sqlite;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Claims execution work and retains stop and settlement facts. The caller joins request cleanup first.
internal sealed class SessionExecutionStorage(Database database, ToolResultCodec toolResults) {
  internal LlmRequest? ClaimNextRequest(Guid sessionId, Guid executionId, long historyRevision) {
    return database.Write<LlmRequest?>(transaction => {
      Guid requestId = Guid.NewGuid();
      using SqliteCommand claim = database.Command(transaction, """
        INSERT INTO execution_requests (request_id, run_id, request_index, attempt)
        SELECT $request, r.run_id,
            COALESCE((SELECT MAX(request_index) + 1 FROM execution_requests WHERE run_id = r.run_id), 0), 1
        FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
        WHERE r.session_id = $session AND r.run_id = $run
            AND r.history_revision = $revision AND s.history_revision = $revision
            AND r.stopping = 0 AND r.outcome IS NULL
            AND NOT EXISTS (SELECT 1 FROM execution_requests q
                WHERE q.run_id = r.run_id AND (q.outcome IS NULL OR q.outcome <> 0))
            AND NOT EXISTS (SELECT 1 FROM execution_tools t WHERE t.run_id = r.run_id
                AND (t.background = 0 OR t.started = 0) AND (t.outcome IS NULL OR t.result_json IS NULL))
            AND (EXISTS (SELECT 1 FROM execution_tool_completions c
                    WHERE c.session_id = $session AND c.history_revision = $revision AND c.claimed_request_id IS NULL)
                OR EXISTS (SELECT 1 FROM execution_inputs i WHERE i.run_id = r.run_id
                    AND i.request_id IS NULL AND i.outcome IS NULL)
                OR EXISTS (SELECT 1 FROM execution_tools t JOIN execution_requests q ON q.request_id = t.request_id
                    WHERE q.run_id = r.run_id AND q.request_index =
                        (SELECT MAX(request_index) FROM execution_requests WHERE run_id = r.run_id)));
        """,
          ("$session", sessionId), ("$run", executionId), ("$revision", historyRevision), ("$request", requestId));
      if (claim.ExecuteNonQuery() == 0)
        return null;

      PublishCompletions(transaction, sessionId, historyRevision, requestId);
      using SqliteCommand assign = database.Command(transaction, """
        UPDATE execution_tool_completions SET claimed_request_id = $request
        WHERE session_id = $session AND history_revision = $revision AND claimed_request_id IS NULL;
        UPDATE execution_inputs SET request_id = $request
        WHERE run_id = $run AND request_id IS NULL AND outcome IS NULL;
        """, ("$session", sessionId), ("$revision", historyRevision), ("$run", executionId), ("$request", requestId));
      assign.ExecuteNonQuery();
      var context = new LlmContextReader(database, toolResults).ReadContext(transaction, sessionId, historyRevision, executionId, requestId);
      return new LlmRequest(requestId, context.ReadMessages(), context);
    });
  }

  // A rejected provider request may be retried only before any output or call was observed.
  internal bool CanRetryOverflow(Guid sessionId, Guid runId, Guid requestId) => database.Read(transaction => {
    using SqliteCommand read = database.Command(transaction, """
      SELECT EXISTS (SELECT 1 FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
          WHERE q.request_id = $request AND q.run_id = $run AND r.session_id = $session
              AND q.outcome IS NULL AND q.next_chunk_index = 0 AND r.stopping = 0 AND r.outcome IS NULL
              AND NOT EXISTS (SELECT 1 FROM execution_messages m WHERE m.request_id = q.request_id AND m.role IN (1, 2))
              AND NOT EXISTS (SELECT 1 FROM execution_tools t WHERE t.request_id = q.request_id));
      """, ("$session", sessionId), ("$run", runId), ("$request", requestId));
    return (long)read.ExecuteScalar()! == 1;
  });

  // The marker and its user entries share a transaction. A repeat hook never allocates
  // entries again, and the stopped/settled/revision checks also apply to repeats.
  internal bool StartRequest(Guid sessionId, Guid executionId, long historyRevision, Guid requestId,
      StartingContext? startingContext = null) {
    return database.Write(transaction => {
      using SqliteCommand mark = database.Command(transaction, """
        UPDATE execution_requests SET attempt = 2,
            compaction_id = (SELECT compaction_id FROM session_compactions
                WHERE session_id = $session AND history_revision = $revision ORDER BY ordinal DESC LIMIT 1)
        WHERE request_id = $request AND run_id = $run AND outcome IS NULL AND attempt IN (1, 2)
            AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
                WHERE r.run_id = $run AND r.session_id = $session AND r.history_revision = $revision
                    AND s.history_revision = $revision AND r.stopping = 0 AND r.outcome IS NULL);
        """, ("$session", sessionId), ("$run", executionId), ("$revision", historyRevision), ("$request", requestId));
      if (mark.ExecuteNonQuery() == 0)
        return false;

      if (startingContext is not null)
        new StartingContextStorage(database).Save(transaction, sessionId, startingContext);

      using SqliteCommand place = database.Command(transaction, """
        INSERT INTO execution_messages (
            session_id, history_revision, run_id, role, message_id, input_id, request_id, created_at, layout_weight)
        SELECT i.session_id, $revision, i.run_id, 0, i.input_id, i.input_id, $request, $created,
            256 + length(d.original_text_utf8)
        FROM execution_inputs i JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
        WHERE i.run_id = $run AND i.request_id = $request
            AND NOT EXISTS (SELECT 1 FROM execution_messages m
                WHERE m.run_id = i.run_id AND m.role = 0 AND m.message_id = i.input_id)
        ORDER BY i.ordinal;
        """, ("$run", executionId), ("$revision", historyRevision), ("$request", requestId),
          ("$created", StorageText.UtcTimestamp()));
      place.ExecuteNonQuery();
      return true;
    });
  }

  internal StopReceipt RequestStop(Guid sessionId, Guid executionId, bool ownsExecution) {
    return database.Write(transaction => {
      using SqliteCommand read = database.Command(transaction,
          "SELECT outcome FROM execution_runs WHERE session_id = $session AND run_id = $run;",
          ("$session", sessionId), ("$run", executionId));
      object? outcome = read.ExecuteScalar();
      if (outcome is null)
        return new StopReceipt(executionId, StopStatus.NotFound);

      if (outcome is not DBNull)
        return new StopReceipt(executionId, StopStatus.Settled, (ExecutionOutcome)(long)outcome);

      if (!ownsExecution)
        return new StopReceipt(executionId, StopStatus.Unavailable);

      using SqliteCommand command = database.Command(transaction, """
        UPDATE execution_runs SET stopping = 1
        WHERE session_id = $session AND run_id = $run AND stopping = 0 AND outcome IS NULL;
        UPDATE execution_inputs SET outcome = 1,
            not_sent = CASE WHEN NOT EXISTS (SELECT 1 FROM execution_messages m
                WHERE m.run_id = execution_inputs.run_id AND m.role = 0
                    AND m.input_id = execution_inputs.input_id) THEN 1 ELSE 0 END
        WHERE session_id = $session AND run_id = $run AND request_id IS NULL AND outcome IS NULL
            AND EXISTS (SELECT 1 FROM execution_runs WHERE run_id = $run AND stopping = 1 AND outcome IS NULL);
        """, ("$session", sessionId), ("$run", executionId));
      command.ExecuteNonQuery();
      return new StopReceipt(executionId, StopStatus.Requested);
    });
  }

  // The caller has joined all owned work, including cancellation callbacks, before settlement.
  internal SessionExecutionResult? TrySettle(Guid sessionId, Guid executionId, ExecutionOutcome driverOutcome) {
    if (!Enum.IsDefined(driverOutcome))
      throw new ArgumentOutOfRangeException(nameof(driverOutcome));

    return database.Write<SessionExecutionResult?>(transaction => {
      using SqliteCommand read = database.Command(transaction, """
        SELECT r.outcome, r.stopping,
            (SELECT q.outcome FROM execution_requests q WHERE q.run_id = r.run_id ORDER BY q.request_index DESC LIMIT 1),
            (EXISTS (SELECT 1 FROM execution_inputs i WHERE i.run_id = r.run_id AND i.request_id IS NULL AND i.outcome IS NULL)
                OR EXISTS (SELECT 1 FROM execution_tool_completions c WHERE c.session_id = r.session_id
                    AND c.history_revision = r.history_revision AND c.claimed_request_id IS NULL)),
            EXISTS (SELECT 1 FROM execution_tools t WHERE t.run_id = r.run_id AND (t.outcome IS NULL OR t.result_json IS NULL)),
            EXISTS (SELECT 1 FROM execution_tools t JOIN execution_requests q ON q.request_id = t.request_id
                WHERE q.run_id = r.run_id AND q.request_index =
                    (SELECT MAX(request_index) FROM execution_requests WHERE run_id = r.run_id)),
            r.compaction_selection_json IS NOT NULL,
            (SELECT substr(q.error, 1, 1024) FROM execution_requests q
                WHERE q.run_id = r.run_id AND q.outcome = 2 ORDER BY q.request_index DESC LIMIT 1),
            substr(r.error, 1, 1024)
        FROM execution_runs r WHERE r.session_id = $session AND r.run_id = $run;
        """, ("$session", sessionId), ("$run", executionId));
      if (DecideSettlement(read, driverOutcome) is not { } settlement)
        return null;

      if (settlement.AlreadySettled)
        return new(executionId, settlement.Outcome, settlement.Error);

      using SqliteCommand settle = database.Command(transaction, """
        UPDATE execution_tool_completions SET claimed_request_id = NULL WHERE claimed_request_id IN (
            SELECT request_id FROM execution_requests WHERE run_id = $run AND attempt = 1);
        UPDATE execution_messages SET end_chunk_index = (
            SELECT next_chunk_index FROM execution_requests q WHERE q.request_id = execution_messages.request_id)
        WHERE run_id = $run AND role = 1 AND end_chunk_index IS NULL;
        UPDATE execution_requests SET outcome = $outcome WHERE run_id = $run AND outcome IS NULL;
        UPDATE execution_inputs SET outcome = $outcome,
            not_sent = CASE WHEN (request_id IS NULL OR EXISTS (
                SELECT 1 FROM execution_requests q WHERE q.request_id = execution_inputs.request_id AND q.attempt = 1))
                AND NOT EXISTS (SELECT 1 FROM execution_messages m WHERE m.run_id = execution_inputs.run_id
                    AND m.role = 0 AND m.input_id = execution_inputs.input_id) THEN 1 ELSE 0 END
        WHERE run_id = $run AND outcome IS NULL;
        UPDATE execution_tools SET outcome = $toolOutcome,
            error = CASE WHEN started = 0 THEN 'Tool was not executed.' ELSE 'Tool result unavailable; effects may have occurred.' END,
            result_json = CASE WHEN started = 0 THEN $unstartedResult ELSE $unknownResult END,
            ended_at = CASE WHEN started = 1 THEN $ended ELSE NULL END
        WHERE run_id = $run AND result_json IS NULL;
        UPDATE execution_runs SET outcome = $outcome WHERE session_id = $session AND run_id = $run;
        """,
          ("$session", sessionId), ("$run", executionId), ("$outcome", (int)settlement.Outcome),
          ("$ended", StorageText.UtcTimestamp()),
          ("$unstartedResult", toolResults.Error("Tool was not executed.")),
          ("$unknownResult", toolResults.Error("Tool result unavailable; effects may have occurred.")),
          ("$toolOutcome", (int)(settlement.Outcome == ExecutionOutcome.Cancelled
              ? ExecutionOutcome.Cancelled : ExecutionOutcome.Failed)));
      settle.ExecuteNonQuery();
      QueueMissingCompletions(transaction, executionId);
      using SqliteCommand revision = database.Command(transaction,
          "SELECT history_revision FROM execution_runs WHERE run_id = $run;", ("$run", executionId));
      PublishCompletions(transaction, sessionId, (long)revision.ExecuteScalar()!);
      return new(executionId, settlement.Outcome, settlement.Error);
    });
  }

  // Host-only startup operation, under its exclusive data-directory lease. Opening a store
  // alone never changes history or restarts work. Completed facts remain untouched.
  internal void RecoverInterruptedExecutions() {
    database.Write(transaction => {
      var interrupted = new List<(Guid RunId, Guid SessionId, long Revision)>();
      using (SqliteCommand read = database.Command(transaction,
          "SELECT run_id, session_id, history_revision FROM execution_runs WHERE outcome IS NULL;"))
      using (SqliteDataReader reader = read.ExecuteReader()) {
        while (reader.Read())
          interrupted.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2)));
      }

      foreach (var run in interrupted) {
        using SqliteCommand recover = database.Command(transaction, """
          UPDATE execution_tool_completions SET claimed_request_id = NULL WHERE claimed_request_id IN (
              SELECT request_id FROM execution_requests WHERE run_id = $run AND attempt = 1);
          UPDATE execution_messages SET end_chunk_index = (
              SELECT next_chunk_index FROM execution_requests q WHERE q.request_id = execution_messages.request_id)
          WHERE run_id = $run AND role = 1 AND end_chunk_index IS NULL;
          UPDATE execution_inputs SET outcome = $failed,
              not_sent = CASE WHEN request_id IS NULL OR EXISTS (SELECT 1 FROM execution_requests q
                  WHERE q.request_id = execution_inputs.request_id AND q.attempt = 1) THEN 1 ELSE 0 END
          WHERE run_id = $run AND outcome IS NULL;
          UPDATE execution_requests SET outcome = $failed, error = 'Interrupted by backend shutdown.'
          WHERE run_id = $run AND outcome IS NULL;
          UPDATE execution_tools SET outcome = $failed,
              error = CASE WHEN started = 0 THEN $unstarted ELSE $unknown END,
              result_json = CASE WHEN started = 0 THEN $unstartedResult ELSE $unknownResult END,
              ended_at = CASE WHEN started = 1 THEN $ended ELSE NULL END
          WHERE run_id = $run AND result_json IS NULL;
          UPDATE execution_runs SET outcome = $failed WHERE run_id = $run;
          """, ("$run", run.RunId), ("$failed", (int)ExecutionOutcome.Failed),
            ("$unstarted", "Interrupted before tool execution; tool was not executed."),
            ("$unknown", "Tool execution interrupted; effects may have occurred. Do not automatically retry."),
            ("$unstartedResult", toolResults.Error("Interrupted before tool execution; tool was not executed.")),
            ("$unknownResult", toolResults.Error("Tool execution interrupted; effects may have occurred. Do not automatically retry.")),
            ("$ended", StorageText.UtcTimestamp()));
        recover.ExecuteNonQuery();
        QueueMissingCompletions(transaction, run.RunId);
        PublishCompletions(transaction, run.SessionId, run.Revision);
      }
      return true;
    });
  }

  // Synthetic interruption results need the same one-event guarantee as real completions.
  private void QueueMissingCompletions(SqliteTransaction transaction, Guid executionId) {
    var missing = new List<(Guid RequestId, string CallId)>();
    using (SqliteCommand read = database.Command(transaction, """
      SELECT t.request_id, t.call_id FROM execution_tools t JOIN execution_requests q ON q.request_id = t.request_id
      WHERE t.run_id = $run AND t.background = 1 AND t.started = 1 AND t.result_json IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM execution_tool_completions c
              WHERE c.request_id = t.request_id AND c.call_id = t.call_id)
      ORDER BY q.request_index, t.call_index;
      """, ("$run", executionId)))
    using (SqliteDataReader reader = read.ExecuteReader()) {
      while (reader.Read())
        missing.Add((reader.GetGuid(0), reader.GetString(1)));
    }
    foreach (var call in missing) {
      using SqliteCommand insert = database.Command(transaction, """
        INSERT INTO execution_tool_completions (
            completion_id, session_id, history_revision, request_id, call_id, created_at)
        SELECT $completion, r.session_id, r.history_revision, t.request_id, t.call_id, t.ended_at
        FROM execution_tools t JOIN execution_runs r ON r.run_id = t.run_id
        WHERE t.request_id = $request AND t.call_id = $call;
        """, ("$completion", Guid.NewGuid()), ("$request", call.RequestId), ("$call", call.CallId));
      insert.ExecuteNonQuery();
    }
  }

  // Never append behind a streaming text tail: publication and input delivery are separate.
  private void PublishCompletions(SqliteTransaction transaction, Guid sessionId, long revision, Guid? unstartedRequest = null) {
    using SqliteCommand publish = database.Command(transaction, """
      INSERT INTO execution_messages (
          session_id, history_revision, run_id, role, message_id, request_id, created_at, call_id, tool_name)
      SELECT c.session_id, c.history_revision, t.run_id, 3, c.completion_id, c.request_id, c.created_at, c.call_id, t.name
      FROM execution_tool_completions c JOIN execution_tools t ON t.request_id = c.request_id AND t.call_id = c.call_id
      WHERE c.session_id = $session AND c.history_revision = $revision AND c.published = 0
          AND NOT EXISTS (SELECT 1 FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
              WHERE r.session_id = $session AND r.history_revision = $revision AND q.outcome IS NULL
                  AND ($claim IS NULL OR q.request_id <> $claim OR q.attempt <> 1))
      ORDER BY c.ordinal;
      UPDATE execution_tool_completions SET published = 1
      WHERE session_id = $session AND history_revision = $revision AND published = 0
          AND EXISTS (SELECT 1 FROM execution_messages m WHERE m.role = 3 AND m.message_id = completion_id);
      """, ("$session", sessionId), ("$revision", revision), ("$claim", unstartedRequest));
    publish.ExecuteNonQuery();
  }

  internal SessionExecutionResult? ReadSettlement(Guid sessionId, Guid executionId) {
    return database.Guarded<SessionExecutionResult?>(() => {
      using SqliteCommand read = database.Command(null, """
        SELECT r.outcome,
            (SELECT substr(q.error, 1, 1024) FROM execution_requests q
                WHERE q.run_id = r.run_id AND q.outcome = 2 ORDER BY q.request_index DESC LIMIT 1),
            substr(r.error, 1, 1024)
        FROM execution_runs r WHERE r.session_id = $session AND r.run_id = $run;
        """, ("$session", sessionId), ("$run", executionId));
      using SqliteDataReader reader = read.ExecuteReader();
      if (!reader.Read() || reader.IsDBNull(0))
        return null;

      ExecutionOutcome outcome = (ExecutionOutcome)reader.GetInt32(0);
      return new(executionId, outcome, ReadSettlementError(reader, outcome, 1, 2));
    });
  }

  private static (ExecutionOutcome Outcome, bool AlreadySettled, string? Error)? DecideSettlement(
      SqliteCommand read, ExecutionOutcome driverOutcome) {
    using SqliteDataReader reader = read.ExecuteReader();
    if (!reader.Read())
      return null;

    ExecutionOutcome? executionOutcome = reader.IsDBNull(0) ? null : (ExecutionOutcome)reader.GetInt32(0);
    bool stopRequested = reader.GetInt32(1) != 0;
    ExecutionOutcome? lastOutcome = reader.IsDBNull(2) ? null : (ExecutionOutcome)reader.GetInt32(2);
    bool pendingSubmissions = reader.GetInt32(3) != 0;
    bool missingResults = reader.GetInt32(4) != 0;
    bool needsToolContinuation = reader.GetInt32(5) != 0;
    if (executionOutcome is { } settled)
      return (settled, AlreadySettled: true, ReadSettlementError(reader, settled, 7, 8));

    if (stopRequested)
      return (ExecutionOutcome.Cancelled, AlreadySettled: false, null);

    if (driverOutcome != ExecutionOutcome.Completed)
      return (driverOutcome, AlreadySettled: false,
          ReadSettlementError(reader, driverOutcome, 7, 8));

    if (reader.GetBoolean(6))
      return (driverOutcome, AlreadySettled: false, null);

    if (lastOutcome is not { } outcome)
      return null;

    if (outcome == ExecutionOutcome.Completed && (pendingSubmissions || missingResults || needsToolContinuation))
      return null;

    return (outcome, AlreadySettled: false, ReadSettlementError(reader, outcome, 7, 8));
  }

  private static string? ReadSettlementError(SqliteDataReader reader, ExecutionOutcome outcome,
      int requestErrorIndex, int runErrorIndex) {
    if (outcome != ExecutionOutcome.Failed)
      return null;
    if (!reader.IsDBNull(requestErrorIndex))
      return reader.GetString(requestErrorIndex);
    return reader.IsDBNull(runErrorIndex) ? null : reader.GetString(runErrorIndex);
  }
}
