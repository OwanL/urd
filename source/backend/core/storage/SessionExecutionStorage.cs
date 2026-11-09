using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Claims execution work and retains stop and settlement facts. The caller joins request cleanup first.
internal sealed class SessionExecutionStorage(Database database) {
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
                AND (t.outcome IS NULL OR t.result_json IS NULL))
            AND (EXISTS (SELECT 1 FROM execution_inputs i WHERE i.run_id = r.run_id
                    AND i.request_id IS NULL AND i.outcome IS NULL)
                OR EXISTS (SELECT 1 FROM execution_tools t JOIN execution_requests q ON q.request_id = t.request_id
                    WHERE q.run_id = r.run_id AND q.request_index =
                        (SELECT MAX(request_index) FROM execution_requests WHERE run_id = r.run_id)));
        """,
          ("$session", sessionId), ("$run", executionId), ("$revision", historyRevision), ("$request", requestId));
      if (claim.ExecuteNonQuery() == 0)
        return null;

      using SqliteCommand assign = database.Command(transaction, """
        UPDATE execution_inputs SET request_id = $request
        WHERE run_id = $run AND request_id IS NULL AND outcome IS NULL;
        """, ("$run", executionId), ("$request", requestId));
      assign.ExecuteNonQuery();
      return new LlmRequest(requestId,
          new LlmContextReader(database).Read(transaction, sessionId, executionId, historyRevision, requestId));
    });
  }

  // The marker and its user entries share a transaction. A repeat hook never allocates
  // entries again, and the stopped/settled/revision checks also apply to repeats.
  internal bool StartRequest(Guid sessionId, Guid executionId, long historyRevision, Guid requestId) {
    return database.Write(transaction => {
      using SqliteCommand mark = database.Command(transaction, """
        UPDATE execution_requests SET attempt = 2
        WHERE request_id = $request AND run_id = $run AND outcome IS NULL AND attempt IN (1, 2)
            AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
                WHERE r.run_id = $run AND r.session_id = $session AND r.history_revision = $revision
                    AND s.history_revision = $revision AND r.stopping = 0 AND r.outcome IS NULL);
        """, ("$session", sessionId), ("$run", executionId), ("$revision", historyRevision), ("$request", requestId));
      if (mark.ExecuteNonQuery() == 0)
        return false;

      using SqliteCommand place = database.Command(transaction, """
        INSERT INTO execution_messages (
            session_id, history_revision, run_id, role, message_id, input_id, request_id, created_at)
        SELECT i.session_id, $revision, i.run_id, 0, i.input_id, i.input_id, $request, $created
        FROM execution_inputs i WHERE i.run_id = $run AND i.request_id = $request
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
            EXISTS (SELECT 1 FROM execution_inputs i WHERE i.run_id = r.run_id AND i.request_id IS NULL AND i.outcome IS NULL),
            EXISTS (SELECT 1 FROM execution_tools t WHERE t.run_id = r.run_id AND (t.outcome IS NULL OR t.result_json IS NULL)),
            EXISTS (SELECT 1 FROM execution_tools t JOIN execution_requests q ON q.request_id = t.request_id
                WHERE q.run_id = r.run_id AND q.request_index =
                    (SELECT MAX(request_index) FROM execution_requests WHERE run_id = r.run_id))
        FROM execution_runs r WHERE r.session_id = $session AND r.run_id = $run;
        """, ("$session", sessionId), ("$run", executionId));
      if (DecideSettlement(read, driverOutcome) is not { } settlement)
        return null;

      if (settlement.AlreadySettled)
        return new(executionId, settlement.Outcome);

      using SqliteCommand settle = database.Command(transaction, """
        UPDATE execution_requests SET outcome = $outcome WHERE run_id = $run AND outcome IS NULL;
        UPDATE execution_inputs SET outcome = $outcome,
            not_sent = CASE WHEN (request_id IS NULL OR EXISTS (
                SELECT 1 FROM execution_requests q WHERE q.request_id = execution_inputs.request_id AND q.attempt = 1))
                AND NOT EXISTS (SELECT 1 FROM execution_messages m WHERE m.run_id = execution_inputs.run_id
                    AND m.role = 0 AND m.input_id = execution_inputs.input_id) THEN 1 ELSE 0 END
        WHERE run_id = $run AND outcome IS NULL;
        UPDATE execution_tools SET outcome = $toolOutcome,
            error = CASE WHEN started = 0 THEN 'Tool was not executed.' ELSE 'Tool result unavailable; effects may have occurred.' END,
            result_json = CASE WHEN started = 0 THEN '{"error":"Tool was not executed."}'
                ELSE '{"error":"Tool result unavailable; effects may have occurred."}' END
        WHERE run_id = $run AND result_json IS NULL;
        UPDATE execution_runs SET outcome = $outcome WHERE session_id = $session AND run_id = $run;
        """,
          ("$session", sessionId), ("$run", executionId), ("$outcome", (int)settlement.Outcome),
          ("$toolOutcome", (int)(settlement.Outcome == ExecutionOutcome.Cancelled
              ? ExecutionOutcome.Cancelled : ExecutionOutcome.Failed)));
      settle.ExecuteNonQuery();
      return new(executionId, settlement.Outcome);
    });
  }

  internal SessionExecutionResult? ReadSettlement(Guid sessionId, Guid executionId) {
    return database.Guarded<SessionExecutionResult?>(() => {
      using SqliteCommand read = database.Command(null,
          "SELECT outcome FROM execution_runs WHERE session_id = $session AND run_id = $run;",
          ("$session", sessionId), ("$run", executionId));
      object? outcome = read.ExecuteScalar();
      return outcome is null or DBNull ? null : new(executionId, (ExecutionOutcome)(long)outcome);
    });
  }

  private static (ExecutionOutcome Outcome, bool AlreadySettled)? DecideSettlement(
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
      return (settled, AlreadySettled: true);

    if (stopRequested)
      return (ExecutionOutcome.Cancelled, AlreadySettled: false);

    if (driverOutcome != ExecutionOutcome.Completed)
      return (driverOutcome, AlreadySettled: false);

    if (lastOutcome is not { } outcome)
      return null;

    if (outcome == ExecutionOutcome.Completed && (pendingSubmissions || missingResults || needsToolContinuation))
      return null;

    return (outcome, AlreadySettled: false);
  }
}
