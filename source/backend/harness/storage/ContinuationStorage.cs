using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Compaction;
using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Admits an input-less execution against the exact settled history tail it continues.
internal sealed class ContinuationStorage(Database database, ToolResultCodec toolResults) {
  internal (ContinuationReceipt Receipt, bool New)? Admit(Guid sessionId, long historyRevision,
      ContinuationSubmission submission, bool accepting) => database.Write<(ContinuationReceipt Receipt, bool New)?>(transaction => {
    ContinuationReceipt? retained = ReadReceipt(transaction, sessionId, submission.OperationId, submission);
    if (retained is not null)
      return (retained, false);

    Validate(sessionId, submission);
    if (!accepting || submission.ExpectedHistoryRevision != historyRevision)
      return null;

    using (SqliteCommand current = database.Command(transaction, """
      SELECT s.history_revision,
          (SELECT r.run_id FROM execution_runs r
              WHERE r.session_id = s.session_id AND r.history_revision = s.history_revision
              ORDER BY r.ordinal DESC LIMIT 1),
          (SELECT r.outcome FROM execution_runs r
              WHERE r.session_id = s.session_id AND r.history_revision = s.history_revision
              ORDER BY r.ordinal DESC LIMIT 1),
          EXISTS (SELECT 1 FROM execution_runs r WHERE r.session_id = s.session_id AND r.outcome IS NULL),
          EXISTS (SELECT 1 FROM execution_runs r JOIN execution_requests q ON q.run_id = r.run_id
              WHERE r.session_id = s.session_id AND r.history_revision = s.history_revision
                  AND q.attempt = 2),
          (EXISTS (SELECT 1 FROM execution_messages m JOIN execution_requests q ON q.request_id = m.request_id
                  JOIN execution_runs r ON r.run_id = q.run_id
              WHERE m.session_id = s.session_id AND m.history_revision = s.history_revision AND m.role = 0
                  AND r.history_revision = s.history_revision AND q.attempt = 2)
              OR EXISTS (SELECT 1 FROM session_compactions c
                  WHERE c.session_id = s.session_id AND c.history_revision = s.history_revision))
      FROM sessions s WHERE s.session_id = $session;
      """, ("$session", sessionId)))
    using (SqliteDataReader reader = current.ExecuteReader()) {
      if (!reader.Read())
        throw new KeyNotFoundException($"Session {sessionId} does not exist.");
      Guid? latestExecutionId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
      bool latestSettled = !reader.IsDBNull(2);
      bool hasActiveExecution = reader.GetBoolean(3);
      bool hasAttemptedContext = reader.GetBoolean(4) && reader.GetBoolean(5);
      if (reader.GetInt64(0) != submission.ExpectedHistoryRevision
          || latestExecutionId != submission.ExpectedLastExecutionId || !latestSettled
          || hasActiveExecution || !hasAttemptedContext)
        return null;
    }

    Guid? omittedRequestId = ReadFinalFailedAssistant(transaction, sessionId, historyRevision);
    using SqliteCommand insert = database.Command(transaction, """
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
    insert.ExecuteNonQuery();
    return (new ContinuationReceipt(sessionId, submission.OperationId, submission.OperationId), true);
  });

  internal ContinuationReceipt? ReadReceipt(Guid sessionId, Guid operationId) => database.Read(transaction =>
      ReadReceipt(transaction, sessionId, operationId, submission: null));

  private ContinuationReceipt? ReadReceipt(SqliteTransaction transaction, Guid sessionId, Guid operationId,
      ContinuationSubmission? submission) {
    using SqliteCommand read = database.Command(transaction, """
      SELECT session_id, kind, continuation_expected_history_revision,
          continuation_expected_last_execution_id, llm_selection_json
      FROM execution_runs WHERE run_id = $operation;
      """, ("$operation", operationId));
    using SqliteDataReader reader = read.ExecuteReader();
    if (!reader.Read())
      return null;

    Guid retainedSession = reader.GetGuid(0);
    if (retainedSession != sessionId || reader.GetInt32(1) != (int)ExecutionRunKind.Continuation) {
      if (submission is not null)
        throw new UserMessageConflictException(operationId);
      return null;
    }

    if (submission is not null && (reader.IsDBNull(2)
        || reader.GetInt64(2) != submission.ExpectedHistoryRevision
        || reader.IsDBNull(3) || reader.GetGuid(3) != submission.ExpectedLastExecutionId
        || LlmSelectionJson.Read(reader.IsDBNull(4) ? null : reader.GetString(4)) != submission.LlmSelection))
      throw new UserMessageConflictException(operationId);

    return new(sessionId, operationId, operationId);
  }

  private Guid? ReadFinalFailedAssistant(SqliteTransaction transaction, Guid sessionId, long historyRevision) {
    ModelHistory context = new LlmContextReader(database, toolResults)
        .ReadContext(transaction, sessionId, historyRevision);
    if (context.Messages.Count == 0)
      return null;
    HistoryMessage tail = context.Messages[^1];
    if (tail.Message.Role != ChatRole.Assistant)
      return null;

    using SqliteCommand request = database.Command(transaction, """
      SELECT q.outcome FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
      WHERE q.request_id = $request AND r.session_id = $session AND r.history_revision = $revision;
      """, ("$request", tail.Location.RequestId), ("$session", sessionId), ("$revision", historyRevision));
    using SqliteDataReader reader = request.ExecuteReader();
    if (!reader.Read())
      return null;
    return reader.IsDBNull(0) || reader.GetInt32(0) != (int)ExecutionOutcome.Completed
        || tail.Message.Contents.Any(content => content is FunctionCallContent)
        ? tail.Location.RequestId : null;
  }

  private static void Validate(Guid sessionId, ContinuationSubmission submission) {
    if (sessionId == Guid.Empty || submission.OperationId == Guid.Empty
        || submission.ExpectedLastExecutionId == Guid.Empty || submission.LlmSelection is null)
      throw new ArgumentException("Continuation requires operation, tail and model identities.", nameof(submission));
    ArgumentOutOfRangeException.ThrowIfNegative(submission.ExpectedHistoryRevision);
    submission.LlmSelection.Validate();
  }
}
