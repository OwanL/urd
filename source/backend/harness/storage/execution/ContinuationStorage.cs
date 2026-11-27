using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Starts an input-less execution against the exact settled history tail it continues.
internal sealed class ContinuationStorage(Database database, ToolResultCodec toolResults) {
  internal (ContinuationReceipt Receipt, bool New)? Start(Guid sessionId, long historyRevision,
      ContinuationSubmission submission, bool accepting) => database.Write<(ContinuationReceipt Receipt, bool New)?>(transaction => {
    ContinuationReceipt? retained = ReadReceipt(transaction, sessionId, submission.OperationId, submission);
    if (retained is not null)
      return (retained, false);

    Validate(sessionId, submission);
    if (!accepting || submission.ExpectedHistoryRevision != historyRevision)
      return null;

    bool? canContinue = database.QuerySingleOrDefault<bool?>(transaction, """
      SELECT s.history_revision,
          (SELECT r.run_id FROM execution_runs r
              WHERE r.session_id = s.session_id AND r.history_revision = s.history_revision
              ORDER BY r.ordinal DESC LIMIT 1) AS latest_run_id,
          (SELECT r.outcome FROM execution_runs r
              WHERE r.session_id = s.session_id AND r.history_revision = s.history_revision
              ORDER BY r.ordinal DESC LIMIT 1) AS latest_outcome,
          EXISTS (SELECT 1 FROM execution_runs r WHERE r.session_id = s.session_id AND r.outcome IS NULL
              AND r.queued_after_compaction IS NULL) AS has_active_execution,
          EXISTS (SELECT 1 FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
              WHERE r.session_id = s.session_id AND r.history_revision = s.history_revision
                  AND q.attempt = 2) AS has_request,
          (EXISTS (SELECT 1 FROM execution_messages m JOIN execution_requests q ON q.request_id = m.request_id
                  JOIN execution_runs r ON r.run_id = q.run_id
              WHERE m.session_id = s.session_id AND m.history_revision = s.history_revision AND m.role = 0
                  AND r.history_revision = s.history_revision AND q.attempt = 2)
              OR EXISTS (SELECT 1 FROM session_compactions c
                  WHERE c.session_id = s.session_id AND c.history_revision = s.history_revision)) AS has_context
      FROM sessions s WHERE s.session_id = $session;
      """, row => row.Long("history_revision") == submission.ExpectedHistoryRevision
          && row.NullableGuid("latest_run_id") == submission.ExpectedLastExecutionId
          && row.NullableInt("latest_outcome") is not null && !row.Bool("has_active_execution")
          && row.Bool("has_request") && row.Bool("has_context"), ("$session", sessionId));
    if (canContinue is null)
      throw new KeyNotFoundException($"Session {sessionId} does not exist.");
    if (!canContinue.Value)
      return null;

    Guid? omittedRequestId = ReadFinalFailedAssistant(transaction, sessionId, historyRevision);
    database.Execute(transaction, """
      INSERT INTO execution_runs (
          session_id, run_id, history_revision, kind, llm_selection_json,
          continuation_expected_history_revision, continuation_expected_last_execution_id,
          continuation_omitted_request_id, working_started_at)
      VALUES ($session, $operation, $revision, $kind, $selection, $expectedRevision, $expectedLast, $omitted, $started);
      """, ("$session", sessionId), ("$operation", submission.OperationId),
        ("$revision", submission.ExpectedHistoryRevision), ("$kind", (int)ExecutionRunKind.Continuation),
        ("$selection", LlmSelectionJson.Write(submission.LlmSelection)),
        ("$expectedRevision", submission.ExpectedHistoryRevision),
        ("$expectedLast", submission.ExpectedLastExecutionId), ("$omitted", omittedRequestId),
        ("$started", StorageText.UtcTimestamp()));
    return (new ContinuationReceipt(sessionId, submission.OperationId, submission.OperationId), true);
  });

  internal ContinuationReceipt? ReadReceipt(Guid sessionId, Guid operationId) => database.Read(transaction =>
      ReadReceipt(transaction, sessionId, operationId, submission: null));

  private ContinuationReceipt? ReadReceipt(SqliteTransaction transaction, Guid sessionId, Guid operationId,
      ContinuationSubmission? submission) {
    return database.QuerySingleOrDefault<ContinuationReceipt?>(transaction, """
      SELECT session_id, kind, continuation_expected_history_revision,
          continuation_expected_last_execution_id, llm_selection_json
      FROM execution_runs WHERE run_id = $operation;
      """, row => {
        if (row.Guid("session_id") != sessionId || row.Int("kind") != (int)ExecutionRunKind.Continuation) {
          if (submission is not null)
            throw new UserMessageConflictException(operationId);
          return null;
        }

        if (submission is not null && (row.NullableLong("continuation_expected_history_revision") != submission.ExpectedHistoryRevision
            || row.NullableGuid("continuation_expected_last_execution_id") != submission.ExpectedLastExecutionId
            || LlmSelectionJson.Read(row.NullableString("llm_selection_json")) != submission.LlmSelection))
          throw new UserMessageConflictException(operationId);

        return new(sessionId, operationId, operationId);
      }, ("$operation", operationId));
  }

  private Guid? ReadFinalFailedAssistant(SqliteTransaction transaction, Guid sessionId, long historyRevision) {
    ModelHistory context = new LlmContextReader(database, toolResults)
        .ReadContext(transaction, sessionId, historyRevision);
    if (context.Messages.Count == 0)
      return null;
    HistoryMessage tail = context.Messages[^1];
    if (tail.Message.Role != ChatRole.Assistant)
      return null;

    return database.QuerySingleOrDefault<Guid?>(transaction, """
      SELECT q.outcome FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
      WHERE q.request_id = $request AND r.session_id = $session AND r.history_revision = $revision;
      """, row => row.NullableInt("outcome") != (int)ExecutionOutcome.Completed
          || tail.Message.Contents.Any(content => content is FunctionCallContent) ? tail.Location.RequestId : null,
        ("$request", tail.Location.RequestId), ("$session", sessionId), ("$revision", historyRevision));
  }

  private static void Validate(Guid sessionId, ContinuationSubmission submission) {
    if (sessionId == Guid.Empty || submission.OperationId == Guid.Empty
        || submission.ExpectedLastExecutionId == Guid.Empty || submission.LlmSelection is null)
      throw new ArgumentException("Continuation requires operation, tail and model identities.", nameof(submission));
    ArgumentOutOfRangeException.ThrowIfNegative(submission.ExpectedHistoryRevision);
    submission.LlmSelection.Validate();
  }
}
