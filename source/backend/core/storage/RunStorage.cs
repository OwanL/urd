using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Claims run work and retains stop and settlement facts. The caller joins request cleanup first.
internal sealed class RunStorage(Database database) {
  internal LlmRequest? ClaimNextRequest(Guid sessionId, Guid runId, long historyRevision) {
    return database.Write<LlmRequest?>(transaction => {
      Guid requestId = Guid.NewGuid();
      using SqliteCommand claim = database.Command(transaction, """
        INSERT INTO execution_requests (request_id, run_id, request_index)
        SELECT $request, r.run_id,
            COALESCE((SELECT MAX(request_index) + 1 FROM execution_requests WHERE run_id = r.run_id), 0)
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
          ("$session", sessionId), ("$run", runId), ("$revision", historyRevision), ("$request", requestId));
      if (claim.ExecuteNonQuery() == 0)
        return null;

      using SqliteCommand assign = database.Command(transaction, """
        UPDATE execution_inputs SET request_id = $request
        WHERE run_id = $run AND request_id IS NULL AND outcome IS NULL;
        """, ("$run", runId), ("$request", requestId));
      assign.ExecuteNonQuery();
      return new LlmRequest(requestId,
          new RequestContextReader(database).Read(transaction, sessionId, runId, historyRevision, requestId));
    });
  }

  internal StopReceipt RequestStop(Guid sessionId, Guid runId, bool ownsRun) {
    return database.Write(transaction => {
      using SqliteCommand read = database.Command(transaction,
          "SELECT outcome FROM execution_runs WHERE session_id = $session AND run_id = $run;",
          ("$session", sessionId), ("$run", runId));
      object? outcome = read.ExecuteScalar();
      if (outcome is null)
        return new StopReceipt(runId, StopStatus.NotFound);

      if (outcome is not DBNull)
        return new StopReceipt(runId, StopStatus.Settled, (ExecutionOutcome)(long)outcome);

      if (!ownsRun)
        return new StopReceipt(runId, StopStatus.Unavailable);

      using SqliteCommand command = database.Command(transaction, """
        UPDATE execution_runs SET stopping = 1
        WHERE session_id = $session AND run_id = $run AND stopping = 0 AND outcome IS NULL;
        UPDATE execution_inputs SET outcome = 1
        WHERE session_id = $session AND run_id = $run AND request_id IS NULL AND outcome IS NULL
            AND EXISTS (SELECT 1 FROM execution_runs WHERE run_id = $run AND stopping = 1 AND outcome IS NULL);
        """, ("$session", sessionId), ("$run", runId));
      command.ExecuteNonQuery();
      return new StopReceipt(runId, StopStatus.Requested);
    });
  }

  // The caller has joined all owned work, including cancellation callbacks, before settlement.
  internal ExecutionResult? TrySettle(Guid sessionId, Guid runId, ExecutionOutcome driverOutcome) {
    if (!Enum.IsDefined(driverOutcome))
      throw new ArgumentOutOfRangeException(nameof(driverOutcome));

    return database.Write<ExecutionResult?>(transaction => {
      using SqliteCommand read = database.Command(transaction, """
        SELECT r.outcome, r.stopping,
            (SELECT q.outcome FROM execution_requests q WHERE q.run_id = r.run_id ORDER BY q.request_index DESC LIMIT 1),
            EXISTS (SELECT 1 FROM execution_inputs i WHERE i.run_id = r.run_id AND i.request_id IS NULL AND i.outcome IS NULL),
            EXISTS (SELECT 1 FROM execution_tools t WHERE t.run_id = r.run_id AND (t.outcome IS NULL OR t.result_json IS NULL)),
            EXISTS (SELECT 1 FROM execution_tools t JOIN execution_requests q ON q.request_id = t.request_id
                WHERE q.run_id = r.run_id AND q.request_index =
                    (SELECT MAX(request_index) FROM execution_requests WHERE run_id = r.run_id))
        FROM execution_runs r WHERE r.session_id = $session AND r.run_id = $run;
        """, ("$session", sessionId), ("$run", runId));
      Settlement? settlement = DecideSettlement(read, driverOutcome);
      if (settlement is null)
        return null;

      if (settlement.AlreadySettled)
        return new(runId, settlement.Outcome);

      using SqliteCommand settle = database.Command(transaction, """
        UPDATE execution_requests SET outcome = $outcome WHERE run_id = $run AND outcome IS NULL;
        UPDATE execution_inputs SET outcome = $outcome WHERE run_id = $run AND outcome IS NULL;
        UPDATE execution_tools SET outcome = $toolOutcome,
            error = CASE WHEN started = 0 THEN 'Tool was not executed.' ELSE 'Tool result unavailable; effects may have occurred.' END,
            result_json = CASE WHEN started = 0 THEN '{"error":"Tool was not executed."}'
                ELSE '{"error":"Tool result unavailable; effects may have occurred."}' END
        WHERE run_id = $run AND result_json IS NULL;
        UPDATE execution_runs SET outcome = $outcome WHERE session_id = $session AND run_id = $run;
        """,
          ("$session", sessionId), ("$run", runId), ("$outcome", (int)settlement.Outcome),
          ("$toolOutcome", (int)(settlement.Outcome == ExecutionOutcome.Cancelled
              ? ExecutionOutcome.Cancelled : ExecutionOutcome.Failed)));
      settle.ExecuteNonQuery();
      return new(runId, settlement.Outcome);
    });
  }

  internal ExecutionResult? ReadSettlement(Guid sessionId, Guid runId) {
    return database.Guarded<ExecutionResult?>(() => {
      using SqliteCommand read = database.Command(null,
          "SELECT outcome FROM execution_runs WHERE session_id = $session AND run_id = $run;",
          ("$session", sessionId), ("$run", runId));
      object? outcome = read.ExecuteScalar();
      return outcome is null or DBNull ? null : new(runId, (ExecutionOutcome)(long)outcome);
    });
  }

  private static Settlement? DecideSettlement(SqliteCommand read, ExecutionOutcome driverOutcome) {
    using SqliteDataReader reader = read.ExecuteReader();
    if (!reader.Read())
      return null;

    ExecutionOutcome? runOutcome = reader.IsDBNull(0) ? null : (ExecutionOutcome)reader.GetInt32(0);
    bool stopRequested = reader.GetInt32(1) != 0;
    ExecutionOutcome? lastOutcome = reader.IsDBNull(2) ? null : (ExecutionOutcome)reader.GetInt32(2);
    bool pendingInputs = reader.GetInt32(3) != 0;
    bool missingResults = reader.GetInt32(4) != 0;
    bool needsToolContinuation = reader.GetInt32(5) != 0;
    if (runOutcome is { } settled)
      return new(settled, AlreadySettled: true);

    if (stopRequested)
      return new(ExecutionOutcome.Cancelled, AlreadySettled: false);

    if (driverOutcome != ExecutionOutcome.Completed)
      return new(driverOutcome, AlreadySettled: false);

    if (lastOutcome is not { } outcome)
      return null;

    if (outcome == ExecutionOutcome.Completed && (pendingInputs || missingResults || needsToolContinuation))
      return null;

    return new(outcome, AlreadySettled: false);
  }

  private sealed record Settlement(ExecutionOutcome Outcome, bool AlreadySettled);
}
